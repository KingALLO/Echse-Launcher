using System.Globalization;
using Echse.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Echse.App;

public sealed partial class MainWindow : Window
{
    private readonly StateRepository repository = new(LocalServices.DataDirectory);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly PlayTracker tracker;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LauncherState state = new();
    private List<Game> catalog = [];
    private string page = "store";
    private bool ready;
    private bool closing;
    private bool downloadsLoading;
    private StackPanel? downloadList;
    private TextBlock? downloadUpdated;
    private ContentDialog? activeDialog;
    private TaskCompletionSource? dialogClosed;
    private readonly SteamCatalogService steam;
    private readonly KeyforsteamService keyforsteam;
    private static SolidColorBrush Accent => (SolidColorBrush)Application.Current.Resources["LauncherAccentBrush"];
    private static SolidColorBrush Muted => (SolidColorBrush)Application.Current.Resources["LauncherMutedBrush"];
    private static SolidColorBrush Surface => (SolidColorBrush)Application.Current.Resources["LauncherPanelBrush"];
    private static SolidColorBrush SurfaceBorder => (SolidColorBrush)Application.Current.Resources["LauncherBorderBrush"];

    public MainWindow()
    {
        InitializeComponent();
        PageScroll.SizeChanged += (_, _) => SizePageContent();
        PageLayout.SizeChanged += (_, _) => SizePageContent();
        steam = new SteamCatalogService(http);
        keyforsteam = new KeyforsteamService(http);
        InitializeImageViewer();
        tracker = new PlayTracker(repository);
        tracker.Error += exception => DispatcherQueue.TryEnqueue(() => ShowError(exception));
        SystemBackdrop = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()
            ? new NeutralAcrylicBackdrop() : new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.Closing += OnClosing;
        timer.Tick += async (_, _) =>
        {
            if (ready && page == "downloads" && activeDialog is null && !closing)
                await RunAsync(RefreshDownloadsAsync);
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (!Root.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler? onLoaded = null;
                onLoaded = (_, _) =>
                {
                    Root.Loaded -= onLoaded;
                    loaded.TrySetResult();
                };
                Root.Loaded += onLoaded;
                await loaded.Task;
            }
            if (closing) return;
            var scale = Root.XamlRoot.RasterizationScale;
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Resize(new SizeInt32(
                Math.Min((int)Math.Round(1380 * scale), workArea.Width),
                Math.Min((int)Math.Round(900 * scale), workArea.Height)));
            await repository.LoadAsync();
            state = await repository.ReadAsync();
            catalog = string.IsNullOrWhiteSpace(state.Settings.CatalogUrl) ? DemoCatalog.Games() : state.CatalogCache;
            ready = true;
            Navigation.SelectedItem = Navigation.MenuItems[0];
            if (PageHost.Children.Count == 0) await RenderAsync();
            timer.Start();
            await RunAsync(RefreshSteamHomeAsync);
            if (!closing && !string.IsNullOrWhiteSpace(state.Settings.CatalogUrl))
                await RunAsync(RefreshCatalogAsync);
        }
        catch (Exception exception)
        {
            ShowStartupMessage("Deine Daten konnten nicht geladen werden.",
                "Die vorhandene Datei wurde nicht überschrieben. Bitte prüfe state.json im lokalen Datenordner, bevor du den Launcher erneut startest.");
            ShowError(exception);
        }
    }

    public void ShowStartupMessage(string title, string message)
    {
        ready = false;
        Navigation.IsEnabled = false;
        PageHost.Children.Clear();
        PageHost.Children.Add(Text(title, 28));
        PageHost.Children.Add(Text(message, 16, Muted));
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || closing) return;
        page = args.IsSettingsSelected ? "settings" :
            (args.SelectedItemContainer?.Tag as string ?? "store");
        await RunAsync(RenderAsync);
    }

    private async Task RenderAsync()
    {
        CancelOnlineRequest();
        renderVersion++;
        state = await repository.ReadAsync();
        refreshLibrary = null;
        libraryInlineSearch = null;
        PageHost.Children.Clear();
        PageHost.Spacing = page is "store" or "game" or "library" ? 18 : 24;
        var showSidebar = page is "store" or "game" && !ShowingLibrary;
        CatalogSidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(showSidebar ? 240 : 0);
        if (ShowingLibrary)
        {
            LibrarySearch.Text = libraryQuery;
            UpdateLibrarySidebar();
        }
        PageHost.MaxWidth = page is "profile" or "profile-edit" ? 1120 : 1460;
        SizePageContent();
        UpdateCatalogSidebar();
        PageScroll.ChangeView(null, 0, null, true);
        downloadList = null;
        downloadUpdated = null;
        switch (page)
        {
            case "library":
                RenderLibrary();
                await RefreshLibraryArtworkAsync();
                break;
            case "profile": RenderProfile(); break;
            case "profile-edit": RenderProfileEditor(); break;
            case "settings": RenderSettings(); break;
            case "downloads": await RenderDownloadsAsync(); break;
            case "game":
                RenderGamePage();
                if (!gameDetailsLoading && gameDetailsError is null && selectedGame?.SteamAppId is not null)
                    await RefreshKeyPriceAsync(selectedGame);
                break;
            default: RenderStore(); break;
        }
    }

    private void SizePageContent()
    {
        var showLibrarySidebar = ShowingLibrary && PageLayout.ActualWidth >= 680;
        LibrarySidebar.Visibility = showLibrarySidebar ? Visibility.Visible : Visibility.Collapsed;
        if (ShowingLibrary) SidebarColumn.Width = new GridLength(showLibrarySidebar ? 212 : 0);
        if (libraryInlineSearch is not null)
            libraryInlineSearch.Visibility = showLibrarySidebar ? Visibility.Collapsed : Visibility.Visible;
        PageHost.Width = Math.Max(0, PageScroll.ActualWidth - PageScroll.Padding.Left - PageScroll.Padding.Right);
    }

    private void RenderServerStore()
    {
        PageHost.Children.Add(Eyebrow("DEIN NÄCHSTES ABENTEUER"));
        var heading = Row();
        heading.Children.Add(Text("Entdecken", 34));
        heading.Children.Add(Button("Katalog aktualisieren", RefreshCatalogAsync));
        PageHost.Children.Add(heading);
        var demo = string.IsNullOrWhiteSpace(state.Settings.CatalogUrl);
        PageHost.Children.Add(Text(demo
            ? "Demo-Modus: Beispielspiele und Beispiel-Rangfolge. Verbinde deinen eigenen Katalog in den Einstellungen."
            : $"Dein Server-Katalog · {catalog.Count} Spiele · Stand: {FormatDate(state.CatalogUpdatedAt)}", 14, Muted));
        if (catalog.Count == 0)
        {
            PageHost.Children.Add(Empty("Noch keine Spiele", "Aktualisiere deinen Katalog oder prüfe die Katalog-URL in den Einstellungen."));
            return;
        }

        var featured = catalog.OrderByDescending(game => game.Popularity).First();
        PageHost.Children.Add(Featured(featured, demo));
        var search = new TextBox { PlaceholderText = "Spiele, Genres und Tags durchsuchen …", MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
        PageHost.Children.Add(search);
        var results = new StackPanel { Spacing = 18 };
        PageHost.Children.Add(results);

        void Fill()
        {
            results.Children.Clear();
            var query = search.Text.Trim();
            if (query.Length > 0)
            {
                var matching = catalog.Where(game => Matches(game, query))
                    .OrderByDescending(game => game.Popularity).ToList();
                results.Children.Add(Text($"Suchergebnisse · {matching.Count}", 22));
                results.Children.Add(matching.Count == 0
                    ? Empty("Keine Treffer", "Versuche einen anderen Titel, ein Genre oder einen Tag.")
                    : GameGrid(matching));
                return;
            }
            results.Children.Add(SectionHeading("Für dich ausgewählt", "Basierend auf Aufrufen, Favoriten und Spielzeit · nur auf diesem Gerät"));
            var recommendations = Recommendations.ForUser(catalog, state, 4);
            results.Children.Add(recommendations.Count == 0
                ? Text("Du hast bereits alle Katalogspiele in deiner Bibliothek.", 14, Muted)
                : GameGrid(recommendations));
            results.Children.Add(SectionHeading(demo ? "Beliebt im Demo-Katalog" : "Beliebt auf deinem Server",
                "Sortiert nach dem Popularitätswert deiner Katalogquelle"));
            results.Children.Add(GameGrid(catalog.OrderByDescending(game => game.Popularity)));
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
    }

    private Border Featured(Game game, bool demo)
    {
        var content = new Grid { MinHeight = 265, Padding = new Thickness(28) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var copy = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Eyebrow(demo ? "DEMO · IM RAMPENLICHT" : "IM RAMPENLICHT"));
        copy.Children.Add(Text(game.Title, 36));
        copy.Children.Add(Text(game.Description, 15));
        var actions = Row();
        actions.Children.Add(Button("Spiel ansehen  →", () => OpenGameAsync(game), true));
        actions.Children.Add(Text(game.Genre, 13, Muted));
        copy.Children.Add(actions);
        content.Children.Add(copy);
        var art = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12, Margin = new Thickness(24, 0, 0, 0) };
        art.Children.Add(new FontIcon { Glyph = GenreIcon(game.Genre), FontSize = 84, Foreground = Accent, Opacity = 0.9 });
        art.Children.Add(Text("PLAY. EXPLORE. REPEAT.", 10, Accent));
        Grid.SetColumn(art, 1);
        content.Children.Add(art);
        return new Border { CornerRadius = new CornerRadius(14), Background = Gradient(), Child = content };
    }

    private FrameworkElement GameGrid(IEnumerable<Game> games, bool library = false)
    {
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false
        };
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
        foreach (var game in games)
        {
            var entry = state.Library.FirstOrDefault(item => item.Game.Id == game.Id);
            var body = new StackPanel { Width = 218, Spacing = 10, Padding = new Thickness(0, 0, 0, 12) };
            body.Children.Add(Cover(game, 218, 132));
            var title = Text((entry?.Favorite == true ? "★ " : "") + game.Title, 17);
            title.Margin = new Thickness(12, 0, 12, 0);
            body.Children.Add(title);
            var subtitle = Text(library && entry is not null ? Playtime(entry.PlaySeconds) : game.Genre, 12, Muted);
            subtitle.Margin = new Thickness(12, 0, 12, 0);
            body.Children.Add(subtitle);
            if (library && entry is not null)
            {
                var last = Text(tracker.IsRunning(game.Id) ? "● Wird gerade gespielt" : $"Zuletzt: {FormatDate(entry.LastPlayed)}", 11,
                    tracker.IsRunning(game.Id) ? Accent : Muted);
                last.Margin = new Thickness(12, 0, 12, 0);
                body.Children.Add(last);
            }
            var card = Button(body, () => OpenGameAsync(game));
            card.Padding = new Thickness(0);
            card.Margin = new Thickness(0, 0, 12, 12);
            card.CornerRadius = new CornerRadius(10);
            card.Background = Surface;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{game.Title} – Details öffnen");
            grid.Items.Add(card);
        }
        return grid;
    }

    private void RenderGameActions(Game game, StackPanel body)
    {
        var entry = state.Library.FirstOrDefault(item => item.Game.Id == game.Id);
        if (entry is null)
        {
            body.Children.Add(Button("Zur Bibliothek hinzufügen", async () =>
            {
                await repository.UpdateAsync(current =>
                {
                    if (current.Library.All(item => item.Game.Id != game.Id))
                        current.Library.Add(new LibraryEntry { Game = game });
                });
                await CloseDialogAsync();
                await RenderAsync();
                Notify("Zur Bibliothek hinzugefügt", game.Title);
            }, true));
        }
        else
        {
            body.Children.Add(Button("Online-Cover auswählen", () => ChooseArtworkAsync(game)));
            body.Children.Add(Text($"{Playtime(entry.PlaySeconds)} gespielt · {entry.LaunchCount} Starts\nZuletzt gestartet: {FormatDate(entry.LastPlayed)}", 14, Muted));
            body.Children.Add(Text(entry.ExecutablePath ?? "Noch keine lokale Spieldatei verknüpft.", 12, Muted));
            var actions = Row();
            var play = Button(tracker.IsRunning(game.Id) ? "Spiel läuft" : "▶  Spielen", async () =>
            {
                await tracker.LaunchAsync(game.Id);
                await CloseDialogAsync();
                await RenderAsync();
                Notify("Spiel gestartet", "Die Spielzeit wird erfasst, solange der Launcher geöffnet bleibt.");
            }, true);
            play.IsEnabled = !tracker.IsRunning(game.Id) && !string.IsNullOrWhiteSpace(entry.ExecutablePath);
            actions.Children.Add(play);
            var executable = Button("EXE auswählen", async () =>
            {
                var path = await PickAsync(".exe");
                if (path is null) return;
                await repository.UpdateAsync(current =>
                    current.Library.Single(item => item.Game.Id == game.Id).ExecutablePath = path);
                await CloseDialogAsync();
                await RenderAsync();
                Notify("Spieldatei verknüpft", "Öffne die Spieldetails erneut, um das Spiel zu starten.");
            });
            executable.IsEnabled = !tracker.IsRunning(game.Id);
            actions.Children.Add(executable);
            body.Children.Add(actions);
            body.Children.Add(Button(entry.Favorite ? "★  Favorit entfernen" : "☆  Als Favorit markieren", async () =>
            {
                await repository.UpdateAsync(current =>
                {
                    var item = current.Library.Single(item => item.Game.Id == game.Id);
                    item.Favorite = !item.Favorite;
                });
                await CloseDialogAsync();
                await RenderAsync();
            }));
            var remove = Button("Aus Bibliothek entfernen", async () =>
            {
                await CloseDialogAsync();
                if (!await ConfirmAsync("Spiel entfernen?", "Spieldateien bleiben erhalten. Die lokal gespeicherte Spielzeit und Favoritenmarkierung werden entfernt.", "Entfernen"))
                    return;
                if (tracker.IsRunning(game.Id))
                    throw new InvalidOperationException("Laufende Spiele können nicht entfernt werden.");
                await repository.UpdateAsync(current => current.Library.RemoveAll(item => item.Game.Id == game.Id));
                await RenderAsync();
            });
            remove.IsEnabled = !tracker.IsRunning(game.Id);
            body.Children.Add(remove);
        }
        var magnet = entry?.Game.MagnetUri ?? game.MagnetUri;
        if (!string.IsNullOrWhiteSpace(magnet))
        {
            body.Children.Add(Text("Downloads werden an deinen qBittorrent-Server übergeben. Installation und EXE-Auswahl erfolgen anschließend manuell.", 12, Muted));
            body.Children.Add(Button("Mit qBittorrent herunterladen", async () =>
            {
                await CloseDialogAsync();
                if (!await ConfirmAsync("Download an Server senden?",
                    $"„{game.Title}“ wird auf deinem qBittorrent-Server heruntergeladen. Lade nur Inhalte herunter, für deren Nutzung und Verbreitung du die nötigen Rechte besitzt. Es wird nichts automatisch ausgeführt.",
                    "Download starten")) return;
                using var client = CreateQBitClient();
                await client.AddAsync(magnet);
                Notify("Download übergeben", "Den Fortschritt findest du unter Downloads.");
            }));
        }
        else body.Children.Add(Text("Für dieses Spiel stellt der Katalog keinen Torrent-Download bereit.", 12, Muted));
    }

    private async Task AddLocalGameAsync()
    {
        var path = await PickAsync(".exe");
        if (path is null) return;
        var name = new TextBox { Header = "Spielname", Text = Path.GetFileNameWithoutExtension(path), MaxLength = 100 };
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Lokales Spiel hinzufügen", Content = name, PrimaryButtonText = "Hinzufügen",
            CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Primary
        });
        if (result != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(name.Text))
            throw new ArgumentException("Bitte einen Spielnamen angeben.");
        await repository.UpdateAsync(current =>
        {
            if (current.Library.Any(entry => string.Equals(entry.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Diese Spieldatei ist bereits in deiner Bibliothek.");
            current.Library.Add(new LibraryEntry
            {
                Game = new Game { Id = Guid.NewGuid().ToString("N"), Title = name.Text.Trim(), Genre = "Lokales Spiel",
                    Description = "Manuell hinzugefügtes Spiel." },
                ExecutablePath = path
            });
        });
        await RenderAsync();
    }

    private void RenderProfile()
    {
        PageHost.Children.Add(Eyebrow("DEIN SPIELERPROFIL"));
        PageHost.Children.Add(ProfileHeader(ProfileAvatar(), showEditButton: true));
        if (!string.IsNullOrWhiteSpace(state.Profile.Bio))
            PageHost.Children.Add(Panel(Text(state.Profile.Bio, 15)));
        var stats = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 3; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        stats.Children.Add(Stat(state.Library.Count.ToString(CultureInfo.CurrentCulture), "Spiele in deiner Sammlung"));
        stats.Children.Add(Stat(Playtime(state.Library.Sum(entry => entry.PlaySeconds)), "Gesamtspielzeit"));
        stats.Children.Add(Stat(state.Library.Sum(entry => entry.LaunchCount).ToString(CultureInfo.CurrentCulture), "Spielstarts"));
        void ArrangeStats(double width)
        {
            for (var i = 0; i < stats.Children.Count; i++)
            {
                var child = (FrameworkElement)stats.Children[i];
                Grid.SetColumn(child, width < 620 ? 0 : i);
                Grid.SetRow(child, width < 620 ? i : 0);
                Grid.SetColumnSpan(child, width < 620 ? 3 : 1);
            }
        }
        ArrangeStats(0);
        stats.SizeChanged += (_, args) => ArrangeStats(args.NewSize.Width);
        PageHost.Children.Add(stats);

        var activity = new StackPanel { Spacing = 14 };
        activity.Children.Add(Text("Deine Spiele", 24));
        var recent = state.Library.OrderByDescending(entry => entry.LastPlayed)
            .ThenByDescending(entry => entry.PlaySeconds).Take(4).ToList();
        if (recent.Count == 0)
            activity.Children.Add(Text("Deine Sammlung ist noch leer. Füge unter Entdecken oder in der Bibliothek ein Spiel hinzu.", 14, Muted));
        foreach (var entry in recent)
        {
            var card = new Grid { ColumnSpacing = 14 };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.Children.Add(Cover(entry.Game, 112, 63));
            var info = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Text(entry.Game.Title, 17));
            info.Children.Add(Text(Playtime(entry.PlaySeconds) + " gespielt", 12, Muted));
            info.Children.Add(Text("Letzter Start: " + FormatDate(entry.LastPlayed), 12, Muted));
            Grid.SetColumn(info, 1);
            card.Children.Add(info);
            var open = Button(card, () => OpenGameAsync(entry.Game));
            open.HorizontalAlignment = HorizontalAlignment.Stretch;
            open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            activity.Children.Add(open);
        }
        var collection = new StackPanel { Spacing = 12 };
        collection.Children.Add(Text("Sammlung", 22));
        collection.Children.Add(Text($"{state.Library.Count(entry => entry.Favorite)} Favoriten", 15, Accent));
        collection.Children.Add(Text("Lokales Profil", 15));
        collection.Children.Add(Text("Dein Profil und deine Spielzeit werden nur auf diesem Gerät gespeichert. Freunde, Abzeichen und Kommentare sind noch nicht verfügbar.", 13, Muted));
        PageHost.Children.Add(TwoColumns(Panel(activity), Panel(collection)));
    }

    private PersonPicture ProfileAvatar()
    {
        var avatar = new PersonPicture { Width = 92, Height = 92, DisplayName = state.Profile.DisplayName };
        if (state.Profile.AvatarPath is not null)
            avatar.ProfilePicture = new BitmapImage(new Uri(state.Profile.AvatarPath));
        return avatar;
    }

    private Border ProfileHeader(PersonPicture avatar, bool showEditButton)
    {
        var identity = new Grid { ColumnSpacing = 20, RowSpacing = 16 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        identity.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        identity.Children.Add(avatar);
        var intro = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Text(state.Profile.DisplayName, 26));
        intro.Children.Add(Text("Lokales Spielerprofil · Echse", 13, Muted));
        Grid.SetColumn(intro, 1);
        identity.Children.Add(intro);
        if (showEditButton)
        {
            var edit = Button("Profil bearbeiten", async () =>
            {
                page = "profile-edit";
                await RenderAsync();
            });
            Grid.SetColumn(edit, 2);
            identity.Children.Add(edit);
            identity.SizeChanged += (_, args) =>
            {
                var stacked = args.NewSize.Width < 620;
                Grid.SetColumn(edit, stacked ? 0 : 2);
                Grid.SetRow(edit, stacked ? 1 : 0);
                Grid.SetColumnSpan(edit, stacked ? 3 : 1);
            };
        }
        var header = Panel(identity);
        var background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1)
        };
        background.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(210, 77, 11, 32) });
        background.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(180, 35, 3, 14) });
        header.Background = background;
        return header;
    }

    private void RenderProfileEditor()
    {
        PageHost.Children.Add(Eyebrow("DEIN SPIELERPROFIL"));
        PageHost.Children.Add(Text("Profil bearbeiten", 30));
        var avatar = ProfileAvatar();
        PageHost.Children.Add(ProfileHeader(avatar, showEditButton: false));
        var form = new StackPanel { Spacing = 18 };
        var name = new TextBox { Header = "Anzeigename", Text = state.Profile.DisplayName, MaxLength = 48 };
        name.TextChanged += (_, _) => avatar.DisplayName = name.Text;
        var bio = new TextBox { Header = "Über mich", Text = state.Profile.Bio, MaxLength = 500,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        form.Children.Add(name);
        form.Children.Add(bio);
        var avatarPath = state.Profile.AvatarPath;
        var avatarLabel = Text(avatarPath is null ? "Noch kein eigenes Profilbild." : "Eigenes Profilbild gespeichert.", 12, Muted);
        var avatarActions = Row();
        avatarActions.Children.Add(Button("Profilbild auswählen", async () =>
        {
            var path = await PickAsync(".png", ".jpg", ".jpeg");
            if (path is null) return;
            if (new FileInfo(path).Length > 5 * 1024 * 1024)
                throw new InvalidDataException("Das Profilbild darf höchstens 5 MiB groß sein.");
            var image = new BitmapImage();
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using (var stream = await file.OpenReadAsync())
                await image.SetSourceAsync(stream);
            avatarPath = path;
            avatar.ProfilePicture = image;
            avatarLabel.Text = "Neues Bild ausgewählt – bitte Profil speichern.";
        }));
        avatarActions.Children.Add(Button("Bild entfernen", () =>
        {
            avatarPath = null;
            avatar.ProfilePicture = null;
            avatarLabel.Text = "Bild entfernt – bitte Profil speichern.";
            return Task.CompletedTask;
        }));
        form.Children.Add(avatarActions);
        form.Children.Add(avatarLabel);
        form.Children.Add(Button("Profil speichern", async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
                throw new ArgumentException("Dein Anzeigename darf nicht leer sein.");
            string? copiedAvatar = null;
            if (avatarPath is not null && avatarPath != state.Profile.AvatarPath)
            {
                var folder = Path.Combine(LocalServices.DataDirectory, "avatars");
                Directory.CreateDirectory(folder);
                copiedAvatar = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(avatarPath));
                File.Copy(avatarPath, copiedAvatar);
            }
            else copiedAvatar = avatarPath;
            var previous = state.Profile.AvatarPath;
            try
            {
                await repository.UpdateAsync(current =>
                {
                    current.Profile.DisplayName = name.Text.Trim();
                    current.Profile.Bio = bio.Text.Trim();
                    current.Profile.AvatarPath = copiedAvatar;
                });
            }
            catch
            {
                if (copiedAvatar is not null && copiedAvatar != previous) File.Delete(copiedAvatar);
                throw;
            }
            page = "profile";
            await RenderAsync();
            Notify("Profil gespeichert", "Dein Profil bleibt lokal auf diesem Gerät.");
            if (previous is not null && previous != copiedAvatar && File.Exists(previous))
                File.Delete(previous);
        }, true));
        form.Children.Add(Button("Abbrechen", async () =>
        {
            page = "profile";
            await RenderAsync();
        }));
        PageHost.Children.Add(Panel(form));
    }

    private void RenderSettings()
    {
        PageHost.Children.Add(Eyebrow("DEINE VERBINDUNGEN"));
        PageHost.Children.Add(Text("Einstellungen", 34));
        var form = new StackPanel { Spacing = 18, MaxWidth = 740, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Text("Spielekatalog", 22));
        form.Children.Add(Text("Der Katalog liefert Titel, Genres, Popularität und optionale Magnet-Links. qBittorrent übernimmt nur die Downloads und ist keine Spiele-Suchmaschine.", 14, Muted));
        var catalogUrl = new TextBox { Header = "Katalog-URL (HTTP/HTTPS)", PlaceholderText = "https://dein-server.example/games.json",
            Text = state.Settings.CatalogUrl, MinWidth = 540 };
        form.Children.Add(catalogUrl);
        form.Children.Add(Text("Optionaler Download-Katalog, unabhängig von Steam-Metadaten. Leer lassen für Beispiele. Unter Entdecken kannst du zwischen Steam und diesem Katalog wechseln.", 12, Muted));
        form.Children.Add(new Border { Height = 1, Background = SurfaceBorder, Margin = new Thickness(0, 8, 0, 8) });
        form.Children.Add(Text("qBittorrent Web API", 22));
        var server = new TextBox { Header = "Server-Adresse", Text = state.Settings.QBitUrl, PlaceholderText = "http://localhost:8080" };
        var username = new TextBox { Header = "Benutzername", Text = state.Settings.QBitUsername };
        var password = new PasswordBox { Header = "Passwort", Password = LocalServices.Unprotect(state.Settings.ProtectedPassword) };
        form.Children.Add(server);
        form.Children.Add(username);
        form.Children.Add(password);
        form.Children.Add(Text("Aktiviere die Weboberfläche in qBittorrent. Nutze HTTPS für entfernte Server: Über HTTP werden Zugangsdaten unverschlüsselt übertragen. Zertifikatsprüfung bleibt aktiv.", 13, Muted));
        var actions = Row();
        actions.Children.Add(Button("Speichern & Katalog laden", async () =>
        {
            var address = catalogUrl.Text.Trim();
            if (address.Length > 0) NetworkAddress.Parse(address);
            NetworkAddress.Parse(server.Text.Trim());
            using (var validation = new QBitClient(server.Text.Trim(), username.Text, password.Password)) { }
            var settings = new ConnectionSettings
            {
                CatalogUrl = address, QBitUrl = server.Text.Trim(), QBitUsername = username.Text.Trim(),
                ProtectedPassword = LocalServices.Protect(password.Password)
            };
            await repository.UpdateAsync(current =>
            {
                if (!string.Equals(current.Settings.CatalogUrl, address, StringComparison.Ordinal))
                {
                    current.CatalogCache.Clear();
                    current.CatalogUpdatedAt = null;
                }
                current.Settings = settings;
            });
            state = await repository.ReadAsync();
            catalog = address.Length == 0 ? DemoCatalog.Games() : state.CatalogCache;
            await RenderAsync();
            Notify("Einstellungen gespeichert", "Die Zugangsdaten sind für dein Windows-Konto verschlüsselt.");
            await RefreshCatalogAsync();
        }, true));
        actions.Children.Add(Button("qBittorrent testen", async () =>
        {
            using var client = new QBitClient(server.Text.Trim(), username.Text.Trim(), password.Password);
            await client.TestAsync();
            Notify("Verbindung erfolgreich", "qBittorrent ist erreichbar und die Anmeldung funktioniert.");
        }));
        form.Children.Add(actions);
        PageHost.Children.Add(Panel(form));
        PageHost.Children.Add(SectionHeading("Lokal und transparent",
            "Profil, Bibliothek, Spielzeit und Empfehlungen bleiben auf diesem PC. Metadaten, Suchanfragen und Bilder werden von Steam bzw. deiner Katalogquelle geladen. Es gibt keine Telemetrie."));
        PageHost.Children.Add(Text("Datenordner: " + LocalServices.DataDirectory, 12, Muted));
        PageHost.Children.Add(Text("Acrylic wird nativ von Windows bereitgestellt und zeichnet nur den Fensterhintergrund weich, nicht Text oder Bedienelemente. Sichtbarkeit hängt von Windows-Version, Transparenzeinstellungen, Energiesparmodus und Fensterfokus ab; ohne Unterstützung verwendet Windows einen soliden Hintergrund.", 13, Muted));
    }

    private async Task RefreshCatalogAsync()
    {
        state = await repository.ReadAsync();
        if (string.IsNullOrWhiteSpace(state.Settings.CatalogUrl))
        {
            catalog = DemoCatalog.Games();
            await RenderAsync();
            Notify("Demo-Katalog geladen", "Verbinde einen eigenen Spielekatalog in den Einstellungen.");
            return;
        }
        var address = state.Settings.CatalogUrl;
        var fetched = await new CatalogService(http).FetchAsync(address);
        await repository.UpdateAsync(current =>
        {
            if (current.Settings.CatalogUrl != address)
                throw new InvalidOperationException("Die Katalogquelle wurde während des Ladens geändert. Bitte erneut aktualisieren.");
            current.CatalogCache = fetched;
            current.CatalogUpdatedAt = DateTimeOffset.UtcNow;
        });
        catalog = fetched;
        if (!closing && page == "store" && useServerCatalog) await RenderAsync();
        Notify("Katalog aktualisiert", $"{catalog.Count} Spiele von deiner Quelle geladen.");
    }

    private async Task RenderDownloadsAsync()
    {
        PageHost.Children.Add(Eyebrow("VON DEINEM SERVER"));
        var heading = Row();
        heading.Children.Add(Text("Downloads", 34));
        heading.Children.Add(Button("Aktualisieren", RefreshDownloadsAsync));
        PageHost.Children.Add(heading);
        PageHost.Children.Add(Text("Zeigt mit „echse“ markierte Torrents. Dateien liegen auf deinem qBittorrent-Server, nicht automatisch auf diesem PC. Installation erfolgt manuell.", 14, Muted));
        downloadUpdated = Text("Wird geladen …", 12, Muted);
        PageHost.Children.Add(downloadUpdated);
        downloadList = new StackPanel { Spacing = 14 };
        PageHost.Children.Add(downloadList);
        await RefreshDownloadsAsync();
    }

    private async Task RefreshDownloadsAsync()
    {
        if (downloadsLoading || downloadList is null) return;
        downloadsLoading = true;
        var target = downloadList;
        var updated = downloadUpdated;
        try
        {
            state = await repository.ReadAsync();
            using var client = CreateQBitClient();
            var downloads = await client.GetDownloadsAsync();
            if (target != downloadList || closing) return;
            target.Children.Clear();
            if (downloads.Count == 0)
                target.Children.Add(Empty("Keine Echse-Downloads", "Starte einen Download über die Spieldetails. Andere Torrents blendet Echse aus."));
            foreach (var torrent in downloads)
            {
                var item = new StackPanel { Spacing = 10 };
                item.Children.Add(Text(torrent.Name, 18));
                item.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(torrent.Progress * 100, 0, 100) });
                item.Children.Add(Text($"{torrent.Progress:P1}  ·  {torrent.DownloadSpeed / 1024d / 1024d:F1} MiB/s  ·  {torrent.State}", 12, Muted));
                target.Children.Add(Panel(item));
            }
            if (updated is not null)
                updated.Text = $"Aktualisiert {DateTime.Now:T} · automatisch alle 5 Sekunden";
        }
        catch
        {
            if (updated is not null)
                updated.Text = "Aktualisierung fehlgeschlagen · angezeigte Daten sind möglicherweise veraltet.";
            throw;
        }
        finally { downloadsLoading = false; }
    }

    private QBitClient CreateQBitClient() => new(state.Settings.QBitUrl, state.Settings.QBitUsername,
        LocalServices.Unprotect(state.Settings.ProtectedPassword));

    private async Task<string?> PickAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (activeDialog is not null)
            throw new InvalidOperationException("Bitte zuerst den geöffneten Dialog schließen.");
        activeDialog = dialog;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogClosed = closed;
        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = ElementTheme.Dark;
        dialog.Background = (Brush)Application.Current.Resources["LauncherDialogBrush"];
        dialog.BorderBrush = SurfaceBorder;
        dialog.Resources.ThemeDictionaries["Dark"] = new ResourceDictionary
        {
            ["ContentDialogTopOverlay"] = Surface,
            ["AccentButtonBackground"] = Application.Current.Resources["LauncherActionBrush"],
            ["AccentButtonBackgroundPointerOver"] = Application.Current.Resources["LauncherActionHoverBrush"],
            ["AccentButtonBackgroundPressed"] = Application.Current.Resources["LauncherActionPressedBrush"],
            ["AccentButtonForeground"] = Brush(255, 255, 255),
            ["AccentButtonForegroundPointerOver"] = Brush(255, 255, 255),
            ["AccentButtonForegroundPressed"] = Brush(255, 255, 255)
        };
        try { return await dialog.ShowAsync(); }
        finally
        {
            if (ReferenceEquals(activeDialog, dialog)) activeDialog = null;
            if (ReferenceEquals(dialogClosed, closed)) dialogClosed = null;
            closed.TrySetResult();
        }
    }

    private async Task CloseDialogAsync()
    {
        var pending = dialogClosed?.Task;
        activeDialog?.Hide();
        if (pending is not null) await pending;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        await CloseDialogAsync();
        return await ShowDialogAsync(new ContentDialog
        {
            Title = title, Content = Text(message, 15), PrimaryButtonText = action,
            CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Close
        }) == ContentDialogResult.Primary;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        LocalServices.Log(exception);
        Notice.Title = "Aktion fehlgeschlagen";
        Notice.Message = exception.Message;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.IsOpen = true;
    }

    private void Notify(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Success;
        Notice.IsOpen = true;
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        if (activeDialog is not null)
        {
            Notify("Dialog noch geöffnet", "Bitte schließe zuerst den geöffneten Dialog.");
            return;
        }
        closing = true;
        CancelOnlineRequest();
        timer.Stop();
        Root.IsHitTestVisible = false;
        await RunAsync(tracker.StopAsync);
        http.Dispose();
        Close();
    }

    private Button Button(object content, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = content, Padding = new Thickness(16, 10, 16, 10),
            VerticalAlignment = VerticalAlignment.Center };
        if (primary)
        {
            button.Background = (SolidColorBrush)Application.Current.Resources["LauncherActionBrush"];
            button.Foreground = Brush(255, 255, 255);
        }
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await RunAsync(action); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 18 };

    private static TextBlock Text(string text, double size = 14, Brush? foreground = null) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = foreground ?? Brush(239, 243, 249),
        FontWeight = size >= 20 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock Eyebrow(string text)
    {
        var label = Text(text, 11, Accent);
        label.CharacterSpacing = 160;
        return label;
    }

    private static StackPanel SectionHeading(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text(title, 23));
        panel.Children.Add(Text(subtitle, 12, Muted));
        return panel;
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = Surface,
        BorderBrush = SurfaceBorder,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(24), Child = child
    };

    private static Border Stat(string value, string label)
    {
        var stack = new StackPanel { Spacing = 8, MinWidth = 145 };
        stack.Children.Add(Text(value, 28, Accent));
        stack.Children.Add(Text(label, 12, Muted));
        return Panel(stack);
    }

    private static Border Empty(string title, string description)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(8, 20, 8, 20) };
        stack.Children.Add(Text(title, 24));
        stack.Children.Add(Text(description, 14, Muted));
        return Panel(stack);
    }

    private void ImageFailed(object sender, ExceptionRoutedEventArgs args)
    {
        if (sender is Image image) image.Visibility = Visibility.Collapsed;
        Notice.Title = "Cover nicht verfügbar";
        Notice.Message = "Mindestens ein Cover konnte nicht geladen werden. Die Genre-Grafik wird stattdessen angezeigt.";
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    private Border Cover(Game game, double width, double height, string? imageUrl = null, bool fallbackToCover = false)
    {
        var grid = new Grid { Width = width, Height = height, Background = Gradient() };
        grid.Children.Add(new FontIcon { Glyph = GenreIcon(game.Genre), FontSize = 50, Foreground = Accent, Opacity = 0.85 });
        var address = imageUrl ?? game.CoverUrl;
        if (!string.IsNullOrWhiteSpace(address))
        {
            var image = new Image { Source = new BitmapImage(NetworkAddress.Parse(address))
                { DecodePixelWidth = (int)Math.Ceiling(width * Root.XamlRoot.RasterizationScale) },
                Stretch = Stretch.UniformToFill };
            var fallbackUsed = false;
            image.ImageFailed += (_, args) =>
            {
                if (fallbackToCover && !fallbackUsed && !string.IsNullOrWhiteSpace(game.CoverUrl) &&
                    !string.Equals(address, game.CoverUrl, StringComparison.Ordinal))
                {
                    fallbackUsed = true;
                    image.Source = new BitmapImage(NetworkAddress.Parse(game.CoverUrl))
                    {
                        DecodePixelWidth = (int)Math.Ceiling(width * Root.XamlRoot.RasterizationScale)
                    };
                    ToolTipService.SetToolTip(image, "Kein Hochkant-Cover verfügbar. Das Steam-Katalogbild wird verwendet.");
                    return;
                }
                ImageFailed(image, args);
            };
            grid.Children.Add(image);
        }
        return new Border { CornerRadius = new CornerRadius(9), Child = grid };
    }

    private static LinearGradientBrush Gradient()
    {
        var color = Color.FromArgb(255, 77, 11, 32);
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 35, 3, 14), Offset = 1 });
        return brush;
    }

    private static string GenreIcon(string genre) => genre switch
    {
        "Racing" => "\uE7F4", "Strategie" => "\uE80F", "Rollenspiel" => "\uE8D7",
        "Simulation" => "\uE7C1", "Action" => "\uE945", _ => "\uE7FC"
    };

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    private static bool Matches(Game game, string query) =>
        game.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        game.Genre.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        game.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    private static string FormatDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Noch nie";
    private static string Playtime(double seconds) => seconds < 3600
        ? $"{Math.Floor(seconds / 60):N0} Min."
        : $"{seconds / 3600:N1} Std.";
}
