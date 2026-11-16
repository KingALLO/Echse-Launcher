using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Echse.Core;
using Xunit;

namespace Echse.Core.Tests;

public sealed class WebsiteSourceServiceTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
    private const string OtherMagnet = "magnet:?xt=urn:btih:89abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("Portal", "Portal.v1.0.2.Complete-GOG", 95)]
    [InlineData("Portal", "[Group] Portal", 100)]
    [InlineData("Portal", "Portal 2", 0)]
    [InlineData("Portal", "Portal II", 0)]
    [InlineData("Portal", "Portal Collection", 50)]
    [InlineData("Portal 2", "Portal Collection", 50)]
    [InlineData("Portal", "Portal Knights", 0)]
    [InlineData("Portal", "Portal Reloaded", 0)]
    [InlineData("Portal", "Portal Complete Another Game", 0)]
    [InlineData("Portal", "Portal MULTIVERSE", 0)]
    [InlineData("Portal", "Portal MULTI12", 95)]
    [InlineData("Portal", "Portal Premium Edition", 95)]
    [InlineData("Portal", "Portal Gold Edition", 95)]
    [InlineData("Portal", "Portal Premium", 0)]
    [InlineData("Portal", "Portal Premium Knights", 0)]
    [InlineData("Portal", "Portal Premium Edition Another Game", 0)]
    [InlineData("Bodycam", "Live Lens Bodycam", 0)]
    [InlineData("POSTAL 2", "POSTAL 4: No Regerts", 0)]
    [InlineData("Jurassic World Evolution 3", "Jurassic World Evolution 3: Premium Edition, v1.3.5.915680 + 4 DLCs", 95)]
    [InlineData("Half-Life", "Half.Life.v1.0.GOG", 95)]
    [InlineData("Half-Life", "Half-Life 2", 0)]
    [InlineData("Half-Life", "Half-Life Blue Shift", 0)]
    [InlineData("Baldur's Gate 3", "Baldurs.Gate.3.v4.1.1", 95)]
    [InlineData("Half-Life 2", "Half-Life 2: 20th Anniversary Edition – Build 16431800 + Bonus Soundtracks", 95)]
    [InlineData("Half-Life: Alyx", "Half-Life: Alyx – v1.5.4 + DLC + HLA-NoVR-Launcher v4.2.4", 50)]
    [InlineData("Half-Life 2", "Half-Life 2 (Build 16431800 + Bonus Soundtracks, MULTi18) [FitGirl Repack, Selective Download - from 3.4 GB]", 95)]
    [InlineData("Portal", "rutor.info_Portal.v1.0 [FitGirl Repack]", 95)]
    [InlineData("Half-Life", "Half-Life: Source Quadrilogy – v09.26.2019 + 4 OSTs", 50)]
    [InlineData("Half-Life 2", "Half-Life 2: Episode One", 0)]
    [InlineData("Half-Life", "Half-Life: Alyx – v1.5.4", 0)]
    public void ScoresReleaseNames(string title, string release, int expected) =>
        Assert.Equal(expected, ReleaseTitleMatcher.Score(title, release));

    [Theory]
    [InlineData("https://src.example", "https://src.example/")]
    [InlineData("https://src.example/blog", "https://src.example/blog")]
    public void NormalizesWebsiteUrls(string input, string expected) =>
        Assert.Equal(expected, WebsiteSourceService.NormalizeUrl(input));

    [Theory]
    [InlineData("https://src.example/?q=1")]
    [InlineData("https://src.example/#section")]
    [InlineData("https://user:password@src.example/")]
    public void RejectsUnsafeSourceUrls(string input) =>
        Assert.Throws<ArgumentException>(() => WebsiteSourceService.NormalizeUrl(input));

    [Fact]
    public async Task SearchesWebsitesWithoutAmbiguousTitlesAndPreservesSourceErrors()
    {
        var requests = new ConcurrentQueue<Uri>();
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            var uri = request.RequestUri!;
            requests.Enqueue(uri);
            if (uri.Host == "offline.example") return Status(HttpStatusCode.ServiceUnavailable);
            return (uri.AbsolutePath, uri.Query) switch
            {
                ("/", "") => Html("""
                    <title>Games</title>
                    <form role="search" action="/search?scope=games">
                        <input type="search" name="term">
                        <input type="hidden" name="kind" value="game">
                    </form>
                    """),
                ("/search", _) => Html("""
                    <article><h2><a href="/portal-release">Portal v1.0</a></h2></article>
                    <article><h2><a href="/portal-collection">Portal Collection</a></h2></article>
                    <article><h2><a href="/portal-2">Portal 2</a></h2></article>
                    <article><h2><a href="https://other.example/portal">Portal</a></h2></article>
                    """),
                ("/portal-release", "") => Html($"""
                    <header><h1>Games</h1></header>
                    <article><h1>Portal v1.0</h1><div class="entry-content">
                        <a href="{Magnet}&amp;dn=Portal">Magnet</a>
                        <a href="magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH&amp;tr=other">Mirror</a>
                        <a href="magnet:?xt=invalid">Invalid</a>
                    </div></article>
                    """),
                ("/portal-collection", "") => Html($"""
                    <article><h1>Portal Collection</h1><div class="entry-content">
                        <a href="{OtherMagnet}">Magnet</a>
                    </div></article>
                    """),
                _ => Status(HttpStatusCode.NotFound)
            };
        }));

        var result = await service.SearchAsync(["https://games.example/", "https://offline.example/"],
            new Game { Id = "steam:400", Title = "Portal", SteamAppId = 400 });

        var first = Assert.Single(result.Downloads);
        Assert.Equal("Portal v1.0", first.Title);
        Assert.True(first.IsLikely);
        Assert.Equal("Games", first.SourceName);
        Assert.Equal(Magnet + "&dn=Portal", first.MagnetUri);
        Assert.Equal("https://games.example/portal-release", first.PageUrl);
        Assert.Contains(result.Errors, error => error.SourceUrl == "https://offline.example/" && error.Message.Contains("503"));
        Assert.Contains(result.Errors, error => error.SourceUrl == "https://games.example/" && error.Message.Contains("Ungültige Magnet"));
        Assert.Contains(requests, uri => uri.PathAndQuery == "/search?scope=games&kind=game&term=Portal");
        Assert.DoesNotContain(requests, uri => uri.Host == "other.example" || uri.AbsolutePath == "/portal-2");
    }

    [Theory]
    [InlineData("Half-Life 2", "Half-Life 2: 20th Anniversary Edition – Build 16431800 + Bonus Soundtracks",
        "Half-Life 2 (Build 16431800 + Bonus Soundtracks, MULTi18) [FitGirl Repack, Selective Download - from 3.4 GB]")]
    [InlineData("Half-Life: Alyx", "Half-Life: Alyx – v1.5.4 + DLC + HLA-NoVR-Launcher v4.2.4",
        "Half-Life Alyx v1.5.4 + DLC [FitGirl Repack]")]
    [InlineData("Jurassic World Evolution 3", "Jurassic World Evolution 3: Premium Edition, v1.3.5.915680 + 4 DLCs",
        "Jurassic World Evolution 3: Premium Edition (v1.3.5.915680 + 4 DLCs, MULTi13) [FitGirl Hypervisor Repack, Selective Download - from 9.1 GB]")]
    public async Task FindsReleasesWithEditionBuildBonusAndRepackMetadata(string game, string title, string name)
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/" when request.RequestUri.Query.Length == 0 => Html("""<form><input type="search" name="q"></form>"""),
                "/" => Html($"""<article><h2><a href="/release">{title}</a></h2></article>"""),
                "/release" => Html($"""
                    <article><h1>{title}</h1><div class="entry-content">
                        <h3>Download Mirrors (Torrent)</h3>
                        <a href="{Magnet}&amp;dn={Uri.EscapeDataString(name)}">Get this repack</a>
                    </div></article>
                    """),
                _ => Status(HttpStatusCode.NotFound)
            }));
        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = game });
        var download = Assert.Single(result.Downloads);
        Assert.Equal(title, download.Title);
        Assert.False(download.IsCollection);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("Portal", "Portal", true)]
    [InlineData("Portal 2", "Portal 2", true)]
    [InlineData("Portal", "Portal 2", false)]
    [InlineData("Portal 2", "Portal 2: Sixense Perceptual Pack", false)]
    [InlineData("Portal", "Portal – Soundtrack", false)]
    public async Task RequiresExplicitCollectionMembershipBeforeShowingItsDownloads(
        string game, string includedGame, bool expected)
    {
        var detailRequested = false;
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            if (request.RequestUri!.PathAndQuery == "/")
                return Html("""<form><input type="search" name="q"></form>""");
            if (request.RequestUri.AbsolutePath == "/collection")
            {
                detailRequested = true;
                return Html($"""
                    <article><h1>Portal Collection</h1><div class="entry-content">
                        <h3>Download Mirrors</h3><a href="{Magnet}">[magnet]</a>
                        <h3>Included Content</h3><ul><li>{includedGame}, Build 123456 (May 1, 2020)</li></ul>
                        <h3>Description</h3><p>Portal and Portal 2 are mentioned outside the inclusion list.</p>
                    </div></article>
                    """);
            }
            return Html($"""
                <article><h2><a href="/collection">Portal Collection</a></h2>
                    <a href="{Magnet}">Magnet</a>
                </article>
                <article><h2><a href="/knights">Portal Knights</a></h2><a href="{OtherMagnet}">Magnet</a></article>
                """);
        }));
        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = game });
        Assert.True(detailRequested);
        if (expected)
        {
            var download = Assert.Single(result.Downloads);
            Assert.True(download.IsCollection);
            Assert.False(download.IsLikely);
            Assert.Equal("Portal Collection", download.Title);
        }
        else Assert.Empty(result.Downloads);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CollectionMentionOutsideTheInclusionListDoesNotProveMembership()
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.PathAndQuery switch
            {
                "/" => Html("""<form><input type="search" name="q"></form>"""),
                "/?q=Portal" => Html("""<article><h2><a href="/collection">Portal Collection</a></h2></article>"""),
                "/collection" => Html($"""
                    <article><h1>Portal Collection</h1><div class="entry-content">
                        <h3>Download Mirrors</h3><a href="{Magnet}">Magnet</a>
                        <h3>Included Content</h3><ul><li>Portal 2</li></ul>
                        <h3>Related games</h3><ul><li>Portal</li></ul>
                    </div></article>
                    """),
                _ => Status(HttpStatusCode.NotFound)
            }));
        Assert.Empty((await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" })).Downloads);
    }

    [Theory]
    [InlineData("Portal 2", "Portal", "Portal Collection", "Included Content", "Portal 2", true)]
    [InlineData("Half-Life 2: Episode One", "Half-Life", "Half-Life: Source Quadrilogy", "Source Quadrilogy Contents",
        "Half-Life 2: Episode One", true)]
    [InlineData("Half-Life: Opposing Force", "Half-Life", "Half-Life: Source Quadrilogy", "Source Quadrilogy Contents",
        "Half-Life Source", false)]
    public async Task RetriesSeriesSearchOnlyForCollectionsAndStillRequiresTheExactGame(
        string title, string series, string collection, string heading, string included, bool expected)
    {
        var queries = new List<string>();
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.PathAndQuery == "/") return Html("""<form><input type="search" name="q"></form>""");
            if (uri.AbsolutePath == "/collection") return Html($"""
                <article><h1>{collection}</h1><div class="entry-content">
                    <h3>Download Mirrors</h3><a href="{Magnet}">Magnet</a>
                    <p><strong>{heading}</strong></p><ul><li>{included}</li></ul>
                </div></article>
                """);
            queries.Add(WebUtility.UrlDecode(uri.Query["?q=".Length..]));
            if (queries.Last() == series)
                return Html($"""
                    <article><h2><a href="/collection">{collection}</a></h2></article>
                    <article><h2>Unrelated game</h2><a href="{OtherMagnet}">Magnet</a></article>
                    """);
            return Html("<main>No matching titles</main>");
        }));
        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = title });
        Assert.Equal(new[] { title, series }, queries);
        Assert.Equal(expected ? 1 : 0, result.Downloads.Count);
        Assert.All(result.Downloads, download => Assert.True(download.IsCollection));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MatchingPageDoesNotRelabelForeignMagnetsOrNestedResults()
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.PathAndQuery switch
            {
                "/" => Html("""<form><input type="search" name="q"></form>"""),
                "/?q=Portal" => Html($"""
                    <article><h1>Portal</h1>
                        <a href="{Magnet}&amp;dn=Portal.v1.0.Complete-GOG">Magnet</a>
                        <a href="{OtherMagnet}&amp;dn=Portal+2">Magnet</a>
                        <a href="{OtherMagnet}&amp;dn=Half-Life">Magnet</a>
                        <a href="{OtherMagnet}">Half-Life</a>
                        <section><h2>Half-Life</h2><a href="{OtherMagnet}">Magnet</a></section>
                        <section><h2>Downloads</h2><a href="{Magnet}">Magnet Link</a></section>
                        <aside><a href="{OtherMagnet}">Recommended</a></aside>
                        <div class="related-posts"><a href="{OtherMagnet}">Magnet</a></div>
                        <div class="result"><h2>Portal Knights</h2><a href="{OtherMagnet}">Magnet</a></div>
                    </article>
                    """),
                _ => Status(HttpStatusCode.NotFound)
            }));
        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" });
        Assert.StartsWith(Magnet, Assert.Single(result.Downloads).MagnetUri);
    }

    [Fact]
    public async Task KeepsInlineResultsWhenDetailPageFails()
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.PathAndQuery switch
            {
                "/" => Html("""<form><input type="search" name="q"></form>"""),
                "/?q=Portal" => Html($"""
                    <div class="result"><a href="/portal"><h2>Portal</h2></a>
                        <a href="{Magnet}">Magnet</a>
                    </div>
                    """),
                _ => Status(HttpStatusCode.ServiceUnavailable)
            }));

        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" });

        var download = Assert.Single(result.Downloads);
        Assert.Equal(Magnet, download.MagnetUri);
        Assert.Equal("https://games.example/portal", download.PageUrl);
        Assert.Contains(result.Errors, error => error.Message.Contains("Portal") && error.Message.Contains("503"));
    }

    [Fact]
    public async Task ReadsDirectResultsWhenSearchOpensAPlainDetailPage()
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.PathAndQuery switch
            {
                "/" => Html("""<form><input type="search" name="q"></form>"""),
                "/?q=Portal" => Html($"""<main><h1>Portal</h1><a href="{Magnet}">Magnet</a></main>"""),
                _ => Status(HttpStatusCode.NotFound)
            }));

        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" });

        Assert.Equal(Magnet, Assert.Single(result.Downloads).MagnetUri);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task EncodesTheGameTitleInTheSearchQuery()
    {
        Uri? searchUrl = null;
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            if (request.RequestUri!.Query.Length == 0)
                return Html("""<form><input type="search" name="q"></form>""");
            searchUrl = request.RequestUri;
            return Html("<main>No results</main>");
        }));
        const string title = "Baldur's Gate 3 & More";

        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = title });

        Assert.Empty(result.Downloads);
        Assert.Empty(result.Errors);
        Assert.NotNull(searchUrl);
        Assert.Equal(title, WebUtility.UrlDecode(searchUrl.Query["?q=".Length..]));
    }

    [Fact]
    public async Task LimitsTheNumberOfDetailPages()
    {
        var detailRequests = 0;
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.PathAndQuery == "/")
                return Html("""<form><input type="search" name="q"></form>""");
            if (uri.PathAndQuery == "/?q=Portal")
                return Html(string.Join("", Enumerable.Range(1, 9).Select(index =>
                    $"""<article><h2><a href="/detail{index}">Portal v1.0</a></h2></article>""")));
            detailRequests++;
            return Html($"""<article><h1>Portal v1.0</h1><a href="{Magnet}">Magnet</a></article>""");
        }));

        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" });

        Assert.Equal(8, detailRequests);
        Assert.Single(result.Downloads);
        Assert.Contains(result.Errors, error => error.Message.Contains("8 passenden Trefferseiten"));
    }

    [Fact]
    public async Task DoesNotTrustSearchResultWhenDetailTitleIsDifferent()
    {
        using var service = new WebsiteSourceService(new Handler(request =>
            request.RequestUri!.PathAndQuery switch
            {
                "/" => Html("""<form><input type="search" name="q"></form>"""),
                "/?q=Portal" => Html("""<article><h2><a href="/wrong">Portal</a></h2></article>"""),
                "/wrong" => Html($"""<article><h1>Portal 2</h1><a href="{Magnet}">Magnet</a></article>"""),
                _ => Status(HttpStatusCode.NotFound)
            }));

        var result = await service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" });

        Assert.Empty(result.Downloads);
        Assert.Contains(result.Errors, error => error.Message.Contains("Titel der Trefferseite"));
    }

    [Fact]
    public async Task RejectsCrossSiteRedirectBeforeRequestingIt()
    {
        var requests = new ConcurrentQueue<Uri>();
        using var service = new WebsiteSourceService(new Handler(request =>
        {
            requests.Enqueue(request.RequestUri!);
            var response = Status(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://elsewhere.example/");
            return response;
        }));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ValidateSourceAsync("https://games.example/"));

        Assert.Contains("andere", error.Message);
        Assert.Single(requests);
        Assert.Equal("games.example", requests.Single().Host);
    }

    [Fact]
    public async Task RejectsUnsupportedSearchAndOversizedPages()
    {
        using var noSearch = new WebsiteSourceService(new Handler(_ =>
            Html("""<form method="post"><input type="search" name="q"></form>""")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            noSearch.ValidateSourceAsync("https://games.example/"));
        Assert.Contains("Suchformular", error.Message);

        using var tooLarge = new WebsiteSourceService(new Handler(_ => Html(new string('a', 2 * 1024 * 1024 + 1))));
        await Assert.ThrowsAsync<InvalidDataException>(() => tooLarge.ValidateSourceAsync("https://games.example/"));
    }

    [Fact]
    public async Task CancellationInterruptsAnActiveWebsiteRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new WebsiteSourceService(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Query.Length == 0)
                return Html("""<form><input type="search" name="q"></form>""");
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Status(HttpStatusCode.OK);
        }));
        await service.ValidateSourceAsync("https://games.example/");
        using var cancellation = new CancellationTokenSource();
        var search = service.SearchAsync(["https://games.example/"], new Game { Title = "Portal" },
            cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
