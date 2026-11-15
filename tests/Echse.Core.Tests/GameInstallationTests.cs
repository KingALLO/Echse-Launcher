using Echse.Core;
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
}
