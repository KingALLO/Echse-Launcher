using System.Text.RegularExpressions;

namespace Echse.Core;

public sealed record GameStorageRequirements(string? Minimum, string? Recommended)
{
    private static readonly Regex StorageLine = new(
        @"(?:^|\n)[ \t]*(?:Speicherplatz|Festplattenspeicher(?:platz)?|Festplatte|Storage|Hard (?:Drive|Disk)(?: Space)?|Disk Space|HDD)\b[ \t]*:?[ \t]*(?:\r?\n[ \t]*(?=\d))?(?<value>[^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex Capacity = new(
        @"\b\d+(?:[.,]\d+)?\s*(?:[KMGT]i?B)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static GameStorageRequirements ForWindows(Game game)
    {
        var requirements = game.Requirements.FirstOrDefault(item =>
            item.Platform.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        return new(Read(requirements?.Minimum), Read(requirements?.Recommended));
    }

    private static string? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match match in StorageLine.Matches(text))
        {
            var value = match.Groups["value"].Value.Trim();
            if (Capacity.IsMatch(value)) return value;
        }
        return null;
    }
}
