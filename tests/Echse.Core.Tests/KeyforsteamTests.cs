using System.Net;
using System.Text;
using Echse.Core;
using Xunit;

namespace Echse.Core.Tests;

public sealed class KeyforsteamTests
{
    [Fact]
    public async Task ReadsPublicAggregateRatherThanFaqAndCachesItsTimestamp()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("/katalog/", request.RequestUri!.AbsolutePath);
            var response = Html("""
                <script type="application/ld+json">
                {"@type":"FAQPage","price":"1.91"}
                </script>
                <script type='application/ld+json'>
                {"@graph":[{"@type":"Product","name":"Brotato","category":{"name":"pc action"},
                  "offers":{"@type":"AggregateOffer","lowPrice":0.29,"priceCurrency":"EUR"}}]}
                </script>
                """);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                "https://www.keyforsteam.de/brotato-key-kaufen-preisvergleich/");
            return response;
        }));
        var service = new KeyforsteamService(client);
        var price = await service.GetPriceAsync("Brotato");
        Assert.Equal(0.29m, price.FromPrice);
        Assert.Equal("EUR", price.Currency);
        Assert.Equal("https://www.keyforsteam.de/brotato-key-kaufen-preisvergleich/", price.Source.AbsoluteUri);
        Assert.Null(price.UnavailableReason);
        Assert.Equal(price, await service.GetPriceAsync("Brotato"));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("Brotato DLC", "pc action", """{"@type":"AggregateOffer","lowPrice":0.29,"priceCurrency":"EUR"}""")]
    [InlineData("Brotato", "xbox action", """{"@type":"AggregateOffer","lowPrice":0.29,"priceCurrency":"EUR"}""")]
    [InlineData("Brotato", "pc action", """{"@type":"AggregateOffer","priceCurrency":"EUR"}""")]
    public async Task DoesNotQuoteMismatchedEditionsConsolesOrMissingPrices(string name, string category, string offers)
    {
        using var client = new HttpClient(new Handler(_ => Html($$"""
            <script type="application/ld+json">
            {"@type":"Product","name":"{{name}}","category":{"name":"{{category}}"},"offers":{{offers}}}
            </script>
            """)));
        var result = await new KeyforsteamService(client).GetPriceAsync("Brotato");
        Assert.Null(result.FromPrice);
        Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
        Assert.Equal(KeyforsteamService.SearchUrl("Brotato"), result.Source);
    }

    [Fact]
    public async Task MissingCandidateHasExplicitUnavailableResultAndEscapedSearchLink()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var result = await new KeyforsteamService(client).GetPriceAsync("A & B: Test");
        Assert.Null(result.FromPrice);
        Assert.Contains("nicht verfügbar", result.UnavailableReason);
        Assert.EndsWith("search_name=A%20%26%20B%3A%20Test", result.Source.AbsoluteUri);
    }

    [Fact]
    public async Task DiscoversRealProductUrlInsteadOfGuessingSlugFromSteamTitle()
    {
        using var client = new HttpClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/katalog/" ? Html("""
                <a href="/anderer-name-key-kaufen-preisvergleich/"><img alt="A &amp; B" src="/cover.jpg"></a>
                <a href="https://other.example/a-b-preisvergleich/">A &amp; B</a>
                """) : Html("""
                <script type="application/ld+json">
                {"@type":"Product","name":"A & B","category":"pc action",
                 "offers":{"@type":"AggregateOffer","lowPrice":"1.29","priceCurrency":"EUR"}}
                </script>
                """)));
        var result = await new KeyforsteamService(client).GetPriceAsync("A & B");
        Assert.Equal(1.29m, result.FromPrice);
        Assert.Equal("/anderer-name-key-kaufen-preisvergleich/", result.Source.AbsolutePath);
    }

    [Fact]
    public async Task NetworkTimeoutIsDistinguishableFromNavigationCancellation()
    {
        using var client = new HttpClient(new Handler(_ => throw new TaskCanceledException("Timeout")));
        await Assert.ThrowsAsync<TimeoutException>(() => new KeyforsteamService(client).GetPriceAsync("Brotato"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KeyforsteamService(client).GetPriceAsync("Brotato", cancelled.Token));
    }

    [Fact]
    public async Task JavascriptOnlySearchRetainsValidatedProductCandidateWithoutInventingPrice()
    {
        using var client = new HttpClient(new Handler(request =>
            request.RequestUri!.AbsolutePath == "/katalog/" ? Html("<html><body></body></html>") : Html("""
                <script type="application/ld+json">
                {"@type":"Product","name":"Brotato","category":"pc action",
                 "offers":{"@type":"AggregateOffer","lowPrice":0.29,"priceCurrency":"EUR"}}
                </script>
                """)));
        var result = await new KeyforsteamService(client).GetPriceAsync("Brotato");
        Assert.Equal(0.29m, result.FromPrice);
        Assert.Equal("/brotato-key-kaufen-preisvergleich/", result.Source.AbsolutePath);
    }

    [Fact]
    public async Task InvalidPriceAndHttpErrorsAreNotSuccesses()
    {
        using var invalid = new HttpClient(new Handler(_ => Html("""
            <script type="application/ld+json">
            {"@type":"Product","name":"Brotato","category":{"name":"pc action"},
              "offers":{"@type":"AggregateOffer","lowPrice":-1,"priceCurrency":"EUR"}}
            </script>
            """)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new KeyforsteamService(invalid).GetPriceAsync("Brotato"));
        using var offline = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new KeyforsteamService(offline).GetPriceAsync("Brotato"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KeyforsteamService(offline).GetPriceAsync("Brotato", cancellation.Token));
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
        { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
