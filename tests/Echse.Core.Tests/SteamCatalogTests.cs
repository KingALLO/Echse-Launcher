using System.Net;
using System.Text;
using Echse.Core;
using Xunit;

namespace Echse.Core.Tests;

public sealed class SteamCatalogTests
{
    [Fact]
    public async Task ArtworkSearchMatchesUniqueNormalizedTitleAndReturnsPortraitSource()
    {
        using var client = Client("""
            {"items":[
              {"type":"app","id":620,"name":"Portal 2","tiny_image":"https://cdn.example/portal.jpg"},
              {"type":"app","id":400,"name":"Portal"}]}
            """);
        var game = await new SteamCatalogService(client).FindArtworkAsync("portal-2");
        Assert.NotNull(game);
        Assert.Equal(620, game.SteamAppId);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/620/library_600x900.jpg", game.PortraitUrl);
    }

    [Theory]
    [InlineData("Portal")]
    [InlineData("Portal 2 Deluxe")]
    public async Task ArtworkSearchDoesNotGuessWhenTitleIsAmbiguousOrEditionDiffers(string title)
    {
        using var client = Client("""
            {"items":[
              {"type":"app","id":620,"name":"Portal"},
              {"type":"app","id":400,"name":"Portal"}]}
            """);
        Assert.Null(await new SteamCatalogService(client).FindArtworkAsync(title));
    }

    [Fact]
    public async Task HomeKeepsCategoriesDeduplicatesAndExcludesBundles()
    {
        using var client = Client("""
            {
              "top_sellers":{"items":[
                {"id":620,"type":0,"name":"Portal 2","large_capsule_image":"https://cdn.example/portal.jpg"},
                {"id":620,"type":0,"name":"Portal 2"},
                {"id":10,"type":1,"name":"Bundle"}]},
              "new_releases":{"items":[{"id":400,"type":0,"name":"Portal"}]},
              "specials":{"items":[{"id":620,"type":0,"name":"Portal 2"}]},
              "coming_soon":{"items":[]}
            }
            """);
        var snapshot = await new SteamCatalogService(client).GetHomeAsync();
        var game = Assert.Single(snapshot.Popular);
        Assert.Equal("steam:620", game.Id);
        Assert.Equal("https://cdn.example/portal.jpg", game.CoverUrl);
        Assert.Single(snapshot.NewReleases);
        Assert.Equal(2, snapshot.AllGames().Count());
        Assert.True(snapshot.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task SearchEscapesQueryAndPreservesAppIdentityWithoutCreatingDownloadLinks()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/api/storesearch/", request.RequestUri!.AbsolutePath);
            Assert.Contains("term=A%26B%20%2F%20Spiel", request.RequestUri.AbsoluteUri);
            return Task.FromResult(Json("""
                {"total":3,"items":[
                  {"type":"app","id":620,"name":"A&amp;B","tiny_image":"https://cdn.example/small.jpg"},
                  {"type":"sub","id":3,"name":"Bundle"},
                  {"type":"app","id":620,"name":"Duplicate"}]}
                """));
        });
        using var client = new HttpClient(handler);
        var game = Assert.Single(await new SteamCatalogService(client).SearchAsync("A&B / Spiel"));
        Assert.Equal(620, game.SteamAppId);
        Assert.Equal("A&B", game.Title);
        Assert.Null(game.MagnetUri);
    }

    [Fact]
    public async Task DetailsMapRealSteamFieldsAndRenderDescriptionsAsPlainText()
    {
        using var client = Client("""
            {"620":{"success":true,"data":{
              "steam_appid":620,"type":"game","name":"Portal &amp; Friends",
              "short_description":"<b>Rätsel</b> &amp; Abenteuer",
              "header_image":"https://cdn.example/header.jpg",
              "background_raw":"https://cdn.example/banner.jpg",
              "genres":[{"id":"25","description":"Abenteuer"}],
              "developers":["Valve"],"publishers":["Valve"],
              "release_date":{"date":"18. Apr. 2011"},
              "platforms":{"windows":true,"mac":false,"linux":true},
              "is_free":false,"price_overview":{"final_formatted":"9,99€",
                "initial":1999,"currency":"EUR","discount_percent":50,"initial_formatted":""},
              "screenshots":[{"path_thumbnail":"https://cdn.example/screen.jpg",
                "path_full":"https://cdn.example/full.jpg"},{"path_thumbnail":"https://cdn.example/old.jpg"}]
            }}}
            """);
        var game = await new SteamCatalogService(client).GetDetailsAsync(620);
        Assert.Equal("Portal & Friends", game.Title);
        Assert.Equal("Rätsel & Abenteuer", game.Description);
        Assert.Equal("https://cdn.example/banner.jpg", game.BannerUrl);
        Assert.Equal(["Abenteuer"], game.Tags);
        Assert.Equal(["Windows", "Linux"], game.Platforms);
        Assert.Equal(["Valve"], game.Developers);
        Assert.Equal("18. Apr. 2011", game.ReleaseDate);
        Assert.Equal("9,99€", game.PriceLabel);
        Assert.Equal("19,99 €", game.OriginalPriceLabel.Replace('\u00a0', ' '));
        Assert.Equal(50, game.DiscountPercent);
        Assert.True(game.PriceCheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(["https://cdn.example/full.jpg", "https://cdn.example/old.jpg"], game.Screenshots);
        Assert.Equal(["https://cdn.example/screen.jpg", "https://cdn.example/old.jpg"], game.ScreenshotThumbnails);
        Assert.Null(game.MagnetUri);
    }

    [Theory]
    [InlineData("""{"620":{"success":false}}""")]
    [InlineData("""{"620":{"success":true,"data":{"steam_appid":400,"type":"game","name":"Wrong"}}}""")]
    [InlineData("""{"620":{"success":true,"data":{"steam_appid":620,"type":"hardware","name":"Hardware"}}}""")]
    public async Task DetailsRejectUnavailableMismatchedAndNonGameEntries(string json)
    {
        using var client = Client(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SteamCatalogService(client).GetDetailsAsync(620));
    }

    [Fact]
    public async Task DetailsAllowMissingArtworkAndUnknownReleaseDate()
    {
        using var client = Client("""
            {"620":{"success":true,"data":{
              "steam_appid":620,"type":"dlc","name":"Expansion","is_free":true
            }}}
            """);
        var game = await new SteamCatalogService(client).GetDetailsAsync(620);
        Assert.Null(game.CoverUrl);
        Assert.Null(game.BannerUrl);
        Assert.Empty(game.Screenshots);
        Assert.Empty(game.ReleaseDate);
        Assert.Equal("Kostenlos spielbar", game.PriceLabel);
        Assert.Empty(game.OriginalPriceLabel);
        Assert.Empty(game.ScreenshotThumbnails);
    }

    [Fact]
    public async Task MissingPriceIsNotReportedAsFree()
    {
        using var client = Client("""
            {"620":{"success":true,"data":{"steam_appid":620,"type":"game","name":"Unreleased","is_free":false}}}
            """);
        var game = await new SteamCatalogService(client).GetDetailsAsync(620);
        Assert.Empty(game.PriceLabel);
        Assert.Empty(game.OriginalPriceLabel);
        Assert.Equal(0, game.DiscountPercent);
    }

    [Fact]
    public async Task NumericPriceUsesMinorUnitsAndSourceCurrencyWhenFormattedPriceIsMissing()
    {
        using var client = Client("""
            {"620":{"success":true,"data":{"steam_appid":620,"type":"game","name":"Portal",
              "price_overview":{"currency":"USD","final":249,"initial":999,"discount_percent":75}}}}
            """);
        var game = await new SteamCatalogService(client).GetDetailsAsync(620);
        Assert.Equal("2,49 USD", game.PriceLabel);
        Assert.Equal("9,99 USD", game.OriginalPriceLabel);
        Assert.Equal(75, game.DiscountPercent);
    }

    [Fact]
    public async Task RequirementsKeepPlatformsAndLineBreaksWithoutRenderingMarkupOrScripts()
    {
        using var client = Client("""
            {"620":{"success":true,"data":{"steam_appid":620,"type":"game","name":"Portal",
              "pc_requirements":{"minimum":"<strong>Minimum:</strong><br><ul><li>OS: Windows 10</li><li>RAM: 8&nbsp;GB</li></ul><script>hidden()</script>",
                "recommended":"<p>GPU: RTX 3060</p>"},
              "mac_requirements":[],
              "linux_requirements":{"minimum":"<b>OS:</b> Ubuntu<br>CPU: x64"}}}}
            """);
        var game = await new SteamCatalogService(client).GetDetailsAsync(620);
        Assert.Equal(2, game.Requirements.Length);
        Assert.Equal("Windows", game.Requirements[0].Platform);
        Assert.Equal("Minimum:\nOS: Windows 10\nRAM: 8 GB", game.Requirements[0].Minimum);
        Assert.Equal("GPU: RTX 3060", game.Requirements[0].Recommended);
        Assert.Equal("OS: Ubuntu\nCPU: x64", game.Requirements[1].Minimum);
        Assert.Empty(game.Requirements[1].Recommended);
    }

    [Fact]
    public async Task MissingSearchArrayAndMissingHomeCategoriesAreErrorsNotEmptySuccesses()
    {
        using var client = Client("{}");
        var service = new SteamCatalogService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SearchAsync("Portal"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetHomeAsync());
    }

    [Fact]
    public async Task NetworkErrorsAndCancellationArePropagated()
    {
        using var limited = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new SteamCatalogService(limited).SearchAsync("Portal"));
        Assert.Contains("begrenzt", exception.Message);
        using var cancelled = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        var request = new SteamCatalogService(cancelled).SearchAsync("Portal", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task OldStateLoadsAndSteamDetailsRoundTripWithoutChangingPlaytime()
    {
        var folder = Path.Combine(Path.GetTempPath(), "echse-steam-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "state.json"),
                """{"version":1,"library":[{"game":{"id":"local","title":"Existing"},"playSeconds":1234}]}""");
            var repository = new StateRepository(folder);
            await repository.LoadAsync();
            await repository.UpdateAsync(state =>
            {
                state.Library[0].Game = state.Library[0].Game with { PortraitUrl = "https://cdn.example/portrait.jpg" };
                state.SteamCatalog = new SteamCatalogSnapshot
                {
                    Popular = [new Game { Id = "steam:620", Title = "Portal", SteamAppId = 620 }]
                };
                state.SteamDetails.Add(new Game
                {
                    Id = "steam:620", Title = "Portal", Screenshots = ["https://cdn.example/a.jpg"],
                    Requirements = [new SystemRequirements { Platform = "Windows", Minimum = "RAM: 8 GB" }]
                });
            });
            var reloaded = new StateRepository(folder);
            await reloaded.LoadAsync();
            var result = await reloaded.ReadAsync();
            Assert.Equal(1234, Assert.Single(result.Library).PlaySeconds);
            Assert.Equal("https://cdn.example/portrait.jpg", Assert.Single(result.Library).Game.PortraitUrl);
            Assert.Null(Assert.Single(result.Library).Game.SteamAppId);
            Assert.Equal(620, Assert.Single(result.SteamCatalog!.Popular).SteamAppId);
            Assert.Single(Assert.Single(result.SteamDetails).Screenshots);
            Assert.Equal("RAM: 8 GB", Assert.Single(Assert.Single(result.SteamDetails).Requirements).Minimum);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static HttpClient Client(string json) => new(new Handler((_, _) => Task.FromResult(Json(json))));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
