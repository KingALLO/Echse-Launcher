using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Echse.App;

public sealed partial class MainWindow
{
    private readonly HashSet<string> artworkAttempted = [];

    private static bool NeedsArtwork(LibraryEntry entry) => string.IsNullOrWhiteSpace(entry.Game.CoverUrl) &&
        (entry.Game.SteamAppId is not null || string.IsNullOrWhiteSpace(entry.Game.PortraitUrl));

    private async Task RefreshLibraryArtworkAsync()
    {
        var pending = state.Library.Where(entry => NeedsArtwork(entry) &&
            !artworkAttempted.Contains(entry.Game.Id)).Take(20).Select(entry => entry.Game).ToList();
        if (pending.Count == 0) return;
        var token = BeginOnlineRequest();
        var version = renderVersion;
        Notice.Title = "Online-Cover";
        Notice.Message = "Fehlende Cover werden anhand der Spieltitel bei Steam gesucht …";
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.IsOpen = true;
        var found = 0;
        try
        {
            foreach (var game in pending)
            {
                var match = game.SteamAppId is not null ? game : await steam.FindArtworkAsync(game.Title, token);
                token.ThrowIfCancellationRequested();
                if (version != renderVersion || closing) return;
                if (match is not null)
                {
                    await SaveArtworkAsync(game.Id, match, token);
                    found++;
                    state = await repository.ReadAsync();
                    if (token.IsCancellationRequested || version != renderVersion || closing) return;
                    UpdateLibrarySidebar();
                    refreshLibrary?.Invoke();
                }
                artworkAttempted.Add(game.Id);
                Notice.Message = $"{artworkAttempted.Count} Titel geprüft; {found} Cover in diesem Durchlauf zugeordnet.";
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            Notice.Title = "Online-Coversuche abgeschlossen";
            Notice.Message = $"{found} von {pending.Count} Covern zugeordnet. " +
                "Bei fehlenden oder mehrdeutigen Treffern kannst du in den Spieldetails „Online-Cover auswählen“ verwenden. " +
                "Weitere fehlende Titel lassen sich mit „Cover laden“ prüfen.";
            Notice.Severity = found < pending.Count ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task SaveArtworkAsync(string gameId, Game artwork, CancellationToken token)
    {
        if (artwork.SteamAppId is not int appId)
            throw new InvalidDataException("Der Cover-Treffer enthält keine gültige Steam-App-ID.");
        var images = await steam.GetDetailsAsync(appId, token);
        token.ThrowIfCancellationRequested();
        await repository.UpdateAsync(current =>
        {
            var entry = current.Library.Single(item => item.Game.Id == gameId);
            entry.Game = entry.Game with
            {
                CoverUrl = images.CoverUrl ?? entry.Game.CoverUrl,
                PortraitUrl = images.PortraitUrl
            };
        });
    }

    private async Task ChooseArtworkAsync(Game game)
    {
        CancelOnlineRequest();
        var query = new TextBox { Header = "Spieltitel auf Steam", Text = game.Title, MaxLength = 200 };
        var prompt = new StackPanel { Spacing = 12 };
        prompt.Children.Add(query);
        prompt.Children.Add(Text("Gesucht wird im öffentlichen Steam-Katalog. Nur die Bilder werden übernommen – keine Spieldateien, Besitzrechte oder Steam-Verknüpfungen.", 13, Muted));
        if (await ShowDialogAsync(new ContentDialog
            {
                Title = "Online-Cover suchen", Content = prompt,
                PrimaryButtonText = "Suchen", CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Primary
            }) != ContentDialogResult.Primary) return;
        var token = BeginOnlineRequest();
        var version = renderVersion;
        try
        {
            var matches = await steam.SearchAsync(query.Text, token);
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            if (matches.Count == 0)
            {
                Notice.Title = "Kein Cover gefunden";
                Notice.Message = "Steam hat keine passenden Titel geliefert. Versuche einen anderen Spielnamen.";
                Notice.Severity = InfoBarSeverity.Warning;
                Notice.IsOpen = true;
                return;
            }
            var list = new ListView
            {
                ItemsSource = matches, DisplayMemberPath = "Title", SelectedIndex = 0, MaxHeight = 240,
                SelectionMode = ListViewSelectionMode.Single
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Steam-Titel für das Cover auswählen");
            var preview = new StackPanel();
            void ShowPreview()
            {
                preview.Children.Clear();
                if (list.SelectedItem is Game selected)
                {
                    var image = Cover(selected, 112, 168, selected.PortraitUrl, fallbackToCover: true);
                    image.HorizontalAlignment = HorizontalAlignment.Center;
                    preview.Children.Add(image);
                }
            }
            list.SelectionChanged += (_, _) => ShowPreview();
            ShowPreview();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(preview);
            content.Children.Add(list);
            var picker = new ContentDialog
            {
                Title = "Cover zuordnen", Content = content, PrimaryButtonText = "Bilder übernehmen",
                CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            list.SelectionChanged += (_, _) => picker.IsPrimaryButtonEnabled = list.SelectedItem is Game;
            list.Loaded += (_, _) =>
            {
                list.SelectedIndex = 0;
                ShowPreview();
                picker.IsPrimaryButtonEnabled = list.SelectedItem is Game;
            };
            if (await ShowDialogAsync(picker) != ContentDialogResult.Primary) return;
            if (list.SelectedItem is not Game artwork)
                throw new InvalidOperationException("Bitte einen Steam-Titel für das Cover auswählen.");
            if (token.IsCancellationRequested || version != renderVersion || closing) return;
            await SaveArtworkAsync(game.Id, artwork, token);
            state = await repository.ReadAsync();
            if (selectedGame?.Id == game.Id)
                selectedGame = state.Library.Single(entry => entry.Game.Id == game.Id).Game;
            await RenderAsync();
            Notify("Online-Cover gespeichert", "Bilderquelle: Steam / jeweilige Rechteinhaber. Spieldatei, Spielzeit und Favoriten bleiben unverändert.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
