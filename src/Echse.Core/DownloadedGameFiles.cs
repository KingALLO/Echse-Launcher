using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Echse.Core;

public sealed record InstallationFiles(IReadOnlyList<string> Installers, IReadOnlyList<string> Executables);

public static class DownloadedGameFiles
{
    private static readonly Regex InstallerName = new(@"(?:^|[-_. ])(setup|install|installer)(?:$|[-_.\d ])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UtilityName = new(
        @"^(unins|uninstall|crash|unitycrash|dxsetup|vcredist|vc_redist|dotnet|ue4prereq|ueprereq|redist|support|config)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> UtilityDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "redist", "redistributables", "DirectX", "Support", "__MACOSX"
    };

    public static string FolderName(string title)
    {
        var name = Regex.Replace(title, @"[^\p{L}\p{N}]+", "_").Trim('_');
        if (name.Length == 0) throw new ArgumentException("Der Spieltitel enthält keinen gültigen Ordnernamen.");
        name = name[..Math.Min(name.Length, 80)];
        return IsDeviceName(name) ? "Game_" + name : name;
    }

    public static string CreateGameDirectory(string root, string title)
    {
        root = GameInstallation.ValidateDirectory(root);
        RejectLinks(root);
        var name = FolderName(title);
        var path = Path.Combine(root, name);
        for (var index = 2; Directory.Exists(path) || File.Exists(path); index++)
            path = Path.Combine(root, name + "_" + index);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "Download"));
        return path;
    }

    public static void ValidateTorrent(InstallationDownload download, TorrentStatus torrent)
    {
        if (!download.Hash.Equals(torrent.Hash, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(torrent.SavePath) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(torrent.SavePath)).Equals(
                Path.GetFullPath(download.DownloadDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Torrent liegt nicht im zugeordneten Echse-Downloadordner. In qBittorrent den Speicherpfad prüfen; fremde Ordner werden nicht verarbeitet.");
        if (torrent.State is "error" or "missingFiles" or "unknown")
            throw new IOException("qBittorrent meldet einen Downloadfehler: " + torrent.State);
    }

    public static Task<InstallationFiles> DiscoverAsync(string directory, CancellationToken token = default) =>
        Task.Run(() =>
        {
            var installers = new List<string>();
            var executables = new List<string>();
            foreach (var file in EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                var parts = Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar);
                var name = Path.GetFileNameWithoutExtension(file);
                if (parts.SkipLast(1).Any(part => UtilityDirectories.Contains(part) ||
                    part.StartsWith(".echse-unpack-", StringComparison.OrdinalIgnoreCase)) || UtilityName.IsMatch(name)) continue;
                var extension = Path.GetExtension(file);
                if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && InstallerName.IsMatch(name))
                    installers.Add(file);
                else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    executables.Add(file);
            }
            return new InstallationFiles(installers.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                executables.Order(StringComparer.OrdinalIgnoreCase).ToList());
        }, token);

    public static Task<string?> ExtractAsync(InstallationDownload download, CancellationToken token = default) =>
        Task.Run(() =>
        {
            var archives = EnumerateFiles(download.DownloadDirectory).Where(IsArchive)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (archives.Count == 0) return null;
            RejectLinks(GameInstallation.ValidateDirectory(download.Directory));
            var id = Guid.NewGuid().ToString("N");
            var output = Path.Combine(download.Directory, ".echse-unpack-" + id);
            Directory.CreateDirectory(output);
            try
            {
                var queue = new Queue<(string Path, int Depth)>(archives.Select(path => (path, 0)));
                var progress = new ExtractionProgress(token);
                var fileCount = 0;
                var archiveCount = 0;
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (queue.TryDequeue(out var next))
                {
                    token.ThrowIfCancellationRequested();
                    if (processed.Contains(next.Path)) continue;
                    if (++archiveCount > 32 || next.Depth > 3)
                        throw new InvalidDataException("Zu viele oder zu tief verschachtelte Archive. Bitte manuell entpacken.");
                    var destination = Path.Combine(output, archiveCount.ToString("D2") + "_" + FolderName(Path.GetFileName(next.Path)));
                    Directory.CreateDirectory(destination);
                    var parts = ArchiveFactory.GetFileParts(next.Path).ToArray();
                    foreach (var part in parts)
                    {
                        if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Verknüpfte Archivdateien werden nicht automatisch verarbeitet.");
                        processed.Add(part);
                    }
                    var options = ReaderOptions.ForFilePath.WithProgress(progress);
                    using var archive = ArchiveFactory.OpenArchive(parts.Select(part => new FileInfo(part)).ToArray(), options);
                    using var reader = archive.IsSolid || archive.Type == ArchiveType.SevenZip
                        ? archive.ExtractAllEntries() : ReaderFactory.OpenReader(next.Path, options);
                    progress.Reader = reader;
                    while (reader.MoveToNextEntry())
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = reader.Entry;
                        if (++fileCount > 100_000 || entry.IsEncrypted || !string.IsNullOrEmpty(entry.LinkTarget))
                            throw new InvalidDataException("Das Archiv enthält zu viele Einträge, verschlüsselte Dateien oder Verknüpfungen. Bitte manuell prüfen.");
                        var target = ArchiveTarget(destination, entry.Key ?? "");
                        if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        try
                        {
                            reader.WriteEntryToFile(target, new ExtractionOptions { Overwrite = false, CheckCrc = true });
                        }
                        catch { reader.Cancel(); throw; }
                        var length = new FileInfo(target).Length;
                        if (entry.Size >= 0 && length != entry.Size)
                            throw new InvalidDataException("Eine Archivdatei wurde nicht vollständig entpackt.");
                        progress.CompletedBytes = checked(progress.CompletedBytes + length);
                        if (IsArchive(target)) queue.Enqueue((target, next.Depth + 1));
                    }
                }
                var completed = Path.Combine(download.Directory, "Entpackt_" + id);
                Directory.Move(output, completed);
                return completed;
            }
            catch (Exception exception)
            {
                // Only this operation's newly created directory is removed, never torrent data.
                try { Directory.Delete(output, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    throw new AggregateException("Entpacken fehlgeschlagen; der temporäre Ordner konnte nicht entfernt werden.", exception, cleanup);
                }
                throw;
            }
        }, token);

    // Synchronous progress lets us stop the reader before it drains an oversized entry on disposal.
    private sealed class ExtractionProgress(CancellationToken token) : IProgress<ProgressReport>
    {
        public IReader? Reader { get; set; }
        public long CompletedBytes { get; set; }

        public void Report(ProgressReport report)
        {
            if (!token.IsCancellationRequested && report.BytesTransferred <= 500L * 1024 * 1024 * 1024 - CompletedBytes)
                return;
            Reader?.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException("Die entpackten Daten überschreiten 500 GiB. Bitte manuell entpacken.");
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        directory = GameInstallation.ValidateDirectory(directory);
        RejectLinks(directory);
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        });
    }

    private static bool IsArchive(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar" ||
        path.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip.001", StringComparison.OrdinalIgnoreCase);

    private static string ArchiveTarget(string root, string key)
    {
        var parts = key.Replace('\\', '/').TrimEnd('/').Split('/');
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.EndsWith(' ') || part.EndsWith('.') || IsDeviceName(part) ||
            part.Any(c => c < 32 || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("Das Archiv enthält einen unsicheren Dateipfad.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ein Archivpfad liegt außerhalb des Zielordners.");
        return path;
    }

    private static bool IsDeviceName(string name) =>
        Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verknüpfte Ordner werden nicht automatisch verarbeitet: " + directory.FullName);
    }
}
