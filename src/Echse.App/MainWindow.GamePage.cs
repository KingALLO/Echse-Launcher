using System.Globalization;
using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Echse.App;

public sealed partial class MainWindow
{
    private int selectedGalleryIndex;
    private StackPanel? keyPriceHost;
    private string[] viewerImages = [];
    private string viewerTitle = "";
    private int viewerIndex;
    private Control? galleryReturnFocus;

    private void RenderGamePage()
    {
        var game = selectedGame ?? throw new InvalidOperationException("Es wurde kein Spiel ausgewählt.");
        keyPriceHost = null;
        var navigation = Row();
        navigation.Children.Add(Button("←  Zurück", async () =>
        {
            gameDetailsLoading = false;
            page = gameOriginPage;
            if (page == "library-game")
            {
                var current = await repository.ReadAsync();
                selectedGame = current.Library.FirstOrDefault(entry => entry.Game.Id == libraryReturnGameId)?.Game;
                if (selectedGame is null) page = "library";
            }
            SelectNavigation(page is "library-game" or "library" ? "library" : "store");
            await RenderAsync();
        }));
        navigation.Children.Add(Eyebrow(game.SteamAppId is null ? "DEIN SPIEL" : $"STEAM  /  APP {game.SteamAppId}"));
        PageHost.Children.Add(navigation);
        PageHost.Children.Add(Text(game.Title, 34));
        if (gameDetailsLoading)
            PageHost.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
        if (gameDetailsError is not null)
        {
            PageHost.Children.Add(Text(gameDetailsError, 14, Muted));
            PageHost.Children.Add(Button("Details erneut laden", () => OpenGameAsync(game)));
        }

        var facts = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(game.CoverUrl))
        {
            var capsule = new Image
            {
                Source = new BitmapImage(NetworkAddress.Parse(game.CoverUrl)),
                Height = 150, Stretch = Stretch.Uniform
            };
            capsule.ImageFailed += ImageFailed;
            facts.Children.Add(capsule);
        }
        if (game.SteamAppId is not null)
        {
            facts.Children.Add(Eyebrow("STEAM-PREIS · DE"));
            facts.Children.Add(Text(string.IsNullOrWhiteSpace(game.PriceLabel)
                ? gameDetailsLoading ? "Wird geladen …" : "Kein Einzelpreis verfügbar"
                : game.PriceLabel, 26, Accent));
        }
        facts.Children.Add(Text(string.IsNullOrWhiteSpace(game.Description)
            ? gameDetailsLoading ? "Informationen werden von Steam geladen …" : "Noch keine Beschreibung verfügbar."
            : game.Description, 14));
        AddFact(facts, "Entwickler", string.Join(", ", game.Developers));
        AddFact(facts, "Publisher", string.Join(", ", game.Publishers));
        AddFact(facts, "Veröffentlichung", game.ReleaseDate);
        AddFact(facts, "Plattformen", string.Join(" · ", game.Platforms));
        AddFact(facts, "Inhalt", game.ContentType switch
        {
            "game" => "Spiel", "dlc" => "DLC / Erweiterung", "demo" => "Demo", _ => ""
        });
        if (game.Tags.Length > 0) facts.Children.Add(Text(string.Join("  ·  ", game.Tags), 12, Accent));
        PageHost.Children.Add(TwoColumns(RenderGallery(game), facts));

        if (game.SteamAppId is int id)
        {
            var purchase = new StackPanel { Spacing = 10 };
            purchase.Children.Add(Eyebrow("STEAM · DEUTSCHLAND"));
            purchase.Children.Add(Text(game.Title + " kaufen", 22));
            if (game.DiscountPercent > 0 && !string.IsNullOrWhiteSpace(game.PriceLabel))
            {
                var discount = Row();
                discount.Children.Add(Text($"−{game.DiscountPercent} %", 20, Accent));
                if (!string.IsNullOrWhiteSpace(game.OriginalPriceLabel))
                {
                    var original = Text(game.OriginalPriceLabel, 16, Muted);
                    original.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    discount.Children.Add(original);
                }
                purchase.Children.Add(discount);
            }
            purchase.Children.Add(Text(string.IsNullOrWhiteSpace(game.PriceLabel)
                ? gameDetailsLoading ? "Preis wird geladen …" : "Kein Einzelpreis verfügbar"
                : game.PriceLabel, 28, Accent));
            if (game.PriceCheckedAt is not null)
                purchase.Children.Add(Text("Preisstand: " + FormatDate(game.PriceCheckedAt), 11, Muted));
            purchase.Children.Add(StoreLink("Zum Steam-Angebot ↗", $"https://store.steampowered.com/app/{id}/"));
            keyPriceHost = new StackPanel { Spacing = 10 };
            keyPriceHost.Children.Add(Eyebrow("KEYFORSTEAM · PREISVERGLEICH"));
            keyPriceHost.Children.Add(Text(gameDetailsError is not null
                ? "Bitte zuerst die Spieldetails erneut laden."
                : "Öffentlicher Ab-Preis wird abgerufen …", 14, Muted));
            PageHost.Children.Add(TwoColumns(Panel(purchase), Panel(keyPriceHost)));
        }
        sourceGameHost = new StackPanel();
        RenderSourceGameContent(game);
        PageHost.Children.Add(Panel(sourceGameHost));
        if (!gameDetailsLoading)
        {
            var actions = new StackPanel { Spacing = 12 };
            actions.Children.Add(Eyebrow("DEINE BIBLIOTHEK"));
            RenderGameActions(game, actions);
            PageHost.Children.Add(Panel(actions));
        }
        PageHost.Children.Add(SectionHeading("Über dieses Spiel",
            string.IsNullOrWhiteSpace(game.Description) ? "Noch keine Beschreibung verfügbar." : game.Description));
        if (game.SteamAppId is not null) PageHost.Children.Add(RenderRequirements(game));
        PageHost.Children.Add(Text("Metadaten und Bilder: Steam / jeweilige Rechteinhaber. Preise können sich ändern; Verfügbarkeit und Bedingungen beim Anbieter prüfen. Ein Katalogeintrag ist keine Besitz- oder Downloadberechtigung.", 11, Muted));
    }

    private static FrameworkElement RenderRequirements(Game game)
    {
        var section = new StackPanel { Spacing = 14 };
        section.Children.Add(Text("Systemanforderungen", 24));
        if (game.Requirements.Length == 0)
        {
            section.Children.Add(Text("Steam stellt für dieses Spiel derzeit keine technischen Anforderungen bereit.", 14, Muted));
            return section;
        }
        var platforms = new ComboBox
        {
            ItemsSource = game.Requirements.Select(item => item.Platform).ToArray(),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160
        };
        var content = new StackPanel();
        void ShowRequirements()
        {
            var item = game.Requirements[platforms.SelectedIndex];
            var minimum = new StackPanel { Spacing = 12 };
            minimum.Children.Add(Eyebrow("MINDESTANFORDERUNGEN"));
            minimum.Children.Add(Text(item.Minimum.Length > 0 ? item.Minimum : "Keine Angaben vom Anbieter.", 14, Muted));
            var recommended = new StackPanel { Spacing = 12 };
            recommended.Children.Add(Eyebrow("EMPFOHLEN"));
            recommended.Children.Add(Text(item.Recommended.Length > 0 ? item.Recommended : "Keine Angaben vom Anbieter.", 14, Muted));
            content.Children.Clear();
            content.Children.Add(TwoColumns(Panel(minimum), Panel(recommended)));
        }
        platforms.SelectionChanged += (_, _) => ShowRequirements();
        section.Children.Add(platforms);
        section.Children.Add(content);
        ShowRequirements();
        return section;
    }

    private static Grid TwoColumns(FrameworkElement main, FrameworkElement aside)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(aside, 1);
        grid.Children.Add(main);
        grid.Children.Add(aside);
        void Arrange(double width)
        {
            var stacked = width < 820;
            grid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 320);
            Grid.SetColumn(aside, stacked ? 0 : 1);
            Grid.SetRow(aside, stacked ? 1 : 0);
            aside.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
        }
        Arrange(0);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private FrameworkElement RenderGallery(Game game)
    {
        var images = game.Screenshots.Length > 0 ? game.Screenshots :
            new[] { game.BannerUrl ?? game.CoverUrl }.OfType<string>().ToArray();
        if (images.Length == 0) return Empty("Noch keine Bilder", "Für dieses Spiel sind keine Bilder verfügbar.");
        selectedGalleryIndex = Math.Clamp(selectedGalleryIndex, 0, images.Length - 1);
        var gallery = new StackPanel { Spacing = 10 };
        var preview = new Image { Stretch = Stretch.Uniform };
        preview.ImageFailed += ImageFailed;
        var frame = ThemeCorners(new Grid { Height = 340, Background = Surface });
        frame.Children.Add(preview);
        frame.SizeChanged += (_, args) => frame.Height = Math.Clamp(args.NewSize.Width * 9 / 16, 160, 520);
        var open = Button(frame, () =>
        {
            OpenImageViewer(game.Title, images, selectedGalleryIndex);
            return Task.CompletedTask;
        });
        open.HorizontalAlignment = HorizontalAlignment.Stretch;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.Padding = new Thickness(0);
        open.BorderThickness = new Thickness(0);
        AutomationProperties.SetName(open, "Bild fensterfüllend öffnen");
        ToolTipService.SetToolTip(open, "Bild vergrößern");
        gallery.Children.Add(open);
        var position = Text("", 12, Muted);
        gallery.Children.Add(position);
        var thumbnails = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var buttons = new List<Button>();
        void SelectImage(int index)
        {
            selectedGalleryIndex = index;
            preview.Visibility = Visibility.Visible;
            preview.Source = new BitmapImage(NetworkAddress.Parse(images[index]));
            position.Text = $"Bild {index + 1} von {images.Length} · Klicken zum Vergrößern";
            for (var i = 0; i < buttons.Count; i++)
                buttons[i].BorderBrush = i == index ? Accent : SurfaceBorder;
        }
        for (var i = 0; i < images.Length; i++)
        {
            var index = i;
            var thumbnailUrl = game.ScreenshotThumbnails.Length == images.Length
                ? game.ScreenshotThumbnails[i] : images[i];
            var thumbnail = Button(Cover(game, 104, 59, thumbnailUrl), () =>
            {
                SelectImage(index);
                return Task.CompletedTask;
            });
            thumbnail.Padding = new Thickness(2);
            thumbnail.BorderThickness = new Thickness(2);
            AutomationProperties.SetName(thumbnail, $"Bild {i + 1} auswählen");
            buttons.Add(thumbnail);
            thumbnails.Children.Add(thumbnail);
        }
        SelectImage(selectedGalleryIndex);
        gallery.Children.Add(new ScrollViewer
        {
            Content = thumbnails, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 8)
        });
        return gallery;
    }

    private async Task RefreshKeyPriceAsync(Game game)
    {
        var host = keyPriceHost ?? throw new InvalidOperationException("Die Preisansicht ist nicht geöffnet.");
        var token = BeginOnlineRequest();
        var version = renderVersion;
        host.Children.Clear();
        host.Children.Add(Eyebrow("KEYFORSTEAM · PREISVERGLEICH"));
        host.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
        host.Children.Add(Text("Öffentlicher Ab-Preis wird abgerufen …", 13, Muted));
        try
        {
            var result = await keyforsteam.GetPriceAsync(game.Title, token);
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            host.Children.Clear();
            host.Children.Add(Eyebrow("KEYFORSTEAM · PREISVERGLEICH"));
            if (result.FromPrice is decimal price)
            {
                var formatted = result.Currency == "EUR"
                    ? price.ToString("C2", CultureInfo.GetCultureInfo("de-DE"))
                    : price.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " " + result.Currency;
                host.Children.Add(Text("ab " + formatted, 28));
                host.Children.Add(Text("UNGEPRÜFTER AB-PREIS", 11, Accent));
                host.Children.Add(Text("Öffentlicher Gesamtpreis ab: Region, Edition und Angebotsart sind ungeprüft. Kann Accounts, DLCs oder andere Plattformen enthalten – kein bestätigter Steam-Key. Gutscheine und Zahlungsgebühren beim Anbieter prüfen.", 12, Muted));
            }
            else host.Children.Add(Text(result.UnavailableReason ?? "Kein zuordenbarer Preis verfügbar.", 13, Muted));
            host.Children.Add(Text("Abgerufen: " + FormatDate(result.CheckedAt), 11, Muted));
            host.Children.Add(StoreLink("Auf Keyforsteam prüfen ↗", result.Source.AbsoluteUri));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            LocalServices.Log(exception);
            host.Children.Clear();
            host.Children.Add(Eyebrow("KEYFORSTEAM · PREISVERGLEICH"));
            var message = exception switch
            {
                TimeoutException => exception.Message,
                HttpRequestException { StatusCode: { } status } => $"Keyforsteam meldet HTTP {(int)status}. Bitte später erneut versuchen.",
                HttpRequestException => "Keine Verbindung zu Keyforsteam möglich. Bitte die Website im Browser prüfen.",
                _ => "Das Preisformat von Keyforsteam konnte nicht gelesen werden. Bitte den Preis auf der Website prüfen."
            };
            host.Children.Add(Text(message, 13, Muted));
            host.Children.Add(Button("Erneut versuchen", () => RefreshKeyPriceAsync(game)));
            host.Children.Add(StoreLink("Keyforsteam-Suche öffnen ↗", KeyforsteamService.SearchUrl(game.Title).AbsoluteUri));
        }
    }

    private void InitializeImageViewer()
    {
        ImageZoom.SizeChanged += (_, args) =>
        {
            ImageStage.Width = Math.Max(1, args.NewSize.Width - 24);
            ImageStage.Height = Math.Max(1, args.NewSize.Height - 24);
        };
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (ImageLightbox.Visibility != Visibility.Visible) return;
            switch (args.Key)
            {
                case VirtualKey.Escape: CloseImageViewer(); break;
                case VirtualKey.Left: ChangeViewerImage(-1); break;
                case VirtualKey.Right: ChangeViewerImage(1); break;
                default: return;
            }
            args.Handled = true;
        }), true);
    }

    private void OpenImageViewer(string title, string[] images, int index)
    {
        galleryReturnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        viewerImages = images;
        viewerTitle = title;
        viewerIndex = index;
        Navigation.IsEnabled = false;
        ImageLightbox.Visibility = Visibility.Visible;
        ShowViewerImage();
        CloseImageButton.Focus(FocusState.Programmatic);
    }

    private void ShowViewerImage()
    {
        ImageZoom.ChangeView(0, 0, 1, true);
        ImageViewerTitle.Text = $"{viewerTitle} · Bild {viewerIndex + 1} von {viewerImages.Length}";
        PreviousImage.IsEnabled = viewerIndex > 0;
        NextImage.IsEnabled = viewerIndex < viewerImages.Length - 1;
        ImageViewerError.Visibility = Visibility.Collapsed;
        ImageLoading.IsActive = true;
        FullImage.Visibility = Visibility.Visible;
        FullImage.Source = new BitmapImage(NetworkAddress.Parse(viewerImages[viewerIndex]));
        AutomationProperties.SetName(FullImage, ImageViewerTitle.Text);
    }

    private void ChangeViewerImage(int delta)
    {
        var index = viewerIndex + delta;
        if (index < 0 || index >= viewerImages.Length) return;
        viewerIndex = index;
        ShowViewerImage();
    }

    private void CloseImageViewer()
    {
        ImageLightbox.Visibility = Visibility.Collapsed;
        FullImage.Source = null;
        ImageLoading.IsActive = false;
        viewerImages = [];
        Navigation.IsEnabled = ready && !closing;
        galleryReturnFocus?.Focus(FocusState.Programmatic);
        galleryReturnFocus = null;
    }

    private void CloseImage_Click(object sender, RoutedEventArgs args) => CloseImageViewer();
    private void PreviousImage_Click(object sender, RoutedEventArgs args) => ChangeViewerImage(-1);
    private void NextImage_Click(object sender, RoutedEventArgs args) => ChangeViewerImage(1);
    private void FitImage_Click(object sender, RoutedEventArgs args) => ImageZoom.ChangeView(0, 0, 1);
    private void ZoomOut_Click(object sender, RoutedEventArgs args) =>
        ImageZoom.ChangeView(null, null, Math.Max(1, ImageZoom.ZoomFactor / 1.25f));
    private void ZoomIn_Click(object sender, RoutedEventArgs args) =>
        ImageZoom.ChangeView(null, null, Math.Min(4, ImageZoom.ZoomFactor * 1.25f));
    private void FullImage_Opened(object sender, RoutedEventArgs args) => ImageLoading.IsActive = false;
    private void FullImage_Failed(object sender, ExceptionRoutedEventArgs args)
    {
        ImageLoading.IsActive = false;
        FullImage.Visibility = Visibility.Collapsed;
        ImageViewerError.Visibility = Visibility.Visible;
    }
}
