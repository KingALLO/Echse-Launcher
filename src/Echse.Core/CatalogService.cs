using System.Text.Json;

namespace Echse.Core;

public sealed class CatalogService(HttpClient client)
{
    public async Task<List<Game>> FetchAsync(string address, CancellationToken cancellationToken = default)
    {
        var uri = NetworkAddress.Parse(address);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Der Katalog darf höchstens 4 MiB groß sein.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Der Katalog darf höchstens 4 MiB groß sein.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var games = JsonSerializer.Deserialize<List<Game>>(buffer.ToArray(), StateRepository.JsonOptions)
            ?? throw new InvalidDataException("Der Katalog muss ein JSON-Array enthalten.");
        Validate(games);
        return games;
    }

    public static void Validate(IReadOnlyCollection<Game> games)
    {
        if (games.Count > 5000)
            throw new InvalidDataException("Der Katalog darf höchstens 5000 Spiele enthalten.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Id) || string.IsNullOrWhiteSpace(game.Title) ||
                game.Description is null || game.Genre is null || game.Tags is null ||
                game.Tags.Any(string.IsNullOrWhiteSpace) || game.Popularity < 0 || !ids.Add(game.Id) ||
                game.SteamAppId is <= 0 || game.Screenshots is null || game.Developers is null ||
                game.Publishers is null || game.Platforms is null || game.ReleaseDate is null ||
                game.PriceLabel is null || game.OriginalPriceLabel is null || game.ContentType is null ||
                game.ScreenshotThumbnails is null || game.DiscountPercent is < 0 or > 100 ||
                game.Requirements is null || game.Requirements.Any(item => item is null ||
                    item.Platform is null || item.Minimum is null || item.Recommended is null))
                throw new InvalidDataException("Ungültiger Katalog: Spiele benötigen eindeutige IDs, Titel und gültige Metadaten.");
            if (!string.IsNullOrWhiteSpace(game.CoverUrl))
                NetworkAddress.Parse(game.CoverUrl);
            if (!string.IsNullOrWhiteSpace(game.BannerUrl))
                NetworkAddress.Parse(game.BannerUrl);
            if (!string.IsNullOrWhiteSpace(game.PortraitUrl))
                NetworkAddress.Parse(game.PortraitUrl);
            foreach (var screenshot in game.Screenshots)
                NetworkAddress.Parse(screenshot);
            foreach (var thumbnail in game.ScreenshotThumbnails)
                NetworkAddress.Parse(thumbnail);
            if (!string.IsNullOrWhiteSpace(game.MagnetUri))
                MagnetLink.Validate(game.MagnetUri);
        }
    }
}

public static class NetworkAddress
{
    public static Uri Parse(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Bitte eine HTTP(S)-Adresse ohne eingebettete Zugangsdaten oder Fragment angeben.");
        return uri;
    }
}

public static class MagnetLink
{
    public static void Validate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "magnet")
            throw new ArgumentException("Es wird ein gültiger Magnet-Link benötigt.");
        var topics = uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Equals("xt", StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1]));
        if (!topics.Any(topic =>
            topic.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase) &&
            IsHash(topic[9..])))
            throw new ArgumentException("Der Magnet-Link benötigt einen gültigen BitTorrent-v1-Infohash (btih).");
    }

    private static bool IsHash(string hash) =>
        (hash.Length == 40 && hash.All(Uri.IsHexDigit)) ||
        (hash.Length == 32 && hash.All(c => char.ToUpperInvariant(c) is >= 'A' and <= 'Z' or >= '2' and <= '7'));
}
