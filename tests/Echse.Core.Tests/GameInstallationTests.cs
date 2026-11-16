using Echse.Core;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace Echse.Core.Tests;

public sealed class GameInstallationTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void DemoAndMissingCurrentSourceCannotReuseAnOldLibraryMagnet()
    {
        var owned = new Game { Id = "owned", SteamAppId = 620, MagnetUri = Magnet };
        Assert.Null(GameInstallation.ResolveDownload(owned, new ConnectionSettings(), [owned]).Source);
        Assert.Null(GameInstallation.ResolveDownload(owned,
            new ConnectionSettings { CatalogUrl = "https://catalog.example/games" }, []).Source);
    }

    [Fact]
    public void DownloadsRequireOneUnambiguousIdentityMatchNotJustTheSameTitle()
    {
        var settings = new ConnectionSettings { CatalogUrl = "https://catalog.example/games" };
        var owned = new Game { Id = "steam:620", SteamAppId = 620, Title = "Portal 2" };
        var download = new Game { Id = "server:portal", SteamAppId = 620, MagnetUri = Magnet };
        Assert.Equal(download, GameInstallation.ResolveDownload(owned, settings, [download]).Source);
        var localDownload = download with { Id = owned.Id, SteamAppId = null };
        Assert.Equal(localDownload, GameInstallation.ResolveDownload(
            owned with { SteamAppId = null }, settings, [localDownload]).Source);
        Assert.Null(GameInstallation.ResolveDownload(owned, settings,
            [download with { SteamAppId = null, Title = "Portal 2" }]).Source);
        Assert.Null(GameInstallation.ResolveDownload(owned, settings,
            [download, download with { Id = "another-edition" }]).Source);
        Assert.Null(GameInstallation.ResolveDownload(owned, settings,
            [download with { Id = owned.Id, SteamAppId = 400 }]).Source);
    }

    [Fact]
    public async Task StorageCountsNestedAndHiddenFilesOnlyInsideTheSelectedGameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-storage-" + Guid.NewGuid().ToString("N"));
        var gameFolder = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(gameFolder, "data"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(gameFolder, "game.exe"), new byte[7]);
            var data = Path.Combine(gameFolder, "data", "hidden.bin");
            await File.WriteAllBytesAsync(data, new byte[13]);
            File.SetAttributes(data, FileAttributes.Hidden);
            await File.WriteAllBytesAsync(Path.Combine(root, "other-game.bin"), new byte[100]);
            Assert.Equal(20, await GameInstallation.MeasureDirectoryAsync(gameFolder));
            Assert.Throws<ArgumentException>(() => GameInstallation.ValidateDirectory(Path.GetPathRoot(root)!));
            Assert.Throws<DirectoryNotFoundException>(() =>
                GameInstallation.ValidateDirectory(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OnlyAnExistingLinkedExecutableEnablesPlaying()
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entry = new LibraryEntry { InstallationDirectory = root, ExecutablePath = Path.Combine(root, "game.exe") };
            Assert.False(GameInstallation.IsInstalled(entry));
            await File.WriteAllBytesAsync(entry.ExecutablePath, [0]);
            Assert.True(GameInstallation.IsInstalled(entry));
            entry.ExecutablePath = Path.Combine(root, "readme.txt");
            await File.WriteAllTextAsync(entry.ExecutablePath, "Not a game");
            Assert.False(GameInstallation.IsInstalled(entry));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DownloadAssociationAndPreparationSurviveRestartWithoutChangingLibraryHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-job-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var directory = DownloadedGameFiles.CreateGameDirectory(root, "Half-Life: 1");
            Assert.Equal("Half_Life_1", Path.GetFileName(directory));
            Assert.NotEqual(directory, DownloadedGameFiles.CreateGameDirectory(root, "Half-Life: 1"));
            var repository = new StateRepository(Path.Combine(root, "state"));
            await repository.UpdateAsync(state =>
            {
                state.Settings.DownloadDirectory = root;
                state.Library.Add(new LibraryEntry
                {
                    Game = new Game { Id = "hl", Title = "Half-Life" }, Favorite = true, PlaySeconds = 120,
                    Download = new InstallationDownload
                    {
                        Hash = MagnetLink.GetInfoHash(Magnet), ServerUrl = "http://localhost:8080", Directory = directory,
                        SetupStarted = true, Phase = InstallationPhase.AwaitingExecutable
                    }
                });
            });
            var reloaded = new StateRepository(Path.Combine(root, "state"));
            await reloaded.LoadAsync();
            var state = await reloaded.ReadAsync();
            var entry = Assert.Single(state.Library);
            Assert.Equal(root, state.Settings.DownloadDirectory);
            Assert.Equal(Path.Combine(directory, "Download"), entry.Download!.DownloadDirectory);
            Assert.Equal(InstallationPhase.AwaitingExecutable, entry.Download.Phase);
            Assert.True(entry.Download.SetupStarted);
            Assert.True(entry.Favorite);
            Assert.Equal(120, entry.PlaySeconds);
            Assert.False(GameInstallation.IsInstalled(entry));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("checkingUP", 0, false)]
    [InlineData("moving", 0, false)]
    [InlineData("downloading", 0, false)]
    [InlineData("error", 0, false)]
    [InlineData("pausedUP", 12, false)]
    [InlineData("pausedUP", 0, true)]
    [InlineData("stoppedUP", 0, true)]
    [InlineData("stalledUP", 0, true)]
    public void DoesNotPrepareIncompleteOrBusyTorrents(string state, long remaining, bool expected)
    {
        var job = new InstallationDownload { Hash = MagnetLink.GetInfoHash(Magnet), Directory = Path.GetTempPath() };
        var torrent = new TorrentStatus("Half-Life", 1, state, 0)
        {
            Hash = job.Hash.ToLowerInvariant(), SavePath = job.DownloadDirectory, AmountLeft = remaining
        };
        Assert.Equal(expected, torrent.IsComplete);
        Assert.Throws<InvalidOperationException>(() =>
            DownloadedGameFiles.ValidateTorrent(job, torrent with { SavePath = Path.GetTempPath() }));
        Assert.Throws<InvalidOperationException>(() =>
            DownloadedGameFiles.ValidateTorrent(job, torrent with { Hash = new string('A', 40) }));
        if (state == "error") Assert.Throws<IOException>(() => DownloadedGameFiles.ValidateTorrent(job, torrent));
        else DownloadedGameFiles.ValidateTorrent(job, torrent);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("rar")]
    public async Task ExtractsSupportedArchivesAndDistinguishesSetupsFromGameExecutables(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Download"));
        try
        {
            var archive = Path.Combine(root, "Download", "game." + format);
            if (format == "rar") WriteStoredRar(archive, "bin/hl.exe", "game");
            else
            {
                using var output = File.Create(archive);
                using var writer = format == "7z"
                    ? WriterFactory.OpenWriter(output, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2))
                    : WriterFactory.OpenWriter(output, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate));
                using var data = new MemoryStream(Encoding.UTF8.GetBytes("game"));
                writer.Write("bin/hl.exe", data);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "Download", "setup.exe"), "installer");
            await File.WriteAllTextAsync(Path.Combine(root, "Download", "unins000.exe"), "uninstaller");
            Directory.CreateDirectory(Path.Combine(root, "Download", "_CommonRedist"));
            await File.WriteAllTextAsync(Path.Combine(root, "Download", "_CommonRedist", "other.exe"), "dependency");
            var prepared = await DownloadedGameFiles.ExtractAsync(new InstallationDownload { Directory = root });
            Assert.NotNull(prepared);
            var files = await DownloadedGameFiles.DiscoverAsync(root);
            Assert.EndsWith("setup.exe", Assert.Single(files.Installers));
            var game = Assert.Single(files.Executables);
            Assert.Equal("game", await File.ReadAllTextAsync(game));
            Assert.StartsWith(prepared, game);
            Assert.True(File.Exists(archive));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("..\\outside.exe")]
    [InlineData("/outside.exe")]
    [InlineData("C:\\outside.exe")]
    [InlineData("data.exe:payload")]
    [InlineData("CON.txt")]
    public async Task RejectsUnsafeArchivePathsAndRemovesOnlyItsOwnPartialExtraction(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-unsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Download"));
        try
        {
            var source = Path.Combine(root, "Download", "unsafe.zip");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("good.txt").Open())) writer.Write("good");
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("bad");
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DownloadedGameFiles.ExtractAsync(new InstallationDownload { Directory = root }));
            Assert.True(File.Exists(source));
            Assert.Single(Directory.GetDirectories(root));
            Assert.False(File.Exists(Path.Combine(root, "outside.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExtractedPortableGameCanBeLinkedPersistedAndStarted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "echse-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = new StateRepository(Path.Combine(root, "state"));
        var tracker = new PlayTracker(repository);
        try
        {
            var directory = DownloadedGameFiles.CreateGameDirectory(root, "Test Game");
            var job = new InstallationDownload { Directory = directory, Phase = InstallationPhase.Preparing };
            ZipFile.CreateFromDirectory(Path.Combine(AppContext.BaseDirectory, "GameFixture"),
                Path.Combine(job.DownloadDirectory, "game.zip"));
            job.PreparedDirectory = await DownloadedGameFiles.ExtractAsync(job);
            job.FilesPrepared = true;
            var candidates = await DownloadedGameFiles.DiscoverAsync(directory);
            Assert.Empty(candidates.Installers);
            var executable = Assert.Single(candidates.Executables);
            await repository.UpdateAsync(state =>
            {
                var entry = new LibraryEntry { Game = new Game { Id = "fixture" }, Download = job };
                Assert.False(GameInstallation.IsInstalled(entry));
                Assert.Throws<IOException>(() => GameInstallation.LinkExecutable(entry, executable + ".missing"));
                Assert.Null(entry.ExecutablePath);
                GameInstallation.LinkExecutable(entry, executable);
                state.Library.Add(entry);
            });
            await repository.LoadAsync();
            var linked = Assert.Single((await repository.ReadAsync()).Library);
            Assert.True(GameInstallation.IsInstalled(linked));
            Assert.Equal(InstallationPhase.Installed, linked.Download!.Phase);
            await tracker.LaunchAsync("fixture");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (tracker.IsRunning("fixture")) await Task.Delay(20, timeout.Token);
            var played = (await repository.ReadAsync()).Library.Single();
            Assert.Equal(1, played.LaunchCount);
            Assert.InRange(played.PlaySeconds, 1.8, 8);
        }
        finally
        {
            await tracker.StopAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IncompleteExtractionDirectoriesAreNeverOfferedAsGameExecutables()
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-interrupted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".echse-unpack-interrupted"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".echse-unpack-interrupted", "partial.exe"), "partial");
            var files = await DownloadedGameFiles.DiscoverAsync(root);
            Assert.Empty(files.Executables);
            Assert.Empty(files.Installers);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptArchivePayloadIsNotAcceptedAsAnInstalledGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "echse-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Download"));
        try
        {
            var path = Path.Combine(root, "Download", "game.zip");
            var payload = Encoding.ASCII.GetBytes("game-payload-to-corrupt");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var stream = archive.CreateEntry("game.exe", CompressionLevel.NoCompression).Open())
                stream.Write(payload);
            var bytes = await File.ReadAllBytesAsync(path);
            var offset = bytes.AsSpan().IndexOf(payload);
            Assert.True(offset >= 0);
            bytes[offset] ^= 0xff;
            await File.WriteAllBytesAsync(path, bytes);
            Assert.NotNull(await Record.ExceptionAsync(() =>
                DownloadedGameFiles.ExtractAsync(new InstallationDownload { Directory = root })));
            Assert.Empty((await DownloadedGameFiles.DiscoverAsync(root)).Executables);
            Assert.Single(Directory.GetDirectories(root));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WriteStoredRar(string path, string name, string content)
    {
        var data = Encoding.ASCII.GetBytes(content);
        var filename = Encoding.ASCII.GetBytes(name);
        using var file = new BinaryWriter(File.Create(path));
        file.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00 });
        void Header(Action<BinaryWriter> write)
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true)) write(writer);
            var bytes = buffer.ToArray();
            file.Write((ushort)Crc(bytes));
            file.Write(bytes);
        }
        Header(writer =>
        {
            writer.Write((byte)0x73); writer.Write((ushort)0); writer.Write((ushort)13);
            writer.Write((ushort)0); writer.Write(0u);
        });
        Header(writer =>
        {
            writer.Write((byte)0x74); writer.Write((ushort)0x8000); writer.Write((ushort)(32 + filename.Length));
            writer.Write((uint)data.Length); writer.Write((uint)data.Length); writer.Write((byte)2);
            writer.Write(Crc(data)); writer.Write(0u); writer.Write((byte)20); writer.Write((byte)0x30);
            writer.Write((ushort)filename.Length); writer.Write(0x20u); writer.Write(filename);
        });
        file.Write(data);
        Header(writer => { writer.Write((byte)0x7b); writer.Write((ushort)0); writer.Write((ushort)7); });
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
