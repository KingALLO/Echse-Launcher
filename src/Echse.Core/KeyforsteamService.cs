using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Echse.Core;

public sealed record KeyforsteamPrice(
    decimal? FromPrice, string Currency, Uri Source, DateTimeOffset CheckedAt, string? UnavailableReason = null);

public sealed class KeyforsteamService(HttpClient client)
{
    private const string Site = "https://www.keyforsteam.de/";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, KeyforsteamPrice> cache = new(StringComparer.Ordinal);
    private long lastRequest;

    public static Uri SearchUrl(string title) => new(Site + "katalog/?search_name=" + Uri.EscapeDataString(title));

    public async Task<KeyforsteamPrice> GetPriceAsync(string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 300)
            throw new ArgumentException("Der Preisvergleich benötigt einen Spieltitel mit 1 bis 300 Zeichen.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(title, out var cached) && cached.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-15))
                return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(55));
            var result = await FetchAsync(title, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.Count >= 200) cache.Remove(cache.Keys.First());
            cache[title] = result;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Keyforsteam antwortet derzeit nicht. Bitte die Website im Browser prüfen oder später erneut versuchen.");
        }
        finally { gate.Release(); }
    }

    private async Task<KeyforsteamPrice> FetchAsync(string title, CancellationToken cancellationToken)
    {
        var (searchHtml, searchSource) = await GetPageAsync(SearchUrl(title), cancellationToken);
        if (searchHtml is null) return Unavailable(title, "Die Keyforsteam-Suche ist derzeit nicht verfügbar.");
        using var search = new HtmlParser().ParseDocument(searchHtml);
        var direct = ReadPrice(search, title, searchSource);
        if (direct is not null) return direct;
        var candidates = search.QuerySelectorAll("a[href]").Where(anchor =>
                new[] { anchor.TextContent, anchor.GetAttribute("title"), anchor.QuerySelector("img")?.GetAttribute("alt") }
                    .OfType<string>().Any(label => NormalizeTitle(label) == NormalizeTitle(title)))
            .Select(anchor => Uri.TryCreate(searchSource, anchor.GetAttribute("href"), out var uri) ? uri : null)
            .OfType<Uri>().Where(uri => IsSource(uri) && uri.AbsolutePath.EndsWith("-preisvergleich/", StringComparison.Ordinal) &&
                uri.Query.Length == 0)
            .DistinctBy(uri => uri.AbsoluteUri).ToArray();
        if (candidates.Length > 1)
            return Unavailable(title, "Kein eindeutiger PC-Produkttreffer in der Keyforsteam-Suche. Bitte direkt auf der Website auswählen.");
        var productUrl = candidates.Length == 1 ? candidates[0] : CandidateUrl(title);
        if (productUrl is null) return Unavailable(title, "Kein zuordenbarer Produkttreffer gefunden.");
        var (html, source) = await GetPageAsync(productUrl, cancellationToken);
        if (html is null) return Unavailable(title, "Die gefundene Produktseite ist nicht mehr verfügbar.");
        using var product = new HtmlParser().ParseDocument(html);
        return ReadPrice(product, title, source) ??
            Unavailable(title, "Keine passenden strukturierten PC-Produktdaten gefunden. Bitte den Preis direkt auf Keyforsteam prüfen.");
    }

    private static Uri? CandidateUrl(string title)
    {
        var letters = new string(title.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        var slug = Regex.Replace(letters, @"[^\p{L}\p{Nd}]+", "-", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Trim('-');
        return slug.Length == 0 ? null : new Uri(Site + Uri.EscapeDataString(slug) + "-key-kaufen-preisvergleich/");
    }

    private async Task<(string? Html, Uri Source)> GetPageAsync(Uri address, CancellationToken cancellationToken)
    {
        // Respect the site's published crawl delay for every page, including failed requests.
        if (lastRequest != 0)
        {
            var delay = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        }
        lastRequest = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        cancellationToken = timeout.Token;
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return (null, address);
        response.EnsureSuccessStatusCode();
        var source = response.RequestMessage?.RequestUri ?? address;
        if (!IsSource(source))
            throw new InvalidDataException("Der Preisvergleich wurde auf eine unerwartete Website umgeleitet.");

        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Die Keyforsteam-Antwort ist zu groß.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException("Die Keyforsteam-Antwort ist zu groß.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        return (Encoding.UTF8.GetString(buffer.ToArray()), source);
    }

    private static KeyforsteamPrice? ReadPrice(IDocument page, string title, Uri source)
    {
        foreach (var script in page.QuerySelectorAll("script[type='application/ld+json']"))
        {
            using var document = JsonDocument.Parse(script.TextContent);
            foreach (var product in Products(document.RootElement))
            {
                if (NormalizeTitle(Value(product, "name")) != NormalizeTitle(title)) continue;
                var category = product.TryGetProperty("category", out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : Value(value, "name") : "";
                if (!category.Equals("pc", StringComparison.OrdinalIgnoreCase) &&
                    !category.StartsWith("pc ", StringComparison.OrdinalIgnoreCase))
                    return Unavailable(title, "Die Plattform der Produktseite konnte nicht eindeutig als PC zugeordnet werden.");
                if (!product.TryGetProperty("offers", out var offers) || Value(offers, "@type") != "AggregateOffer" ||
                    !offers.TryGetProperty("lowPrice", out var lowPrice))
                    return Unavailable(title, "Für dieses Produkt ist kein öffentlicher Ab-Preis verfügbar.");
                var currency = Value(offers, "priceCurrency");
                var rawPrice = lowPrice.ValueKind == JsonValueKind.Number ? lowPrice.GetRawText() :
                    lowPrice.ValueKind == JsonValueKind.String ? lowPrice.GetString() : null;
                if (!decimal.TryParse(rawPrice, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out var amount) || amount < 0 || !Regex.IsMatch(currency, "^[A-Z]{3}$"))
                    throw new InvalidDataException("Keyforsteam lieferte keinen gültigen Preis mit Währung.");
                return new KeyforsteamPrice(amount, currency, source, DateTimeOffset.UtcNow);
            }
        }
        return null;
    }

    private static bool IsSource(Uri source) =>
        source.Scheme == Uri.UriSchemeHttps && source.IsDefaultPort && source.UserInfo.Length == 0 &&
        source.Host is "www.keyforsteam.de" or "keyforsteam.de";

    private static KeyforsteamPrice Unavailable(string title, string reason) =>
        new(null, "", SearchUrl(title), DateTimeOffset.UtcNow, reason);

    private static IEnumerable<JsonElement> Products(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var product in Products(child)) yield return product;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (Value(element, "@type") == "Product") yield return element;
            if (element.TryGetProperty("@graph", out var graph))
                foreach (var product in Products(graph)) yield return product;
        }
    }

    private static string Value(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static string NormalizeTitle(string title) =>
        new(title.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
