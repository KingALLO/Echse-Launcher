using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Echse.Core;

public sealed record SourceDownload(
    string SourceName, string SourceUrl, string Title, string MagnetUri, string PageUrl, int Score)
{
    public bool IsLikely => Score >= ReleaseTitleMatcher.LikelyScore;
    public bool IsCollection { get; init; }
}

public sealed record SourceError(string SourceUrl, string Message);

public sealed record SourceSearchResult(IReadOnlyList<SourceDownload> Downloads, IReadOnlyList<SourceError> Errors);

public sealed class WebsiteSourceService : IDisposable
{
    public const int MaxSources = 10;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxDetailPages = 8;
    private const int MaxDownloadsPerSource = 200;
    private const int MaxMagnetLength = 8192;
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SourceLifetime = TimeSpan.FromMinutes(10);
    private static readonly Regex GenericDownloadLabel = new(
        @"^(?:\W|\d|\b(?:magnet|torrent|download|downloads|link|links|mirror|mirrors|direct|options|click|here|to|get|this|repack|herunterladen|direkt|jetzt|öffnen|downloaden)\b)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SearchNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "q", "query", "search", "search_query", "searchterm", "search-term", "keyword", "keywords", "term"
    };

    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, WebsiteSource> sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RequestGate> requestGates = new(StringComparer.OrdinalIgnoreCase);

    public WebsiteSourceService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EchseLauncher/1.0");
    }

    public static string NormalizeUrl(string address)
    {
        var uri = NetworkAddress.Parse(address.Trim());
        if (!string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Die Quell-Adresse darf keine Abfrageparameter enthalten.");
        return uri.AbsoluteUri;
    }

    public string? CachedName(string sourceUrl) =>
        sources.TryGetValue(sourceUrl, out var source) ? source.Name : null;

    public async Task<string> ValidateSourceAsync(string sourceUrl, CancellationToken cancellationToken = default) =>
        (await GetSourceAsync(sourceUrl, refresh: true, cancellationToken)).Name;

    public async Task<SourceSearchResult> SearchAsync(
        IEnumerable<string> sourceUrls, Game game, bool refreshSources = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(game.Title) || game.Title.Length > 300)
            throw new ArgumentException("Die Quellensuche benötigt einen Spieltitel mit 1 bis 300 Zeichen.");
        var urls = sourceUrls.Distinct(StringComparer.Ordinal).ToList();
        if (urls.Count > MaxSources)
            throw new InvalidOperationException($"Es können höchstens {MaxSources} Quellen durchsucht werden.");

        var results = await Task.WhenAll(urls.Select(async url =>
        {
            try
            {
                return await SearchSourceAsync(url, game, refreshSources, cancellationToken);
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                return new SourceSearchResult([], [new SourceError(url, exception.Message)]);
            }
        }));
        var downloads = results.SelectMany(result => result.Downloads)
            .OrderByDescending(download => download.Score)
            .ThenBy(download => download.Title, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(download => MagnetLink.GetInfoHash(download.MagnetUri), StringComparer.Ordinal)
            .ToList();
        var errors = results.SelectMany(result => result.Errors)
            .OrderBy(error => error.SourceUrl, StringComparer.Ordinal)
            .ToList();
        return new SourceSearchResult(downloads, errors);
    }

    private async Task<SourceSearchResult> SearchSourceAsync(
        string sourceUrl, Game game, bool refresh, CancellationToken cancellationToken)
    {
        var source = await GetSourceAsync(sourceUrl, refresh, cancellationToken);
        var searchUrl = BuildSearchUrl(source.Form, game.Title);
        var searchPage = await GetPageAsync(searchUrl, source.HomeUrl, cancellationToken);
        using var search = new HtmlParser().ParseDocument(searchPage.Html);
        ThrowIfBrowserCheck(search);
        var downloads = new List<SourceDownload>();
        var errors = new List<SourceError>();
        var invalidMagnet = false;

        var blocks = search.QuerySelectorAll("article, .search-result, .search-item, .result, .post");
        foreach (var block in blocks)
        {
            var heading = block.QuerySelector("h1, h2, h3, h4");
            if (heading is null) continue;
            var title = CleanTitle(heading.TextContent);
            if (title.Length is 0 or > 300) continue;
            var score = ReleaseTitleMatcher.Score(game.Title, title);
            if (score > 0 && !ReleaseTitleMatcher.IsCollectionCandidate(game.Title, title))
            {
                var link = heading.QuerySelector("a[href]") ?? heading.Closest("a[href]");
                var pageUrl = link is not null &&
                    Uri.TryCreate(searchPage.Url, link.GetAttribute("href"), out var uri) && IsPermitted(source.HomeUrl, uri)
                    ? WithoutFragment(uri) : searchPage.Url;
                AddMagnets(block, game.Title, source.Name, sourceUrl, title, pageUrl, score, downloads, ref invalidMagnet);
            }
        }
        if (blocks.Length == 0 && (search.QuerySelector("main") ?? search.Body) is { } searchContent)
        {
            var headings = searchContent.QuerySelectorAll("h1, h2, h3, h4");
            if (headings.Length == 1)
            {
                var title = CleanTitle(headings[0].TextContent);
                var score = title.Length is > 0 and <= 300 ? ReleaseTitleMatcher.Score(game.Title, title) : 0;
                if (score > 0 && !ReleaseTitleMatcher.IsCollectionCandidate(game.Title, title))
                    AddMagnets(searchContent, game.Title, source.Name, sourceUrl, title, searchPage.Url, score, downloads, ref invalidMagnet);
            }
        }

        var candidates = FindCandidates(search, searchPage.Url, source.HomeUrl, game.Title);
        var collectionSearchTitle = ReleaseTitleMatcher.CollectionSearchTitle(game.Title);
        if (downloads.Count == 0 && candidates.Count == 0 && collectionSearchTitle.Length > 0 &&
            collectionSearchTitle != game.Title)
        {
            var collectionPage = await GetPageAsync(BuildSearchUrl(source.Form, collectionSearchTitle),
                source.HomeUrl, cancellationToken);
            using var collections = new HtmlParser().ParseDocument(collectionPage.Html);
            ThrowIfBrowserCheck(collections);
            candidates.AddRange(FindCandidates(collections, collectionPage.Url, source.HomeUrl, game.Title)
                .Where(candidate => ReleaseTitleMatcher.IsCollectionCandidate(game.Title, candidate.Title)));
        }
        if (candidates.Count > MaxDetailPages)
            errors.Add(new SourceError(sourceUrl, $"Es wurden nur die ersten {MaxDetailPages} passenden Trefferseiten geprüft."));
        foreach (var candidate in candidates.Take(MaxDetailPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detailPage = await GetPageAsync(candidate.Url, source.HomeUrl, cancellationToken);
                using var detail = new HtmlParser().ParseDocument(detailPage.Html);
                ThrowIfBrowserCheck(detail);
                var heading = detail.QuerySelector("article h1, article h2.entry-title") ??
                    detail.QuerySelector("main h1") ?? detail.QuerySelector("h1");
                var title = heading is null ? candidate.Title : CleanTitle(heading.TextContent);
                if (title.Length is 0 or > 300)
                    throw new InvalidDataException("Die Trefferseite enthält keinen gültigen Titel.");
                var score = ReleaseTitleMatcher.Score(game.Title, title);
                if (score == 0)
                {
                    errors.Add(new SourceError(sourceUrl, $"Der Titel der Trefferseite „{candidate.Title}“ passt nicht zum Spiel."));
                    continue;
                }
                var content = detail.QuerySelector("article .entry-content, article .post-content") ??
                    detail.QuerySelector("article") ?? detail.QuerySelector("main") ?? detail.Body;
                if (content is null)
                    throw new InvalidDataException("Die Trefferseite enthält keinen lesbaren Inhalt.");
                var collection = ReleaseTitleMatcher.IsCollectionCandidate(game.Title, title);
                if (collection && !ContainsIncludedGame(content, game.Title)) continue;
                AddMagnets(content, game.Title, source.Name, sourceUrl, title, detailPage.Url, score, downloads, ref invalidMagnet,
                    collection);
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                errors.Add(new SourceError(sourceUrl, $"„{candidate.Title}“: {exception.Message}"));
            }
        }

        if (invalidMagnet)
            errors.Add(new SourceError(sourceUrl, "Ungültige Magnet-Links wurden ignoriert."));
        var unique = downloads.OrderByDescending(download => download.Score)
            .DistinctBy(download => MagnetLink.GetInfoHash(download.MagnetUri), StringComparer.Ordinal).ToList();
        if (unique.Count > MaxDownloadsPerSource)
            errors.Add(new SourceError(sourceUrl, $"Es werden höchstens {MaxDownloadsPerSource} Downloads pro Quelle angezeigt."));
        return new SourceSearchResult(unique.Take(MaxDownloadsPerSource).ToList(), errors);
    }

    private async Task<WebsiteSource> GetSourceAsync(string sourceUrl, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && sources.TryGetValue(sourceUrl, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt < SourceLifetime)
            return cached;

        var address = NetworkAddress.Parse(NormalizeUrl(sourceUrl));
        var page = await GetPageAsync(address, address, cancellationToken);
        using var document = new HtmlParser().ParseDocument(page.Html);
        ThrowIfBrowserCheck(document);
        var form = FindSearchForm(document, page.Url) ??
            throw new InvalidDataException("Keine unterstützte Suche gefunden. Die Website benötigt ein öffentliches HTML-GET-Suchformular auf derselben Website.");
        var name = document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content") ??
            document.QuerySelector("title")?.TextContent;
        name = CleanTitle(name ?? "");
        if (name.Length == 0) name = page.Url.Host;
        if (name.Length > 80) name = name[..80];
        var source = new WebsiteSource(name, page.Url, form, DateTimeOffset.UtcNow);
        sources[sourceUrl] = source;
        return source;
    }

    private static SearchForm? FindSearchForm(IDocument document, Uri pageUrl)
    {
        foreach (var form in document.QuerySelectorAll("form"))
        {
            var method = form.GetAttribute("method");
            if (!string.IsNullOrWhiteSpace(method) && !method.Equals("get", StringComparison.OrdinalIgnoreCase))
                continue;
            var isSearchForm = string.Equals(form.GetAttribute("role"), "search", StringComparison.OrdinalIgnoreCase) ||
                (form.GetAttribute("id")?.Contains("search", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (form.GetAttribute("class")?.Contains("search", StringComparison.OrdinalIgnoreCase) ?? false);
            var input = form.QuerySelectorAll("input[name]")
                .Where(element => !element.HasAttribute("disabled"))
                .Select(element => new
                {
                    Element = element,
                    Name = element.GetAttribute("name")?.Trim() ?? "",
                    Type = (element.GetAttribute("type") ?? "text").ToLowerInvariant()
                })
                .Where(item => item.Name.Length is > 0 and <= 80 && item.Type is "search" or "text")
                .Select(item => new
                {
                    item.Element,
                    item.Name,
                    Priority = item.Type == "search" ? 3 : SearchNames.Contains(item.Name) ? 2 : isSearchForm ? 1 : 0
                })
                .OrderByDescending(item => item.Priority)
                .FirstOrDefault(item => item.Priority > 0);
            if (input is null) continue;
            var action = form.GetAttribute("action") ?? "";
            if (!Uri.TryCreate(pageUrl, action, out var actionUrl) || !IsPermitted(pageUrl, actionUrl))
                continue;
            var fields = form.QuerySelectorAll("input[name]")
                .Where(element => !element.HasAttribute("disabled") &&
                    string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase))
                .Select(element => new KeyValuePair<string, string>(
                    element.GetAttribute("name")?.Trim() ?? "", element.GetAttribute("value") ?? ""))
                .Where(field => field.Key.Length > 0 && !field.Key.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fields.Count > 20 || fields.Any(field => field.Key.Length > 80 || field.Value.Length > 1024))
                throw new InvalidDataException("Das Suchformular enthält zu viele oder zu lange Zusatzfelder.");
            return new SearchForm(WithoutFragment(actionUrl), input.Name, fields);
        }
        return null;
    }

    private static Uri BuildSearchUrl(SearchForm form, string title)
    {
        var parts = form.Action.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0].Replace("+", " "))
                .Equals(form.InputName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.AddRange(form.HiddenFields.Select(field =>
            Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
        parts.Add(Uri.EscapeDataString(form.InputName) + "=" + Uri.EscapeDataString(title));
        var result = new UriBuilder(form.Action) { Query = string.Join("&", parts) }.Uri;
        if (result.AbsoluteUri.Length > 4096)
            throw new InvalidDataException("Die Suchadresse der Website ist zu lang.");
        return result;
    }

    private static List<Candidate> FindCandidates(IDocument document, Uri pageUrl, Uri homeUrl, string gameTitle)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        void Add(IEnumerable<IElement> elements)
        {
            foreach (var element in elements)
            {
                var anchor = element.LocalName == "a" ? element : element.Closest("a[href]");
                if (anchor is null) continue;
                var title = CleanTitle(anchor.TextContent);
                if (title.Length is 0 or > 300) continue;
                var score = ReleaseTitleMatcher.Score(gameTitle, title);
                if (score == 0 || !Uri.TryCreate(pageUrl, anchor.GetAttribute("href"), out var uri) ||
                    !IsPermitted(homeUrl, uri))
                    continue;
                uri = WithoutFragment(uri);
                if (uri == pageUrl) continue;
                var candidate = new Candidate(title, uri, score);
                if (!candidates.TryGetValue(uri.AbsoluteUri, out var previous) || candidate.Score > previous.Score)
                    candidates[uri.AbsoluteUri] = candidate;
            }
        }

        Add(document.QuerySelectorAll(
            "h1 a[href], h2 a[href], h3 a[href], h4 a[href], a[href] h1, a[href] h2, a[href] h3, a[href] h4"));
        if (candidates.Count == 0) Add(document.QuerySelectorAll("a[href]"));
        return candidates.Values.OrderByDescending(candidate => candidate.Score).ToList();
    }

    private static void AddMagnets(
        IParentNode content, string gameTitle, string sourceName, string sourceUrl, string title, Uri pageUrl, int score,
        List<SourceDownload> downloads, ref bool invalidMagnet, bool collection = false)
    {
        foreach (var anchor in content.QuerySelectorAll("a[href]"))
        {
            var magnet = anchor.GetAttribute("href")?.Trim();
            if (magnet is null || !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
            if (anchor.Closest("aside, nav, footer, .related, .related-posts, .recommendations") is not null)
                continue;
            // Nested result blocks must not inherit the title of their containing article.
            var block = anchor.Closest("article, .search-result, .search-item, .result, .post");
            var heading = block?.QuerySelector("h1, h2, h3, h4");
            if (heading is not null && ReleaseTitleMatcher.Score(gameTitle, CleanTitle(heading.TextContent)) == 0)
                continue;
            try
            {
                if (magnet.Length > MaxMagnetLength)
                    throw new ArgumentException("Magnet-Link zu lang.");
                MagnetLink.Validate(magnet);
                if (!MatchesMagnetContext(anchor, content, gameTitle, title)) continue;
                var names = new Uri(magnet).Query.TrimStart('?').Split('&')
                    .Select(part => part.Split('=', 2))
                    .Where(pair => pair.Length == 2 && pair[0].Equals("dn", StringComparison.OrdinalIgnoreCase))
                    .Select(pair => Uri.UnescapeDataString(pair[1].Replace("+", " ")))
                    .ToList();
                if (names.Any(name => ReleaseTitleMatcher.Score(collection ? title : gameTitle, name) == 0)) continue;
                downloads.Add(new SourceDownload(sourceName, sourceUrl, title, magnet, pageUrl.AbsoluteUri, score)
                {
                    IsCollection = collection
                });
            }
            catch (ArgumentException) { invalidMagnet = true; }
        }
    }

    private static bool ContainsIncludedGame(IParentNode content, string gameTitle)
    {
        foreach (var heading in content.QuerySelectorAll("h2, h3, h4, p"))
        {
            if (!Regex.IsMatch(CleanTitle(heading.TextContent),
                @"^(included (?:content|games)|games included|enthaltene (?:spiele|inhalte)|(?:[\p{L}\p{N} '-]+ )?(?:collection|bundle|trilogy|quadrilogy|anthology) contents)\s*:?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (heading.NextElementSibling is not { } list || list.LocalName is not ("ul" or "ol")) continue;
            foreach (var item in list.QuerySelectorAll("li"))
            {
                var name = CleanTitle(item.TextContent).Split(',', 2)[0];
                if (ReleaseTitleMatcher.Score(gameTitle, name) == 100) return true;
            }
        }
        return false;
    }

    private static bool MatchesMagnetContext(IElement anchor, IParentNode content, string gameTitle, string releaseTitle)
    {
        bool Matches(string text) => text.Length <= 300 &&
            (GenericDownloadLabel.IsMatch(text) || ReleaseTitleMatcher.Score(gameTitle, text) > 0 ||
                ReleaseTitleMatcher.Score(releaseTitle, text) > 0);
        if (!Matches(CleanTitle(anchor.TextContent))) return false;
        var branch = anchor;
        for (var parent = anchor.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            var heading = parent.Children.TakeWhile(child => !ReferenceEquals(child, branch))
                .LastOrDefault(child => child.LocalName is "h1" or "h2" or "h3" or "h4");
            if (heading is not null && !Matches(CleanTitle(heading.TextContent))) return false;
            if (ReferenceEquals(parent, content)) break;
            branch = parent;
        }
        return true;
    }

    private async Task<HtmlPage> GetPageAsync(Uri address, Uri origin, CancellationToken cancellationToken)
    {
        var current = address;
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            if (!IsPermitted(origin, current))
                throw new InvalidDataException("Die Website verweist auf eine andere oder unsichere Adresse. Bitte diese Quelle direkt einrichten.");
            var response = await RequestPageAsync(current, cancellationToken);
            if (response.Redirect is null)
                return new HtmlPage(current, response.Html!);
            current = WithoutFragment(response.Redirect);
        }
        throw new InvalidDataException("Die Website hat zu viele Weiterleitungen gesendet.");
    }

    private async Task<PageResponse> RequestPageAsync(Uri address, CancellationToken cancellationToken)
    {
        var gate = requestGates.GetOrAdd(address.GetLeftPart(UriPartial.Authority), _ => new RequestGate());
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (gate.LastRequest != 0)
            {
                var delay = RequestInterval - Stopwatch.GetElapsedTime(gate.LastRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
            gate.LastRequest = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location is not { } location || !Uri.TryCreate(address, location, out var redirect))
                        throw new InvalidDataException("Die Website hat eine ungültige Weiterleitung gesendet.");
                    return new PageResponse(null, redirect);
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Die Website antwortet mit HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Die Website liefert keine HTML-Seite.");
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new InvalidDataException("Die Website-Antwort ist größer als 2 MiB.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes)
                        throw new InvalidDataException("Die Website-Antwort ist größer als 2 MiB.");
                    await buffer.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
                }
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim().Trim('"');
                Encoding encoding;
                try { encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
                catch (ArgumentException) { throw new InvalidDataException("Der Zeichensatz der Website wird nicht unterstützt."); }
                return new PageResponse(encoding.GetString(buffer.ToArray()), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Die Website hat nicht rechtzeitig geantwortet.");
            }
        }
        finally { gate.Semaphore.Release(); }
    }

    private static bool IsPermitted(Uri origin, Uri target) =>
        target.IsAbsoluteUri && string.IsNullOrEmpty(target.UserInfo) &&
        origin.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        ((origin.Scheme == target.Scheme && origin.Port == target.Port &&
            (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)) ||
         (origin.Scheme == Uri.UriSchemeHttp && target.Scheme == Uri.UriSchemeHttps &&
            origin.IsDefaultPort && target.IsDefaultPort));

    private static Uri WithoutFragment(Uri uri) => new UriBuilder(uri) { Fragment = "" }.Uri;

    private static string CleanTitle(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void ThrowIfBrowserCheck(IDocument document)
    {
        if (document.QuerySelector("#challenge-form, #cf-challenge-running, .cf-browser-verification") is not null)
            throw new InvalidDataException("Die Website verlangt eine Browser-Prüfung und kann nicht automatisch durchsucht werden.");
    }

    private static bool IsSourceFailure(Exception exception) =>
        exception is HttpRequestException or InvalidDataException or IOException or TimeoutException or ArgumentException;

    public void Dispose() => client.Dispose();

    private sealed record SearchForm(Uri Action, string InputName, IReadOnlyList<KeyValuePair<string, string>> HiddenFields);
    private sealed record WebsiteSource(string Name, Uri HomeUrl, SearchForm Form, DateTimeOffset CheckedAt);
    private sealed record Candidate(string Title, Uri Url, int Score);
    private sealed record HtmlPage(Uri Url, string Html);
    private sealed record PageResponse(string? Html, Uri? Redirect);
    private sealed class RequestGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public long LastRequest { get; set; }
    }
}
