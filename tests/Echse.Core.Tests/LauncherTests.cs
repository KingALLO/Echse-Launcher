using System.Net;
using System.Text;
using Echse.Core;
using Xunit;

namespace Echse.Core.Tests;

public sealed class LauncherTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task RepositoryPersistsConcurrentUpdatesAndKeepsLastCommitOnFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "echse-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new StateRepository(folder);
            await repository.LoadAsync();
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => repository.UpdateAsync(state =>
                state.Views["game"] = state.Views.GetValueOrDefault("game") + 1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateAsync(state =>
            {
                state.Profile.DisplayName = "Nicht speichern";
                throw new InvalidOperationException("Abbruch");
            }));
            var reloaded = new StateRepository(folder);
            await reloaded.LoadAsync();
            var state = await reloaded.ReadAsync();
            Assert.Equal(25, state.Views["game"]);
            Assert.Equal("Spieler", state.Profile.DisplayName);
            state.Profile.DisplayName = "Nur eine Kopie";
            Assert.Equal("Spieler", (await reloaded.ReadAsync()).Profile.DisplayName);
            Assert.False(File.Exists(Path.Combine(folder, "state.json.tmp")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task CorruptStateIsNotSilentlyReplaced()
    {
        var folder = Path.Combine(Path.GetTempPath(), "echse-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "state.json");
            await File.WriteAllTextAsync(file, "{broken");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new StateRepository(folder).LoadAsync());
            Assert.Equal("{broken", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void RecommendationsUseInterestAndExcludeOwnedGames()
    {
        var owned = new Game { Id = "owned", Title = "Owned", Genre = "Strategie", Tags = ["Aufbau"] };
        var matching = new Game { Id = "match", Title = "Match", Genre = "Strategie", Popularity = 1 };
        var popular = new Game { Id = "popular", Title = "Popular", Genre = "Action", Popularity = 999 };
        var state = new LauncherState
        {
            Library = [new LibraryEntry { Game = owned, PlaySeconds = 7200, Favorite = true }]
        };
        Assert.Equal("match", Recommendations.ForUser([owned, popular, matching], state).First().Id);
        Assert.DoesNotContain(Recommendations.ForUser([owned, popular, matching], state), game => game.Id == "owned");
        Assert.Equal("popular", Recommendations.ForUser([matching, popular], new LauncherState()).First().Id);
    }

    [Fact]
    public void ViewsInfluenceRecommendationsWithoutPlaying()
    {
        var viewed = new Game { Id = "viewed", Title = "Viewed", Genre = "Simulation" };
        var matching = new Game { Id = "match", Title = "Match", Genre = "Simulation", Popularity = 3 };
        var popular = new Game { Id = "popular", Title = "Popular", Genre = "Action", Popularity = 100 };
        var state = new LauncherState { Views = new() { ["viewed"] = 4 } };
        Assert.Equal("match", Recommendations.ForUser([viewed, matching, popular], state).First().Id);
    }

    [Theory]
    [InlineData("file:///C:/test")]
    [InlineData("https://user:password@example.org/games")]
    [InlineData("not-a-url")]
    public void RejectsUnsafeNetworkAddresses(string address) =>
        Assert.Throws<ArgumentException>(() => NetworkAddress.Parse(address));

    [Theory]
    [InlineData("magnet:?xt=urn:btih:bad")]
    [InlineData("https://example.org/game.exe")]
    [InlineData("magnet:?dn=test")]
    [InlineData("magnet:?xt=urn:btih:11111111111111111111111111111111")]
    public void RejectsInvalidMagnets(string magnet) =>
        Assert.Throws<ArgumentException>(() => MagnetLink.Validate(magnet));

    [Fact]
    public void NormalizesHexAndBase32MagnetHashesToTheSameValue()
    {
        var base32 = "magnet:?xt=urn:btih:aerukz4jvpg66ajdivtytk6n54asgrlh";
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", MagnetLink.GetInfoHash(Magnet));
        Assert.Equal(MagnetLink.GetInfoHash(Magnet), MagnetLink.GetInfoHash(base32));
    }

    [Fact]
    public async Task CatalogValidatesShapeDuplicateIdsAndSize()
    {
        using var valid = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(
            """[{"id":"one","title":"One","genre":"Action","tags":["Arena"],"popularity":42}]"""))));
        var games = await new CatalogService(valid).FetchAsync("https://catalog.example/games");
        Assert.Single(games);
        Assert.Equal(42, games[0].Popularity);
        Assert.Throws<InvalidDataException>(() => CatalogService.Validate([games[0], games[0]]));
        Assert.Throws<InvalidDataException>(() => CatalogService.Validate([new Game { Id = "empty" }]));
        using var large = new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(new string(' ', 4 * 1024 * 1024 + 1)))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CatalogService(large).FetchAsync("https://catalog.example/games"));
    }

    [Fact]
    public async Task QBitAuthenticatesRetriesExpiredSessionAndPostsMagnetWithTag()
    {
        var loginCount = 0;
        var addCount = 0;
        using var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal("https://server.example/qbit/", request.Headers.Referrer?.AbsoluteUri);
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                loginCount++;
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("username=user", form);
                Assert.Contains("password=secret", form);
                return Text("Ok.");
            }
            Assert.Equal("/qbit/api/v2/torrents/add", request.RequestUri.AbsolutePath);
            addCount++;
            if (addCount == 1) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("tags=echse", body);
            Assert.Contains("urls=magnet", body);
            Assert.Contains("savepath=" + Uri.EscapeDataString(Path.Combine(Path.GetTempPath(), "Half_Life_1", "Download")), body);
            Assert.Contains("autoTMM=false", body);
            return Text("Ok.");
        });
        using var client = new QBitClient("https://server.example/qbit", "user", "secret", handler);
        await client.AddAsync(Magnet, Path.Combine(Path.GetTempPath(), "Half_Life_1", "Download"));
        Assert.Equal(2, loginCount);
        Assert.Equal(2, addCount);
    }

    [Fact]
    public async Task QBitReportsLoginAndAddFailures()
    {
        using var badLogin = new QBitClient("http://localhost:8080", "user", "bad",
            new StubHandler((_, _) => Task.FromResult(Text("Fails."))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => badLogin.TestAsync());
        using var rejected = new QBitClient("http://localhost:8080", "", "",
            new StubHandler((request, _) => Task.FromResult(Text(
                request.RequestUri!.AbsolutePath.EndsWith("/auth/login") ? "Ok." : "Fails."))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.AddAsync(Magnet));
    }

    [Fact]
    public async Task QBitFiltersDownloadsByTagAndReadsSpeed()
    {
        using var client = new QBitClient("http://localhost:8080", "", "",
            new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                    return Task.FromResult(Text("Ok."));
                Assert.Equal("?tag=echse", request.RequestUri.Query);
                return Task.FromResult(Json("""[{"name":"Game","progress":0.5,"state":"downloading","dlspeed":2048}]"""));
            }));
        var torrent = Assert.Single(await client.GetDownloadsAsync());
        Assert.Equal(0.5, torrent.Progress);
        Assert.Equal(2048, torrent.DownloadSpeed);
    }

    [Fact]
    public async Task QBitLooksUpTheExactTorrentEvenWithoutTheEchseTag()
    {
        var hash = MagnetLink.GetInfoHash(Magnet);
        using var client = new QBitClient("http://localhost:8080", "", "",
            new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login")) return Task.FromResult(Text("Ok."));
                Assert.Equal("?hashes=" + hash.ToLowerInvariant(), request.RequestUri.Query);
                return Task.FromResult(Json($$"""
                    [{"hash":"{{hash.ToLowerInvariant()}}","name":"Half-Life","progress":1,"state":"stalledUP",
                      "amount_left":0,"save_path":"C:\\Games\\Half_Life\\Download"}]
                    """));
            }));
        var torrent = await client.GetTorrentAsync(hash);
        Assert.NotNull(torrent);
        Assert.True(torrent.IsComplete);
        Assert.Equal(@"C:\Games\Half_Life\Download", torrent.SavePath);
    }

    [Fact]
    public async Task QBitPollingReadsCurrentProgressSizeSpeedAndEtaForTheLinkedHash()
    {
        var hash = MagnetLink.GetInfoHash(Magnet);
        var snapshots = new Queue<string>(
        [
            $$"""[{"hash":"{{hash}}","name":"Game","progress":0.533,"state":"downloading","dlspeed":10276045,"total_size":12000000000,"completed":6396000000,"amount_left":5604000000,"eta":545}]""",
            $$"""[{"hash":"{{hash}}","name":"Game","progress":0.817,"state":"downloading","dlspeed":8388608,"total_size":12000000000,"completed":9804000000,"amount_left":2196000000,"eta":262}]""",
            $$"""[{"hash":"{{hash}}","name":"Game","progress":1,"state":"checkingUP","dlspeed":0,"total_size":12000000000,"completed":12000000000,"amount_left":0,"eta":8640000}]""",
            $$"""[{"hash":"{{hash}}","name":"Game","progress":1,"state":"stalledUP","dlspeed":0,"total_size":12000000000,"completed":12000000000,"amount_left":0,"eta":8640000}]"""
        ]);
        using var client = new QBitClient("http://localhost:8080", "", "",
            new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login")) return Task.FromResult(Text("Ok."));
                Assert.Equal("?hashes=" + hash.ToLowerInvariant(), request.RequestUri.Query);
                return Task.FromResult(Json(snapshots.Dequeue()));
            }));
        var first = await client.GetTorrentAsync(hash);
        Assert.NotNull(first);
        Assert.Equal(0.533, first.Progress);
        Assert.Equal(12000000000, first.TotalSize);
        Assert.Equal(6396000000, first.CompletedBytes);
        Assert.Equal(10276045, first.DownloadSpeed);
        Assert.Equal(545, first.EtaSeconds);
        var second = await client.GetTorrentAsync(hash);
        Assert.NotNull(second);
        Assert.Equal(0.817, second.Progress);
        Assert.Equal(9804000000, second.CompletedBytes);
        Assert.Equal(262, second.EtaSeconds);
        Assert.False(second.IsComplete);
        var checking = await client.GetTorrentAsync(hash);
        Assert.NotNull(checking);
        Assert.Equal(1, checking.Progress);
        Assert.False(checking.IsComplete);
        Assert.True((await client.GetTorrentAsync(hash))!.IsComplete);
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task QBitPollingDoesNotSubstituteOldProgressOnConnectionFailureOrAMissingTorrent()
    {
        var hash = MagnetLink.GetInfoHash(Magnet);
        var poll = 0;
        using var client = new QBitClient("http://localhost:8080", "", "",
            new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login")) return Task.FromResult(Text("Ok."));
                poll++;
                return Task.FromResult(poll switch
                {
                    1 => Json($$"""[{"hash":"{{hash}}","name":"Game","progress":0.533,"completed":533,"total_size":1000,"eta":30}]"""),
                    2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                    3 => Json("[]"),
                    _ => Json($$"""[{"hash":"{{hash}}","name":"Game","progress":0.9,"completed":900,"total_size":1000,"eta":5}]""")
                });
            }));
        Assert.Equal(533, (await client.GetTorrentAsync(hash))!.CompletedBytes);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTorrentAsync(hash));
        Assert.Null(await client.GetTorrentAsync(hash));
        var recovered = await client.GetTorrentAsync(hash);
        Assert.NotNull(recovered);
        Assert.Equal(0.9, recovered.Progress);
        Assert.Equal(900, recovered.CompletedBytes);
        Assert.Equal(5, recovered.EtaSeconds);
    }

    [Fact]
    public async Task MissingExecutableDoesNotChangeLaunchStatistics()
    {
        var folder = Path.Combine(Path.GetTempPath(), "echse-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new StateRepository(folder);
            await repository.UpdateAsync(state => state.Library.Add(new LibraryEntry
            {
                Game = new Game { Id = "missing", Title = "Missing" },
                ExecutablePath = Path.Combine(folder, "missing.exe")
            }));
            var tracker = new PlayTracker(repository);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.LaunchAsync("missing"));
            var entry = Assert.Single((await repository.ReadAsync()).Library);
            Assert.Null(entry.LastPlayed);
            Assert.Equal(0, entry.LaunchCount);
            Assert.False(tracker.IsRunning("missing"));
            await tracker.StopAsync();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task RealGameExitPersistsElapsedTimeAndLastLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "echse-test-" + Guid.NewGuid().ToString("N"));
        var repository = new StateRepository(folder);
        var tracker = new PlayTracker(repository);
        var errors = new List<Exception>();
        tracker.Error += exception => { lock (errors) errors.Add(exception); };
        try
        {
            await AddFixtureAsync(repository);
            var before = DateTimeOffset.UtcNow;
            await tracker.LaunchAsync("fixture");
            await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.LaunchAsync("fixture"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (tracker.IsRunning("fixture"))
                await Task.Delay(20, timeout.Token);
            var reloaded = new StateRepository(folder);
            await reloaded.LoadAsync();
            var entry = Assert.Single((await reloaded.ReadAsync()).Library);
            Assert.Equal(1, entry.LaunchCount);
            Assert.NotNull(entry.LastPlayed);
            Assert.InRange(entry.LastPlayed!.Value, before, DateTimeOffset.UtcNow);
            Assert.InRange(entry.PlaySeconds, 1.8, 8);
            Assert.Empty(errors);
        }
        finally
        {
            await tracker.StopAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ClosingTrackerFlushesTimeAndDoesNotKeepCrediting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "echse-test-" + Guid.NewGuid().ToString("N"));
        var repository = new StateRepository(folder);
        var tracker = new PlayTracker(repository);
        var errors = new List<Exception>();
        tracker.Error += exception => { lock (errors) errors.Add(exception); };
        try
        {
            await AddFixtureAsync(repository);
            await tracker.LaunchAsync("fixture");
            await Task.Delay(150);
            await tracker.StopAsync();
            var entry = Assert.Single((await repository.ReadAsync()).Library);
            Assert.InRange(entry.PlaySeconds, 0.1, 1.8);
            Assert.False(tracker.IsRunning("fixture"));
            await Task.Delay(2200);
            Assert.Equal(entry.PlaySeconds, (await repository.ReadAsync()).Library.Single().PlaySeconds);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.LaunchAsync("fixture"));
            Assert.Empty(errors);
        }
        finally
        {
            await tracker.StopAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static Task AddFixtureAsync(StateRepository repository) =>
        repository.UpdateAsync(state => state.Library.Add(new LibraryEntry
        {
            Game = new Game { Id = "fixture", Title = "Test Game" },
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "GameFixture", "Echse.TestGame.exe")
        }));

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
