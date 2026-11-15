using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Echse.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? onlineRequest;
    private int renderVersion;
    private bool useServerCatalog;
    private string steamQuery = "";
    private IReadOnlyList<Game>? steamSearchResults;
    private string? steamError;
    private Game? selectedGame;
    private string? gameDetailsError;
    private bool gameDetailsLoading;
    private string gameOriginPage = "store";

    private void CancelOnlineRequest()
    {
        onlineRequest?.Cancel();
        onlineRequest?.Dispose();
        onlineRequest = null;
    }

    private CancellationToken BeginOnlineRequest()
    {
        CancelOnlineRequest();
        onlineRequest = new CancellationTokenSource();
        return onlineRequest.Token;
    }

    private void UpdateCatalogSidebar()
    {
        var games = useServerCatalog ? catalog : steamSearchResults ??
            state.SteamCatalog?.AllGames().OrderBy(game => game.Title).ToList() ?? [];
        CatalogGameList.ItemsSource = games;
    }

    private async void CatalogGameList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is Game game) await RunAsync(() => OpenGameAsync(game));
    }

    private void RenderStore()
    {
        var source = new ComboBox
        {
            ItemsSource = new[] { "Steam · Online-Katalog", "Eigener Download-Katalog" },
            SelectedIndex = useServerCatalog ? 1 : 0, Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        source.SelectionChanged += async (_, _) =>
        {
            useServerCatalog = source.SelectedIndex == 1;
            await RunAsync(RenderAsync);
        };
        if (useServerCatalog)
        {
            PageHost.Children.Add(source);
            RenderServerStore();
            return;
        }

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var refresh = Button("Aktualisieren", RefreshSteamHomeAsync);
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(Text("Entdecken", 28));
        header.Children.Add(source);
        header.Children.Add(refresh);
        void ArrangeHeader(double width)
        {
            var wide = width >= 600;
            var compact = width < 420;
            source.Width = Math.Min(240, Math.Max(0, width));
            Grid.SetColumn(source, wide ? 1 : 0);
            Grid.SetRow(source, wide ? 0 : 1);
            Grid.SetColumnSpan(source, wide ? 1 : compact ? 3 : 2);
            Grid.SetColumn(refresh, compact ? 0 : 2);
            Grid.SetRow(refresh, wide ? 0 : compact ? 2 : 1);
            Grid.SetColumnSpan(refresh, compact ? 3 : 1);
            source.Margin = new Thickness(0, wide ? 0 : 8, 0, 0);
            refresh.Margin = new Thickness(0, wide ? 0 : 8, 0, 0);
        }
        ArrangeHeader(0);
        header.SizeChanged += (_, args) => ArrangeHeader(args.NewSize.Width);
        PageHost.Children.Add(header);
        PageHost.Children.Add(Text("Deine nächste Spielewelt. Live-Metadaten von Steam · Downloads separat.", 12, Muted));
        var searchRow = new Grid { ColumnSpacing = 10 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox
        {
            Text = steamQuery, MaxLength = 200,
            PlaceholderText = "Steam durchsuchen – z. B. Portal, Elden Ring oder eine App-ID …"
        };
        search.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true;
            await RunAsync(() => SearchSteamAsync(search.Text));
        };
        var searchButton = Button("Suchen", () => SearchSteamAsync(search.Text), true);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(search);
        searchRow.Children.Add(searchButton);
        PageHost.Children.Add(searchRow);

        if (steamError is not null)
            PageHost.Children.Add(Panel(Text(steamError, 14, Muted)));
        if (steamSearchResults is not null)
        {
            var title = Row();
            title.Children.Add(Text($"Suchergebnisse für „{steamQuery}“", 22));
            title.Children.Add(Button("Zur Übersicht", async () =>
            {
                steamQuery = "";
                steamSearchResults = null;
                await RenderAsync();
            }));
            PageHost.Children.Add(title);
            PageHost.Children.Add(Text("Steam liefert eine begrenzte Trefferauswahl. Präzisiere den Namen oder öffne alle Treffer im Steam-Store. Auch DLCs und Demos können enthalten sein.", 12, Muted));
            PageHost.Children.Add(steamSearchResults.Count == 0
                ? Empty("Keine Treffer", "Versuche einen anderen Namen oder eine Steam-App-ID.")
                : GameGrid(steamSearchResults));
            PageHost.Children.Add(StoreLink("Alle Treffer auf Steam ansehen",
                "https://store.steampowered.com/search/?term=" + Uri.EscapeDataString(steamQuery)));
            return;
        }

        var snapshot = state.SteamCatalog;
        if (snapshot is null)
        {
            PageHost.Children.Add(Empty("Dein Online-Katalog", steamError is null
                ? "Steam-Inhalte werden geladen. Du kannst bereits nach einem Spiel suchen."
                : "Keine gespeicherten Steam-Inhalte verfügbar. Mit „Aktualisieren“ erneut versuchen oder direkt suchen."));
            return;
        }
        PageHost.Children.Add(Text($"STEAM-AUSWAHL  ·  Stand {FormatDate(snapshot.FetchedAt)}", 11, Muted));
        if (snapshot.NewReleases.Count > 0)
            PageHost.Children.Add(Shelf("Neu auf Steam", "Aktuelle Veröffentlichungen aus dem Steam-Store", snapshot.NewReleases, false));
        if (snapshot.Popular.Count > 0)
            PageHost.Children.Add(Shelf("Beliebt auf Steam", "Die aktuelle Steam-Bestseller-Auswahl", snapshot.Popular, true));

        var detailedCatalog = snapshot.AllGames().Select(game =>
            state.SteamDetails.FirstOrDefault(detail => detail.Id == game.Id) ?? game).ToList();
        var suggestions = Recommendations.ForUser(detailedCatalog, state, 8);
        if (suggestions.Count > 0)
            PageHost.Children.Add(Shelf("Für dich entdecken",
                state.Library.Count == 0 && state.Views.Count == 0
                    ? "Zum Einstieg: eine Auswahl aus den geladenen Steam-Titeln"
                    : "Lokal gewichtet nach Aufrufen, Favoriten und bekannten Genres", suggestions, true));
        if (snapshot.Specials.Count > 0)
            PageHost.Children.Add(Shelf("Gerade im Angebot", "Aktionsauswahl von Steam · Preise und Verfügbarkeit können sich ändern", snapshot.Specials, false));
        if (snapshot.Upcoming.Count > 0)
            PageHost.Children.Add(Shelf("Am Horizont", "Demnächst auf Steam", snapshot.Upcoming, true));
        PageHost.Children.Add(SectionHeading("Noch nicht das Richtige dabei?",
            "Die Startseite ist eine Auswahl, kein vollständiger Download des Steam-Katalogs. Über die Online-Suche findest du weitere Titel."));
        PageHost.Children.Add(StoreLink("Im gesamten Steam-Store stöbern", "https://store.steampowered.com/search/"));
    }

    private FrameworkElement Shelf(string title, string subtitle, IEnumerable<Game> games, bool portrait)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(SectionHeading(title, subtitle));
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var game in games)
        {
            var width = portrait ? 152d : 246d;
            var body = new StackPanel { Width = width, Spacing = 9 };
            var portraitUrl = portrait
                ? game.PortraitUrl ?? (game.SteamAppId is int id ? SteamCatalogService.PortraitUrl(id) : null) : null;
            body.Children.Add(Cover(game, width, portrait ? 228 : 138, portraitUrl, fallbackToCover: portrait));
            var name = Text(game.Title, 13);
            name.MaxLines = 1;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(name);
            var label = Text(state.Library.Any(entry => entry.Game.Id == game.Id) ? "IN DEINER BIBLIOTHEK" : "STEAM", 10, Muted);
            body.Children.Add(label);
            var card = Button(body, () => OpenGameAsync(game));
            card.Padding = new Thickness(0);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            card.BorderThickness = new Thickness(0);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, game.Title + " – Spielseite öffnen");
            cards.Children.Add(card);
        }
        section.Children.Add(new ScrollViewer
        {
            Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 0, 4)
        });
        return section;
    }

    private async Task RefreshSteamHomeAsync()
    {
        var token = BeginOnlineRequest();
        var version = renderVersion;
        try
        {
            var fetched = await steam.GetHomeAsync(token);
            token.ThrowIfCancellationRequested();
            await repository.UpdateAsync(current => current.SteamCatalog = fetched);
            steamError = null;
            if (!closing && page == "store" && renderVersion == version)
                await RenderAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || closing) return;
            steamError = "Steam konnte nicht geladen werden. Ein vorhandener Snapshot bleibt sichtbar. Bitte erneut versuchen.";
            if (!closing && page == "store" && renderVersion == version) await RenderAsync();
            ShowError(exception);
        }
    }

    private async Task SearchSteamAsync(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            steamQuery = "";
            steamSearchResults = null;
            await RenderAsync();
            return;
        }
        if (int.TryParse(query, out var appId) && appId > 0)
        {
            await OpenGameAsync(new Game { Id = $"steam:{appId}", SteamAppId = appId, Title = $"Steam-App {appId}" });
            return;
        }
        var token = BeginOnlineRequest();
        var version = renderVersion;
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 };
        PageHost.Children.Insert(0, progress);
        try
        {
            var results = await steam.SearchAsync(query, token);
            if (token.IsCancellationRequested || version != renderVersion || page != "store") return;
            steamQuery = query;
            steamSearchResults = results;
            steamError = null;
            await RenderAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { PageHost.Children.Remove(progress); }
    }

    private async Task OpenGameAsync(Game game)
    {
        var previousVersion = renderVersion;
        if (page == "library-game") libraryReturnGameId = selectedGame?.Id;
        if (page != "game") gameOriginPage = page;
        await repository.UpdateAsync(current =>
            current.Views[game.Id] = Math.Min(1_000_000, current.Views.GetValueOrDefault(game.Id) + 1));
        if (previousVersion != renderVersion || closing) return;
        state = await repository.ReadAsync();
        selectedGame = state.SteamDetails.FirstOrDefault(detail => detail.Id == game.Id) ?? game;
        selectedGalleryIndex = 0;
        SelectNavigation("store");
        gameDetailsError = null;
        gameDetailsLoading = game.SteamAppId is not null;
        page = "game";
        await RenderAsync();
        if (closing || page != "game" || selectedGame?.Id != game.Id) return;
        var sourceGame = selectedGame;
        _ = RunAsync(() => EnsureSourceSearchAsync(sourceGame));
        if (game.SteamAppId is not int id)
            return;
        var token = BeginOnlineRequest();
        var version = renderVersion;
        try
        {
            var details = await steam.GetDetailsAsync(id, token);
            token.ThrowIfCancellationRequested();
            details = details with { Id = game.Id, MagnetUri = game.MagnetUri, Popularity = game.Popularity };
            await repository.UpdateAsync(current =>
            {
                current.SteamDetails.RemoveAll(item => item.Id == game.Id);
                current.SteamDetails.Insert(0, details);
                if (current.SteamDetails.Count > 100)
                    current.SteamDetails.RemoveRange(100, current.SteamDetails.Count - 100);
                var existing = current.Library.FirstOrDefault(entry => entry.Game.Id == game.Id);
                if (existing is not null)
                    existing.Game = details with
                    {
                        MagnetUri = existing.Game.MagnetUri,
                        CoverUrl = existing.Game.CoverUrl ?? details.CoverUrl,
                        PortraitUrl = existing.Game.PortraitUrl ?? details.PortraitUrl
                    };
            });
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            selectedGame = details;
            gameDetailsLoading = false;
            _ = RunAsync(() => EnsureSourceSearchAsync(details));
            await RenderAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            gameDetailsLoading = false;
            gameDetailsError = "Details konnten nicht aktualisiert werden. Angezeigt werden nur bereits bekannte Daten.";
            await RenderAsync();
            ShowError(exception);
        }
    }

    private static void AddFact(StackPanel parent, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parent.Children.Add(Text(label + "  ·  " + value, 14, Muted));
    }

    private static HyperlinkButton StoreLink(string title, string url) => new()
    {
        Content = title, NavigateUri = new Uri(url), HorizontalAlignment = HorizontalAlignment.Left
    };
}
