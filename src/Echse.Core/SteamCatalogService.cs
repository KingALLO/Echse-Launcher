using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Echse.Core;

public sealed class SteamCatalogService(HttpClient client)
{
    private const string Store = "https://store.steampowered.com/";

    public async Task<SteamCatalogSnapshot> GetHomeAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("api/featuredcategories?cc=de&l=german", cancellationToken);
        var root = document.RootElement;
        return new SteamCatalogSnapshot
        {
            Popular = ReadCategory(root, "top_sellers"),
            NewReleases = ReadCategory(root, "new_releases"),
            Specials = ReadCategory(root, "specials"),
            Upcoming = ReadCategory(root, "coming_soon"),
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new ArgumentException("Bitte einen Suchbegriff mit 1 bis 200 Zeichen angeben.");
        using var document = await GetJsonAsync(
            $"api/storesearch/?term={Uri.EscapeDataString(query.Trim())}&l=german&cc=de", cancellationToken);
        var items = RequiredArray(document.RootElement, "items");
        return items.EnumerateArray()
            .Where(item => String(item, "type") == "app")
            .Select(item => ReadSummary(item, 0)).DistinctBy(game => game.Id).ToList();
    }

    public async Task<Game> GetDetailsAsync(int appId, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        using var document = await GetJsonAsync($"api/appdetails?appids={appId}&l=german&cc=de", cancellationToken);
        if (!document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var result) ||
            !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Steam stellt für dieses Spiel derzeit keine Details bereit (möglicherweise regional nicht verfügbar).");
        var data = result.GetProperty("data");
        if (data.GetProperty("steam_appid").GetInt32() != appId)
            throw new InvalidDataException("Steam lieferte Details für eine andere App-ID.");
        var type = String(data, "type");
        if (type is not ("game" or "dlc" or "demo"))
            throw new InvalidDataException("Dieser Steam-Eintrag ist kein Spiel, DLC oder Demo.");
        var genres = data.TryGetProperty("genres", out var genreItems)
            ? genreItems.EnumerateArray().Select(genre => PlainText(String(genre, "description"))).ToArray()
            : [];
        var header = ImageUrl(data, "header_image");
        var platforms = data.TryGetProperty("platforms", out var platformData)
            ? new[] { "windows", "mac", "linux" }.Where(name =>
                platformData.TryGetProperty(name, out var available) && available.ValueKind == JsonValueKind.True)
                .Select(name => name == "windows" ? "Windows" : name == "mac" ? "macOS" : "Linux").ToArray()
            : [];
        var screenshots = data.TryGetProperty("screenshots", out var images)
            ? images.EnumerateArray().Take(12).Select(image => new
                {
                    Full = ImageUrl(image, "path_full") ?? ImageUrl(image, "path_thumbnail"),
                    Thumbnail = ImageUrl(image, "path_thumbnail") ?? ImageUrl(image, "path_full")
                }).Where(image => image.Full is not null).ToArray()
            : [];
        var hasPrice = data.TryGetProperty("price_overview", out var price);
        var discount = hasPrice && price.TryGetProperty("discount_percent", out var percent) &&
            percent.TryGetInt32(out var amount) ? Math.Clamp(amount, 0, 100) : 0;
        return new Game
        {
            Id = $"steam:{appId}", SteamAppId = appId,
            Title = RequiredName(data),
            Description = PlainText(String(data, "short_description")),
            Genre = genres.FirstOrDefault() ?? "Spiel",
            Tags = genres, CoverUrl = header, PortraitUrl = PortraitUrl(appId),
            BannerUrl = ImageUrl(data, "background_raw") ?? header,
            Screenshots = screenshots.Select(image => image.Full!).ToArray(),
            ScreenshotThumbnails = screenshots.Select(image => image.Thumbnail!).ToArray(),
            Developers = Strings(data, "developers"), Publishers = Strings(data, "publishers"),
            ReleaseDate = data.TryGetProperty("release_date", out var release) ? PlainText(String(release, "date")) : "",
            Platforms = platforms, ContentType = type,
            PriceLabel = data.TryGetProperty("is_free", out var free) && free.ValueKind == JsonValueKind.True
                ? "Kostenlos spielbar"
                : hasPrice ? PriceLabel(price, "final", "final_formatted") : "",
            OriginalPriceLabel = discount > 0 ? PriceLabel(price, "initial", "initial_formatted") : "",
            DiscountPercent = discount,
            PriceCheckedAt = DateTimeOffset.UtcNow,
            Requirements = ReadRequirements(data)
        };
    }

    public async Task<Game?> FindArtworkAsync(string title, CancellationToken cancellationToken = default)
    {
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var matches = (await SearchAsync(title, cancellationToken))
            .Where(game => Normalize(game.Title) == Normalize(title)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string PortraitUrl(int appId) =>
        $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";

    private async Task<JsonDocument> GetJsonAsync(string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(Store + route, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Steam begrenzt gerade die Anfragen. Bitte versuche es in einigen Minuten erneut.");
        response.EnsureSuccessStatusCode();
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Die Steam-Antwort ist zu groß.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException("Die Steam-Antwort ist zu groß.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static List<Game> ReadCategory(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var category))
            throw new InvalidDataException($"In der Steam-Antwort fehlt die Kategorie {name}.");
        return RequiredArray(category, "items").EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.TryGetInt32(out var value) && value == 0)
            .Select((item, index) => ReadSummary(item, 1000 - index))
            .DistinctBy(game => game.Id).ToList();
    }

    private static Game ReadSummary(JsonElement item, int popularity)
    {
        var id = item.GetProperty("id").GetInt32();
        if (id <= 0) throw new InvalidDataException("Steam lieferte eine ungültige App-ID.");
        return new Game
        {
            Id = $"steam:{id}", SteamAppId = id, Title = RequiredName(item),
            Genre = "Steam", Popularity = popularity,
            PortraitUrl = PortraitUrl(id),
            CoverUrl = ImageUrl(item, "large_capsule_image") ?? ImageUrl(item, "header_image") ?? ImageUrl(item, "tiny_image"),
            PriceLabel = PriceLabel(item, "final_price", "final_formatted")
        };
    }

    private static string PriceLabel(JsonElement price, string amountName, string formattedName)
    {
        var formatted = PlainText(String(price, formattedName));
        if (formatted.Length > 0) return formatted;
        var currency = String(price, "currency");
        if (!price.TryGetProperty(amountName, out var amount) || !amount.TryGetInt32(out var minorUnits) ||
            minorUnits <= 0 || currency.Length != 3) return "";
        return currency == "EUR" ? (minorUnits / 100m).ToString("C2", CultureInfo.GetCultureInfo("de-DE"))
            : (minorUnits / 100m).ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " " + currency;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Steam lieferte kein gültiges Array für {name}.");
        return value;
    }

    private static string RequiredName(JsonElement item)
    {
        var name = PlainText(String(item, "name"));
        return string.IsNullOrWhiteSpace(name)
            ? throw new InvalidDataException("Steam lieferte einen Eintrag ohne Titel.")
            : name;
    }

    private static string String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static string[] Strings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => PlainText(value.GetString() ?? "")).ToArray()
            : [];

    private static string? ImageUrl(JsonElement item, string name)
    {
        var address = String(item, name);
        if (string.IsNullOrWhiteSpace(address)) return null;
        return NetworkAddress.Parse(address).AbsoluteUri;
    }

    private static SystemRequirements[] ReadRequirements(JsonElement data) =>
        new[] { ("pc_requirements", "Windows"), ("mac_requirements", "macOS"), ("linux_requirements", "Linux") }
            .Select(platform =>
            {
                var present = data.TryGetProperty(platform.Item1, out var item) && item.ValueKind == JsonValueKind.Object;
                return new SystemRequirements
                {
                    Platform = platform.Item2,
                    Minimum = present ? StoreHtml.Text(String(item, "minimum"), preserveLines: true) : "",
                    Recommended = present ? StoreHtml.Text(String(item, "recommended"), preserveLines: true) : ""
                };
            }).Where(item => item.Minimum.Length > 0 || item.Recommended.Length > 0).ToArray();

    private static string PlainText(string html) => StoreHtml.Text(html);
}
