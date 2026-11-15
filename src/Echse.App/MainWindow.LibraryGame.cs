using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Echse.App;

public sealed partial class MainWindow
{
    private StackPanel? libraryNewsHost;
    private TextBlock? libraryStorageValue;
    private TextBlock? libraryStorageHint;
    private Action<LibraryEntry>? refreshLibraryGameStatus;
    private string? libraryReturnGameId;

    private async Task OpenLibraryGameAsync(Game game)
    {
        var version = renderVersion;
        await repository.UpdateAsync(current =>
        {
            if (current.Library.All(entry => entry.Game.Id != game.Id))
                throw new InvalidOperationException("Dieses Spiel befindet sich nicht mehr in deiner Bibliothek.");
            current.Views[game.Id] = Math.Min(1_000_000, current.Views.GetValueOrDefault(game.Id) + 1);
        });
        if (closing || version != renderVersion) return;
        selectedGame = game;
        page = "library-game";
        SelectNavigation("library");
        await RenderAsync();
    }

    private void RenderLibraryGamePage()
    {
        var entry = state.Library.Single(item => item.Game.Id == selectedGame?.Id);
        var game = entry.Game;
        PageHost.Children.Add(LibraryHero(game));
        var body = new StackPanel { Spacing = 20, Margin = new Thickness(24, 20, 24, 24) };
        PageHost.Children.Add(body);

        var actionBar = new Grid { ColumnSpacing = 20 };
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var primary = Button("", async () =>
        {
            var current = (await repository.ReadAsync()).Library.Single(item => item.Game.Id == game.Id);
            if (GameInstallation.IsInstalled(current))
            {
                await tracker.LaunchAsync(game.Id);
                await RenderAsync();
                Notify("Spiel gestartet", "Deine Spielzeit wird erfasst, solange Echse geöffnet bleibt.");
            }
            else if (SourceDownloads(current.Game).Count > 0)
                await ShowSourceDownloadChoiceAsync(current.Game);
            else await DownloadGameAsync(current.Game);
        }, true);
        primary.Width = 190;
        primary.Height = 48;
        primary.FontSize = 18;
        var statistics = new Grid { ColumnSpacing = 16, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < 3; i++)
            statistics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock AddStatistic(string label, int column)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(Text(label, 12, Muted));
            var value = Text("", 16);
            item.Children.Add(value);
            Grid.SetColumn(item, column);
            statistics.Children.Add(item);
            return value;
        }
        var lastPlayed = AddStatistic("ZULETZT GESPIELT", 0);
        var playtime = AddStatistic("GESAMTSPIELZEIT", 1);
        libraryStorageValue = AddStatistic("SPEICHERBEDARF", 2);
        libraryStorageValue.Text = entry.InstallationDirectory is null ? "Ordner auswählen" : "Wird ermittelt …";
        var settings = Button(new FontIcon { Glyph = "\uE713", FontSize = 20 },
            () => ShowLibraryGameSettingsAsync(game.Id));
        settings.Width = settings.Height = 48;
        AutomationProperties.SetName(settings, "Spieleinstellungen");
        ToolTipService.SetToolTip(settings, "Spieleinstellungen");
        Grid.SetColumn(settings, 2);
        actionBar.Children.Add(primary);
        actionBar.Children.Add(statistics);
        actionBar.Children.Add(settings);
        void ArrangeActions(double width)
        {
            var stacked = width < 900;
            Grid.SetColumn(statistics, stacked ? 0 : 1);
            Grid.SetRow(statistics, stacked ? 1 : 0);
            Grid.SetColumnSpan(statistics, stacked ? 3 : 1);
            statistics.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
        }
        ArrangeActions(0);
        actionBar.SizeChanged += (_, args) => ArrangeActions(args.NewSize.Width);
        body.Children.Add(actionBar);
        var status = Text("", 13, Muted);
        body.Children.Add(status);
        refreshLibraryGameStatus = current =>
        {
            var running = tracker.IsRunning(current.Game.Id);
            var installed = GameInstallation.IsInstalled(current);
            var download = GameInstallation.ResolveDownload(current.Game, state.Settings, state.CatalogCache);
            var sourceMatches = SourceDownloads(current.Game).Count;
            var hasSources = state.Settings.SourceUrls.Count > 0;
            var searching = sourceSearches.GetValueOrDefault(current.Game.Id) is { Loading: true } search &&
                SearchMatches(search, current.Game, state.Settings);
            var downloadStatus = sourceMatches > 0
                    ? $"{sourceMatches} Treffer in deinen Quellen · Titel und Quelle vor dem Download prüfen."
                : download.Source is not null ? "Download im Katalog verfügbar · anschließend manuell installieren und die EXE verknüpfen."
                : searching ? "Deine Download-Quellen werden durchsucht …"
                : hasSources ? "In deinen Quellen wurde kein passender Download gefunden."
                : string.IsNullOrWhiteSpace(state.Settings.CatalogUrl)
                    ? "Keine Download-Quellen eingerichtet. Füge in den Einstellungen Quellen hinzu."
                    : download.UnavailableReason!;
            primary.Content = running ? "Spiel läuft" : installed ? "▶  Spielen" : "↓  Installieren";
            primary.IsEnabled = !running && (installed || download.Source is not null || sourceMatches > 0);
            AutomationProperties.SetName(primary, running ? "Spiel läuft" : installed ? "Spiel starten" : "Spiel installieren");
            lastPlayed.Text = FormatDate(current.LastPlayed);
            playtime.Text = Playtime(current.PlaySeconds);
            status.Text = running ? "Wird gerade gespielt · Spielzeit wird automatisch gespeichert."
                : installed ? "Installiert · lokale Spieldatei verknüpft"
                : current.ExecutablePath is not null
                    ? "Die verknüpfte Spieldatei wurde nicht gefunden. " + downloadStatus
                    : downloadStatus;
            ToolTipService.SetToolTip(primary, installed ? current.ExecutablePath
                : sourceMatches > 0 ? "Treffer aus deinen Quellen prüfen und an qBittorrent übergeben."
                : download.Source is not null ? "Download an deinen qBittorrent-Server übergeben. Es wird kein Setup automatisch ausgeführt."
                : downloadStatus);
        };
        refreshLibraryGameStatus(entry);

        var links = Row();
        links.Children.Add(Button("Store-Seite", () => OpenGameAsync(game)));
        links.Children.Add(Button("Downloadquelle aktualisieren", async () =>
        {
            var version = renderVersion;
            await RefreshCatalogAsync();
            if (page == "library-game" && version == renderVersion) await RenderAsync();
        }));
        links.SizeChanged += (_, args) =>
            links.Orientation = args.NewSize.Width < 360 ? Orientation.Vertical : Orientation.Horizontal;
        body.Children.Add(links);
        if (game.SteamAppId is int appId)
            body.Children.Add(StoreLink("Auf Steam ansehen ↗", $"https://store.steampowered.com/app/{appId}/"));
        sourceGameHost = new StackPanel();
        RenderSourceGameContent(game);
        body.Children.Add(Panel(sourceGameHost));

        libraryNewsHost = new StackPanel { Spacing = 12 };
        libraryNewsHost.Children.Add(Text("Neuigkeiten & Updates", 23));
        libraryNewsHost.Children.Add(Text("Entwicklerbeiträge werden geladen …", 14, Muted));
        var aside = new StackPanel { Spacing = 16 };
        var friends = new StackPanel { Spacing = 10 };
        friends.Children.Add(Text("Freunde mit diesem Spiel", 20));
        friends.Children.Add(Text("Noch keine Community-Verbindung", 14, Accent));
        friends.Children.Add(Text("Dein Profil ist lokal. Welche Freunde dieses Spiel besitzen, ist ohne angebundenes Freundesystem nicht verfügbar.", 13, Muted));
        aside.Children.Add(Panel(friends));
        var files = new StackPanel { Spacing = 10 };
        files.Children.Add(Text("Lokale Dateien", 20));
        libraryStorageHint = Text(entry.InstallationDirectory ??
            "Wähle den Installationsordner aus, um den Speicherbedarf zu ermitteln. Eine verknüpfte EXE allein bestimmt den Spielordner nicht zuverlässig.", 13, Muted);
        files.Children.Add(libraryStorageHint);
        files.Children.Add(Button("Installationsordner auswählen", () => SelectGameDirectoryAsync(game.Id)));
        files.Children.Add(Button("Vorhandene EXE verknüpfen", () => LinkExecutableAsync(game.Id)));
        aside.Children.Add(Panel(files));
        body.Children.Add(TwoColumns(libraryNewsHost, aside));
        if (!string.IsNullOrWhiteSpace(game.Description))
            body.Children.Add(SectionHeading("Über dieses Spiel", game.Description));
        body.Children.Add(Text("Bilder und Entwicklerbeiträge: Steam / jeweilige Rechteinhaber. Neuigkeiten sind keine automatisch installierten Patches. Downloads erfolgen ausschließlich über deine eingerichteten Quellen oder deinen Katalog und qBittorrent.", 11, Muted));
    }

    private Grid LibraryHero(Game game)
    {
        var hero = new Grid { Height = 300, Background = Gradient() };
        hero.SizeChanged += (_, args) => hero.Height = Math.Clamp(args.NewSize.Width * 0.32, 180, 420);
        var addresses = new[]
        {
            game.SteamAppId is int id ? $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_hero.jpg" : null,
            game.BannerUrl, game.CoverUrl
        }.Where(address => !string.IsNullOrWhiteSpace(address)).Distinct().ToArray();
        if (addresses.Length > 0)
        {
            var index = 0;
            var image = new Image { Source = new BitmapImage(NetworkAddress.Parse(addresses[0]!)), Stretch = Stretch.UniformToFill };
            image.ImageFailed += (_, args) =>
            {
                if (++index < addresses.Length) image.Source = new BitmapImage(NetworkAddress.Parse(addresses[index]!));
                else ImageFailed(image, args);
            };
            hero.Children.Add(image);
        }
        var shade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        shade.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(30, 0, 0, 0) });
        shade.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(225, 0, 0, 0) });
        hero.Children.Add(new Border { Background = shade });
        var back = Button("←  Bibliothek", async () =>
        {
            page = "library";
            await RenderAsync();
        });
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.VerticalAlignment = VerticalAlignment.Top;
        back.Margin = new Thickness(20, 16, 20, 0);
        back.Background = (Brush)Application.Current.Resources["LauncherLibraryOverlayBrush"];
        hero.Children.Add(back);
        var title = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24) };
        title.Children.Add(Eyebrow("DEINE BIBLIOTHEK"));
        title.Children.Add(Text(game.Title, 36));
        hero.Children.Add(title);
        return hero;
    }

    private async Task RefreshLibraryGameStatusAsync()
    {
        var version = renderVersion;
        var current = await repository.ReadAsync();
        if (version != renderVersion || page != "library-game" || closing) return;
        state = current;
        if (state.Library.FirstOrDefault(item => item.Game.Id == selectedGame?.Id) is { } entry)
            refreshLibraryGameStatus?.Invoke(entry);
    }

    private async Task RefreshLibraryGameDataAsync()
    {
        var entry = state.Library.Single(item => item.Game.Id == selectedGame?.Id);
        var newsHost = libraryNewsHost!;
        var storage = libraryStorageValue!;
        var hint = libraryStorageHint!;
        var token = BeginOnlineRequest();
        var version = renderVersion;
        bool Current() => !token.IsCancellationRequested && version == renderVersion && !closing;
        async Task NewsAsync()
        {
            try
            {
                var news = entry.Game.SteamAppId is int appId ? await steam.GetNewsAsync(appId, token) : null;
                if (!Current()) return;
                newsHost.Children.Clear();
                newsHost.Children.Add(Text("Neuigkeiten & Updates", 23));
                if (news is null || news.Count == 0)
                    newsHost.Children.Add(Text(news is null
                        ? "Für dieses lokale Spiel ist keine Steam-App-ID hinterlegt. Entwicklerbeiträge sind deshalb nicht zuordenbar."
                        : "Für dieses Spiel sind derzeit keine Entwicklerbeiträge verfügbar.", 14, Muted));
                if (news is not null)
                    foreach (var article in news)
                    {
                        var content = new StackPanel { Spacing = 10 };
                        content.Children.Add(Text(FormatDate(article.PublishedAt), 12, Muted));
                        content.Children.Add(Text(article.Title, 20));
                        var summary = Text(article.Summary, 14, Muted);
                        summary.MaxLines = 5;
                        summary.TextTrimming = TextTrimming.CharacterEllipsis;
                        content.Children.Add(summary);
                        content.Children.Add(StoreLink("Originalbeitrag lesen ↗", article.Url.AbsoluteUri));
                        newsHost.Children.Add(Panel(content));
                    }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LocalServices.Log(exception);
                if (!Current()) return;
                newsHost.Children.Clear();
                newsHost.Children.Add(Text("Neuigkeiten & Updates", 23));
                newsHost.Children.Add(Text("Entwicklerbeiträge konnten nicht geladen werden. Deine lokalen Spielinformationen bleiben verfügbar.", 14, Muted));
                newsHost.Children.Add(Button("Erneut versuchen", RenderAsync));
            }
        }
        async Task StorageAsync()
        {
            if (entry.InstallationDirectory is null) return;
            try
            {
                var bytes = await GameInstallation.MeasureDirectoryAsync(entry.InstallationDirectory, token);
                if (!Current()) return;
                storage.Text = FormatStorage(bytes);
                hint.Text = entry.InstallationDirectory + "\nDateigrößen inklusive Unterordnern, ohne Datei- und Ordnerverknüpfungen.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LocalServices.Log(exception);
                if (!Current()) return;
                storage.Text = "Nicht ermittelbar";
                hint.Text = "Speicherbedarf konnte nicht gelesen werden: " + exception.Message;
            }
        }
        await Task.WhenAll(NewsAsync(), StorageAsync());
    }

    private async Task ShowLibraryGameSettingsAsync(string gameId)
    {
        var entry = (await repository.ReadAsync()).Library.Single(item => item.Game.Id == gameId);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Text(entry.Game.Title, 22));
        content.Children.Add(Text(entry.ExecutablePath ?? "Noch keine lokale EXE verknüpft.", 12, Muted));
        var executable = Button("EXE auswählen", () => LinkExecutableAsync(gameId));
        executable.IsEnabled = !tracker.IsRunning(gameId);
        content.Children.Add(executable);
        content.Children.Add(Text(entry.InstallationDirectory ?? "Noch kein Installationsordner ausgewählt.", 12, Muted));
        content.Children.Add(Button("Installationsordner auswählen", () => SelectGameDirectoryAsync(gameId)));
        if (entry.InstallationDirectory is { } directory)
            content.Children.Add(Button("Ordner öffnen", async () =>
            {
                if (!await Windows.System.Launcher.LaunchFolderPathAsync(directory))
                    throw new IOException("Der Installationsordner konnte nicht geöffnet werden.");
            }));
        content.Children.Add(Button("Online-Cover auswählen", async () =>
        {
            await CloseDialogAsync();
            await ChooseArtworkAsync(entry.Game);
        }));
        content.Children.Add(Button(entry.Favorite ? "Favorit entfernen" : "Als Favorit markieren", async () =>
        {
            await repository.UpdateAsync(current =>
            {
                var item = current.Library.Single(item => item.Game.Id == gameId);
                item.Favorite = !item.Favorite;
            });
            await CloseDialogAsync();
            await RenderAsync();
        }));
        var remove = Button("Aus Bibliothek entfernen", () => RemoveLibraryGameAsync(gameId));
        remove.IsEnabled = !tracker.IsRunning(gameId);
        content.Children.Add(remove);
        await ShowDialogAsync(new ContentDialog
        {
            Title = "Spieleinstellungen", CloseButtonText = "Schließen",
            Content = new ScrollViewer { Content = content, MaxHeight = 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        });
    }

    private async Task LinkExecutableAsync(string gameId)
    {
        await CloseDialogAsync();
        if (tracker.IsRunning(gameId)) throw new InvalidOperationException("Das Spiel läuft bereits.");
        var path = await PickAsync(".exe");
        if (path is null || closing) return;
        await repository.UpdateAsync(current =>
            current.Library.Single(item => item.Game.Id == gameId).ExecutablePath = path);
        await RenderAsync();
        Notify("Spieldatei verknüpft", "Das Spiel kann jetzt über „Spielen“ gestartet werden.");
    }

    private async Task SelectGameDirectoryAsync(string gameId)
    {
        await CloseDialogAsync();
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var directory = await picker.PickSingleFolderAsync();
        if (directory is null || closing) return;
        var path = GameInstallation.ValidateDirectory(directory.Path);
        await repository.UpdateAsync(current =>
            current.Library.Single(item => item.Game.Id == gameId).InstallationDirectory = path);
        await RenderAsync();
    }

    private async Task RemoveLibraryGameAsync(string gameId)
    {
        if (!await ConfirmAsync("Spiel entfernen?",
                "Spieldateien bleiben erhalten. Die lokal gespeicherte Spielzeit und Favoritenmarkierung werden entfernt.", "Entfernen"))
            return;
        if (tracker.IsRunning(gameId)) throw new InvalidOperationException("Laufende Spiele können nicht entfernt werden.");
        await repository.UpdateAsync(current => current.Library.RemoveAll(item => item.Game.Id == gameId));
        if (page == "library-game") page = "library";
        await RenderAsync();
    }

    private async Task DownloadGameAsync(Game game)
    {
        var version = renderVersion;
        var token = onlineRequest?.Token ?? BeginOnlineRequest();
        try
        {
            var snapshot = await repository.ReadAsync();
            if (string.IsNullOrWhiteSpace(snapshot.Settings.CatalogUrl))
                throw new InvalidOperationException("Installationen sind im Demo-Modus deaktiviert.");
            var fetched = await new CatalogService(http).FetchAsync(snapshot.Settings.CatalogUrl, token);
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            await repository.UpdateAsync(current =>
            {
                if (current.Settings.CatalogUrl != snapshot.Settings.CatalogUrl)
                    throw new InvalidOperationException("Die Downloadquelle wurde geändert. Bitte erneut versuchen.");
                current.CatalogCache = fetched;
                current.CatalogUpdatedAt = DateTimeOffset.UtcNow;
            });
            catalog = fetched;
            var download = GameInstallation.ResolveDownload(game, snapshot.Settings, fetched);
            if (download.Source is not { } source)
            {
                ShowNotice("Kein Download verfügbar", download.UnavailableReason!, InfoBarSeverity.Warning);
                await RenderAsync();
                return;
            }
            if (!await ConfirmAsync("Download zur Installation starten?",
                    $"„{game.Title}“ wird über {snapshot.Settings.QBitUrl} heruntergeladen. Lade nur Inhalte, für die du die nötigen Rechte besitzt. Anschließend installierst du das Spiel manuell und verknüpfst seine EXE. Es wird kein Setup automatisch ausgeführt.",
                    "Download starten")) return;
            token.ThrowIfCancellationRequested();
            var current = await repository.ReadAsync();
            if (current.Settings.CatalogUrl != snapshot.Settings.CatalogUrl ||
                current.Settings.QBitUrl != snapshot.Settings.QBitUrl)
                throw new InvalidOperationException("Die Serververbindung wurde geändert. Bitte erneut versuchen.");
            state = current;
            using var client = CreateQBitClient();
            await client.AddAsync(source.MagnetUri!, token);
            Notify("Download übergeben", "Den Fortschritt findest du unter Downloads. Danach manuell installieren und die EXE in den Spieleinstellungen verknüpfen.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static string FormatStorage(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N1} {units[unit]}";
    }
}
