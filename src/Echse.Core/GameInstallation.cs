namespace Echse.Core;

public sealed record GameDownload(Game? Source, string? UnavailableReason);

public static class GameInstallation
{
    public static bool IsInstalled(LibraryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.ExecutablePath) &&
        Path.IsPathFullyQualified(entry.ExecutablePath) &&
        Path.GetExtension(entry.ExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(entry.ExecutablePath);

    public static GameDownload ResolveDownload(Game game, ConnectionSettings settings, IReadOnlyList<Game> catalog)
    {
        if (string.IsNullOrWhiteSpace(settings.CatalogUrl))
            return new(null, "Im Demo-Modus sind Installationen deaktiviert. Richte einen eigenen Download-Katalog ein.");
        var matches = catalog.Where(candidate =>
            (candidate.Id == game.Id || game.SteamAppId is int id && candidate.SteamAppId == id) &&
            !(game.SteamAppId is int expected && candidate.SteamAppId is int actual && expected != actual) &&
            !string.IsNullOrWhiteSpace(candidate.MagnetUri)).ToList();
        if (matches.Count == 0)
            return new(null, "Im aktiven Server-Katalog wurde kein Download für dieses Spiel gefunden.");
        if (matches.Count > 1)
            return new(null, "Der Server-Katalog enthält mehrere Downloads für dieses Spiel. Bitte die Zuordnung im Katalog klären.");
        MagnetLink.Validate(matches[0].MagnetUri!);
        return new(matches[0], null);
    }

    public static string ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Bitte einen absoluten Installationsordner auswählen.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bitte den Spielordner auswählen, nicht ein ganzes Laufwerk.");
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException("Der Installationsordner wurde nicht gefunden.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Bitte den tatsächlichen Spielordner statt einer Ordnerverknüpfung auswählen.");
        return path;
    }

    public static Task<long> MeasureDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var root = new DirectoryInfo(ValidateDirectory(directory));
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            long bytes = 0;
            foreach (var file in root.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes = checked(bytes + file.Length);
            }
            return bytes;
        }, cancellationToken);
}
