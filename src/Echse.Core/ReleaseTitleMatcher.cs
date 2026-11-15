using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Echse.Core;

/// <summary>Compares game titles with release names such as "Portal.v1.0.2.Complete-GOG".</summary>
public static class ReleaseTitleMatcher
{
    public const int LikelyScore = 85;

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".iso", ".exe", ".tar", ".gz", ".bin", ".msi"
    };

    // Words that describe a release rather than a different game, e.g. "Portal.Complete-GOG".
    private static readonly HashSet<string> ReleaseWords = new(StringComparer.Ordinal)
    {
        "GOG", "STEAM", "REPACK", "RIP", "MULTI", "PROPER", "COMPLETE", "EDITION", "GOTY", "DELUXE",
        "ULTIMATE", "DEFINITIVE", "INCL", "INCLUDING", "UPDATE", "UPDATES",
        "DLC", "DLCS", "BUILD", "VERSION", "WIN", "WIN32", "WIN64", "WINDOWS", "X64", "X86", "PC",
        "PORTABLE", "SETUP", "INSTALLER", "ISO", "ENG", "EN", "DE", "GER", "GERMAN", "ENGLISH"
    };

    private static readonly Regex VersionPattern =
        new(@"\bv?\d+(?:\.\d+)+[a-z]?\b|\bv\d+[a-z]?\b|\bbuild\s*\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingTagPattern =
        new(@"^\s*(?:\[[^\]]*\]|\([^)]*\)|\{[^}]*\})\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// 100 = exact title, 95 = title plus release words only, 85 = title followed by a release word,
    /// 50 = title followed by unknown words, 0 = different game (including sequels like "Portal 2").
    /// </summary>
    public static int Score(string gameTitle, string releaseName)
    {
        var title = string.Concat(Tokenize(gameTitle, stripReleaseInfo: false));
        if (title.Length == 0) return 0;
        var tokens = Tokenize(releaseName, stripReleaseInfo: true);
        var joined = "";
        for (var index = 0; index < tokens.Count; index++)
        {
            joined += tokens[index];
            if (joined.Length > title.Length) return 0;
            if (joined.Length < title.Length) continue;
            if (joined != title) return 0;
            var rest = tokens.Skip(index + 1).ToList();
            if (rest.Count == 0) return 100;
            var next = rest[0];
            if (next.All(char.IsDigit) && !(next.Length == 4 && next[0] is '1' or '2')) return 0;
            if (next is "II" or "III" or "IV" or "V" or "VI" or "VII" or "VIII" or "IX" or "X") return 0;
            return rest.All(IsReleaseWord) ? 95 : IsReleaseWord(next) ? LikelyScore : 50;
        }
        return 0;
    }

    private static bool IsReleaseWord(string token) =>
        ReleaseWords.Contains(token) || token == "VERSIONTAG" || token.StartsWith("MULTI", StringComparison.Ordinal) ||
        token.Length == 4 && token.All(char.IsDigit) && token[0] is '1' or '2';

    private static List<string> Tokenize(string value, bool stripReleaseInfo)
    {
        var name = value.Replace('\\', '/').Split('/').Last();
        if (stripReleaseInfo)
        {
            var extension = Path.GetExtension(name);
            if (FileExtensions.Contains(extension)) name = name[..^extension.Length];
            while (LeadingTagPattern.Match(name) is { Success: true, Length: > 0 } tag && tag.Length < name.Length)
                name = name[tag.Length..];
            name = VersionPattern.Replace(name, " VERSIONTAG ");
        }
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark ||
                character is '\'' or '’' or ':' or '™' or '®')
                continue;
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToUpperInvariant(character));
                continue;
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
