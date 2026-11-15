using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echse.Core;

public sealed class QBitClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly string username;
    private readonly string password;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool authenticated;

    public QBitClient(string address, string username, string password, HttpMessageHandler? handler = null)
    {
        var uri = NetworkAddress.Parse(address);
        if (!string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Die qBittorrent-Adresse darf keine Query-Parameter enthalten.");
        baseUri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        this.username = username;
        this.password = password;
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Referrer = baseUri;
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("api/v2/app/version", null, cancellationToken);
    }

    public async Task AddAsync(string magnet, CancellationToken cancellationToken = default)
    {
        MagnetLink.Validate(magnet);
        using var response = await SendAsync("api/v2/torrents/add", new Dictionary<string, string>
        {
            ["urls"] = magnet,
            ["tags"] = "echse"
        }, cancellationToken);
        if (!(await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Equals("Ok.", StringComparison.Ordinal))
            throw new InvalidOperationException("qBittorrent hat den Download nicht angenommen.");
    }

    public async Task<IReadOnlyList<TorrentStatus>> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("api/v2/torrents/info?tag=echse", null, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var torrents = await JsonSerializer.DeserializeAsync<List<TorrentDto>>(stream,
            StateRepository.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("qBittorrent lieferte keine gültige Download-Liste.");
        return torrents.Select(item => new TorrentStatus(item.Name, item.Progress, item.State, item.Speed)).ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(string route, Dictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!authenticated)
                    await LoginAsync(cancellationToken);
                using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post,
                    new Uri(baseUri, route));
                if (form is not null)
                    request.Content = new FormUrlEncodedContent(form);
                var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
                {
                    authenticated = false;
                    response.Dispose();
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"qBittorrent-Anfrage fehlgeschlagen (HTTP {(int)status}).");
                }
                return response;
            }
            throw new InvalidOperationException("Die qBittorrent-Sitzung konnte nicht erneuert werden.");
        }
        finally { gate.Release(); }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password
        });
        using var response = await client.PostAsync(new Uri(baseUri, "api/v2/auth/login"), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (!(await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Equals("Ok.", StringComparison.Ordinal))
            throw new InvalidOperationException("qBittorrent-Anmeldung fehlgeschlagen. Benutzername und Passwort prüfen.");
        authenticated = true;
    }

    public void Dispose()
    {
        client.Dispose();
        gate.Dispose();
    }

    private sealed record TorrentDto
    {
        public string Name { get; init; } = "";
        public double Progress { get; init; }
        public string State { get; init; } = "";
        [JsonPropertyName("dlspeed")]
        public long Speed { get; init; }
    }
}
