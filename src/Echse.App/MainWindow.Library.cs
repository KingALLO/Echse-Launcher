using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Echse.App;

public sealed partial class MainWindow
{
    private string libraryQuery = "";
    private int librarySort = 2;
    private Action? refreshLibrary;
    private TextBox? libraryInlineSearch;
    private bool ShowingLibrary => page == "library" || page == "game" && gameOriginPage == "library";

    private void UpdateLibrarySidebar()
    {
        var entries = state.Library.Where(entry => Matches(entry.Game, libraryQuery.Trim()))
            .OrderBy(entry => entry.Game.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        LibraryGameList.ItemsSource = entries;
        LibraryGameList.SelectedItem = page == "game"
            ? entries.FirstOrDefault(entry => entry.Game.Id == selectedGame?.Id) : null;
    }

    private void SetLibraryQuery(string query)
    {
        if (libraryQuery == query) return;
        libraryQuery = query;
        if (LibrarySearch.Text != query) LibrarySearch.Text = query;
        if (libraryInlineSearch is not null && libraryInlineSearch.Text != query)
            libraryInlineSearch.Text = query;
        UpdateLibrarySidebar();
        refreshLibrary?.Invoke();
    }

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs args) =>
        SetLibraryQuery(LibrarySearch.Text);

    private async void LibraryHome_Click(object sender, RoutedEventArgs args)
    {
        page = "library";
        await RunAsync(RenderAsync);
    }

    private async void LibraryGameList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is LibraryEntry entry) await RunAsync(() => OpenGameAsync(entry.Game));
    }

    private void LibraryGameList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is LibraryEntry entry)
            AutomationProperties.SetName(args.ItemContainer, entry.Game.Title);
    }

    private void RenderLibrary()
    {
        libraryInlineSearch = new TextBox { PlaceholderText = "Bibliothek durchsuchen …", Text = libraryQuery };
        AutomationProperties.SetName(libraryInlineSearch, "Bibliothek durchsuchen");
        libraryInlineSearch.TextChanged += (sender, _) => SetLibraryQuery(((TextBox)sender).Text);
        PageHost.Children.Add(libraryInlineSearch);
        var shelves = new StackPanel { Spacing = 16 };
        PageHost.Children.Add(shelves);

        var header = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var count = Text("", 18);
        var sort = new ComboBox
        {
            Width = 180, ItemsSource = new[] { "Zuletzt gespielt", "Titel A–Z", "Spielzeit", "Favoriten zuerst" },
            SelectedIndex = librarySort
        };
        AutomationProperties.SetName(sort, "Spiele sortieren");
        ToolTipService.SetToolTip(sort, "Sortieren nach");
        var add = Button("+ Lokales Spiel", AddLocalGameAsync);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var covers = Button("Cover laden", async () =>
        {
            if (!state.Library.Any(entry => NeedsArtwork(entry) && !artworkAttempted.Contains(entry.Game.Id)))
                artworkAttempted.Clear();
            await RefreshLibraryArtworkAsync();
        });
        tools.Children.Add(covers);
        tools.Children.Add(add);
        ToolTipService.SetToolTip(add, "Lokales Spiel hinzufügen");
        AutomationProperties.SetName(add, "Lokales Spiel hinzufügen");
        header.Children.Add(count);
        header.Children.Add(sort);
        header.Children.Add(tools);
        header.SizeChanged += (_, _) =>
        {
            var narrow = header.ActualWidth < 680;
            header.RowSpacing = narrow ? 10 : 0;
            Grid.SetRow(sort, narrow ? 1 : 0);
            Grid.SetColumn(sort, narrow ? 0 : 1);
            Grid.SetRow(tools, narrow ? 2 : 0);
            Grid.SetColumn(tools, narrow ? 0 : 2);
            sort.HorizontalAlignment = HorizontalAlignment.Left;
            tools.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        PageHost.Children.Add(header);
        var results = new StackPanel();
        PageHost.Children.Add(results);
        refreshLibrary = () =>
        {
            shelves.Children.Clear();
            results.Children.Clear();
            var matching = state.Library.Where(entry => Matches(entry.Game, libraryQuery.Trim())).ToList();
            covers.IsEnabled = state.Library.Any(NeedsArtwork);
            shelves.Visibility = matching.Count > 0 && string.IsNullOrWhiteSpace(libraryQuery)
                ? Visibility.Visible : Visibility.Collapsed;
            count.Text = $"Alle Spiele ({matching.Count})";
            if (matching.Count == 0)
            {
                results.Children.Add(state.Library.Count == 0
                    ? Empty("Deine Bibliothek wartet auf dich", "Füge unter Entdecken ein Spiel hinzu oder verknüpfe hier eine lokale EXE-Datei.")
                    : Empty("Keine passenden Spiele", "Versuche einen anderen Suchbegriff."));
                return;
            }
            if (string.IsNullOrWhiteSpace(libraryQuery))
            {
                var recent = matching.Where(entry => entry.LastPlayed is not null)
                    .OrderByDescending(entry => entry.LastPlayed).Take(12).ToList();
                if (recent.Count > 0) shelves.Children.Add(LibraryShelf("Zuletzt gespielt", recent));
                else shelves.Children.Add(Text("Noch kein Spiel gestartet – deine zuletzt gespielten Titel erscheinen hier.", 12, Muted));
                var next = matching.Where(entry => !tracker.IsRunning(entry.Game.Id))
                    .OrderByDescending(entry => entry.Favorite).ThenBy(entry => entry.LaunchCount > 0)
                    .ThenBy(entry => entry.LastPlayed).ThenBy(entry => entry.PlaySeconds)
                    .ThenBy(entry => entry.Game.Title, StringComparer.CurrentCultureIgnoreCase).Take(12).ToList();
                if (next.Count > 0) shelves.Children.Add(LibraryShelf("Als Nächstes spielen", next));
            }
            var sorted = librarySort switch
            {
                1 => matching.OrderBy(entry => entry.Game.Title, StringComparer.CurrentCultureIgnoreCase),
                2 => matching.OrderByDescending(entry => entry.PlaySeconds).ThenBy(entry => entry.Game.Title),
                3 => matching.OrderByDescending(entry => entry.Favorite).ThenBy(entry => entry.Game.Title),
                _ => matching.OrderByDescending(entry => entry.LastPlayed).ThenBy(entry => entry.Game.Title)
            };
            var grid = new GridView
            {
                SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
            foreach (var entry in sorted)
            {
                var card = LibraryPoster(entry, showTime: true);
                card.Margin = new Thickness(0, 0, 8, 10);
                grid.Items.Add(card);
            }
            results.Children.Add(grid);
        };
        sort.SelectionChanged += (_, _) =>
        {
            librarySort = sort.SelectedIndex;
            refreshLibrary?.Invoke();
        };
        refreshLibrary();
        SizePageContent();
    }

    private FrameworkElement LibraryShelf(string title, IReadOnlyList<LibraryEntry> entries)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Text(title, 18));
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cards.Children.Add(LibraryFeatured(entries[0]));
        foreach (var entry in entries.Skip(1)) cards.Children.Add(LibraryPoster(entry));
        section.Children.Add(new ScrollViewer
        {
            Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 0, 4)
        });
        return section;
    }

    private Button LibraryPoster(LibraryEntry entry, bool showTime = false)
    {
        var game = entry.Game;
        var portrait = game.PortraitUrl ?? (game.SteamAppId is int id ? SteamCatalogService.PortraitUrl(id) : null);
        var art = Cover(game, 112, 168, portrait, fallbackToCover: true);
        art.CornerRadius = new CornerRadius(2);
        var body = new Grid { Width = 112, Height = 168 };
        body.Children.Add(art);
        if (portrait is null && string.IsNullOrWhiteSpace(game.CoverUrl))
        {
            var name = Text(game.Title, 12);
            name.MaxLines = 3;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(8, 0, 8, 30);
            name.VerticalAlignment = VerticalAlignment.Bottom;
            body.Children.Add(name);
        }
        if (showTime)
        {
            var time = Text(tracker.IsRunning(game.Id) ? "Wird gespielt" : Playtime(entry.PlaySeconds), 10);
            time.HorizontalAlignment = HorizontalAlignment.Center;
            var overlay = new Border
            {
                Child = time, Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Bottom
            };
            overlay.Background = (Brush)Application.Current.Resources["LauncherLibraryOverlayBrush"];
            body.Children.Add(overlay);
        }
        var button = LibraryCardButton(body, game);
        ToolTipService.SetToolTip(button, $"{game.Title}\n{Playtime(entry.PlaySeconds)} gespielt\nZuletzt: {FormatDate(entry.LastPlayed)}");
        return button;
    }

    private FrameworkElement LibraryFeatured(LibraryEntry entry)
    {
        var game = entry.Game;
        var body = new StackPanel { Width = 234 };
        var cover = Cover(game, 234, 110, game.BannerUrl ?? game.CoverUrl);
        cover.CornerRadius = new CornerRadius(2, 2, 0, 0);
        body.Children.Add(LibraryCardButton(cover, game));
        var footer = new Grid { Height = 58, Padding = new Thickness(6), ColumnSpacing = 8 };
        footer.Background = (Brush)Application.Current.Resources["LauncherLibraryOverlayBrush"];
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var running = tracker.IsRunning(game.Id);
        var play = Button(new FontIcon { Glyph = "\uE768", FontSize = 18 }, async () =>
        {
            await tracker.LaunchAsync(game.Id);
            await RenderAsync();
            Notify("Spiel gestartet", "Die Spielzeit wird erfasst, solange der Launcher geöffnet bleibt.");
        });
        play.Width = 40;
        play.Height = 40;
        play.Padding = new Thickness(0);
        play.CornerRadius = new CornerRadius(2);
        play.Background = (Brush)Application.Current.Resources["LauncherActionBrush"];
        play.Foreground = Brush(255, 255, 255);
        play.IsEnabled = !running && !string.IsNullOrWhiteSpace(entry.ExecutablePath);
        AutomationProperties.SetName(play, game.Title + " starten");
        ToolTipService.SetToolTip(play, running ? "Spiel läuft bereits" :
            play.IsEnabled ? "Spiel starten" : "Zum Starten in den Spieldetails eine EXE auswählen");
        footer.Children.Add(play);
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = Text(game.Title, 12);
        name.MaxLines = 1;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(name);
        info.Children.Add(Text(Playtime(entry.PlaySeconds) + " gespielt", 10));
        var last = Text(running ? "Wird gerade gespielt" : "Zuletzt: " + FormatDate(entry.LastPlayed), 9, Muted);
        last.MaxLines = 1;
        last.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(last);
        Grid.SetColumn(info, 1);
        footer.Children.Add(info);
        body.Children.Add(footer);
        return body;
    }

    private Button LibraryCardButton(UIElement content, Game game)
    {
        var button = Button(content, () => OpenGameAsync(game));
        button.Padding = new Thickness(0);
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        button.CornerRadius = new CornerRadius(2);
        button.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, game.Title + " – Spieldetails öffnen");
        return button;
    }
}
