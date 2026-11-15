namespace Echse.Core;

public sealed record Game
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Genre { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string? CoverUrl { get; init; }
    public string? PortraitUrl { get; init; }
    public string? MagnetUri { get; init; }
    public int Popularity { get; init; }
    public int? SteamAppId { get; init; }
    public string? BannerUrl { get; init; }
    public string[] Screenshots { get; init; } = [];
    public string[] ScreenshotThumbnails { get; init; } = [];
    public string[] Developers { get; init; } = [];
    public string[] Publishers { get; init; } = [];
    public string[] Platforms { get; init; } = [];
    public string ReleaseDate { get; init; } = "";
    public string PriceLabel { get; init; } = "";
    public string OriginalPriceLabel { get; init; } = "";
    public int DiscountPercent { get; init; }
    public DateTimeOffset? PriceCheckedAt { get; init; }
    public string ContentType { get; init; } = "";
    public SystemRequirements[] Requirements { get; init; } = [];
}

public sealed record SystemRequirements
{
    public string Platform { get; init; } = "";
    public string Minimum { get; init; } = "";
    public string Recommended { get; init; } = "";
}

public sealed class LibraryEntry
{
    public Game Game { get; set; } = new();
    public string? ExecutablePath { get; set; }
    public double PlaySeconds { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
    public int LaunchCount { get; set; }
    public bool Favorite { get; set; }
}

public sealed class UserProfile
{
    public string DisplayName { get; set; } = "Spieler";
    public string Bio { get; set; } = "";
    public string? AvatarPath { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
}

public sealed class ConnectionSettings
{
    public string CatalogUrl { get; set; } = "";
    public string QBitUrl { get; set; } = "http://localhost:8080";
    public string QBitUsername { get; set; } = "";
    public string? ProtectedPassword { get; set; }
}

public sealed class LauncherState
{
    public int Version { get; set; } = 1;
    public UserProfile Profile { get; set; } = new();
    public ConnectionSettings Settings { get; set; } = new();
    public List<LibraryEntry> Library { get; set; } = [];
    public Dictionary<string, int> Views { get; set; } = [];
    public List<Game> CatalogCache { get; set; } = [];
    public DateTimeOffset? CatalogUpdatedAt { get; set; }
    public SteamCatalogSnapshot? SteamCatalog { get; set; }
    public List<Game> SteamDetails { get; set; } = [];
}

public sealed record SteamCatalogSnapshot
{
    public List<Game> Popular { get; init; } = [];
    public List<Game> NewReleases { get; init; } = [];
    public List<Game> Specials { get; init; } = [];
    public List<Game> Upcoming { get; init; } = [];
    public DateTimeOffset FetchedAt { get; init; }

    public IEnumerable<Game> AllGames() =>
        Popular.Concat(NewReleases).Concat(Specials).Concat(Upcoming).DistinctBy(game => game.Id);
}

public sealed record TorrentStatus(string Name, double Progress, string State, long DownloadSpeed);
