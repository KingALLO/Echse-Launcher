using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Echse.App;

public sealed partial class MainWindow
{
    private sealed record SourceSearchState(
        string SourcesKey, string GameTitle, DateTimeOffset StartedAt, SourceSearchResult? Result, string? Error)
    {
        public bool Loading => Result is null && Error is null;
    }

    private static readonly TimeSpan SourceSearchLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, SourceSearchState> sourceSearches = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource sourceSearchCancellation = new();
    private CancellationTokenSource? activeSourceSearch;
    private string? activeSourceGameId;
    private WebsiteSourceService? websiteSources;
    private StackPanel? sourceGameHost;

    private WebsiteSourceService WebsiteSources => websiteSources ??= new WebsiteSourceService();

    private static string SourcesKey(ConnectionSettings settings) => string.Join('\n', settings.SourceUrls);

    private static bool SearchMatches(SourceSearchState search, Game game, ConnectionSettings settings) =>
        search.SourcesKey == SourcesKey(settings) && search.GameTitle == game.Title;

    private IReadOnlyList<SourceDownload> SourceDownloads(Game game) =>
        sourceSearches.GetValueOrDefault(game.Id) is { Result: { } result } search && SearchMatches(search, game, state.Settings)
            ? result.Downloads
            : [];

    private void CancelActiveSourceSearch()
    {
        if (activeSourceSearch is null) return;
        var cancellation = activeSourceSearch;
        var gameId = activeSourceGameId;
        activeSourceSearch = null;
        activeSourceGameId = null;
        if (gameId is not null && sourceSearches.GetValueOrDefault(gameId) is { Loading: true })
            sourceSearches.Remove(gameId);
        cancellation.Cancel();
    }

    private void CancelSourceSearchOnNavigation()
    {
        if (page is not ("game" or "library-game") || selectedGame?.Id != activeSourceGameId)
            CancelActiveSourceSearch();
    }

    private async Task EnsureSourceSearchAsync(Game game, bool force = false)
    {
        var settings = state.Settings;
        if (settings.SourceUrls.Count == 0 || closing) return;
        var key = SourcesKey(settings);
        if (sourceSearches.GetValueOrDefault(game.Id) is { } existing && SearchMatches(existing, game, settings) &&
            (existing.Loading || !force && existing.Error is null && DateTimeOffset.UtcNow - existing.StartedAt < SourceSearchLifetime))
            return;
        CancelActiveSourceSearch();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sourceSearchCancellation.Token);
        activeSourceSearch = cancellation;
        activeSourceGameId = game.Id;
        var started = new SourceSearchState(key, game.Title, DateTimeOffset.UtcNow, null, null);
        sourceSearches[game.Id] = started;
        RefreshSourceViews(game);
        SourceSearchState finished;
        try
        {
            var result = await WebsiteSources.SearchAsync(settings.SourceUrls, game, force, cancellation.Token);
            finished = started with { Result = result };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(sourceSearches.GetValueOrDefault(game.Id), started))
                sourceSearches.Remove(game.Id);
            return;
        }
        catch (Exception exception)
        {
            LocalServices.Log(exception);
            finished = started with { Error = exception.Message };
        }
        finally
        {
            if (ReferenceEquals(activeSourceSearch, cancellation))
            {
                activeSourceSearch = null;
                activeSourceGameId = null;
            }
        }
        if (ReferenceEquals(sourceSearches.GetValueOrDefault(game.Id), started))
            sourceSearches[game.Id] = finished;
        RefreshSourceViews(game);
    }

    private void RefreshSourceViews(Game game)
    {
        if (closing || selectedGame?.Id != game.Id) return;
        if (sourceGameHost is not null) RenderSourceGameContent(selectedGame);
        if (page == "library-game" && state.Library.FirstOrDefault(item => item.Game.Id == game.Id) is { } entry)
            refreshLibraryGameStatus?.Invoke(entry);
    }

    private void RenderSourceGameContent(Game game)
    {
        if (sourceGameHost is null) return;
        var host = sourceGameHost;
        host.Spacing = 12;
        host.Children.Clear();
        host.Children.Add(Eyebrow("DOWNLOADS · DEINE QUELLEN"));
        if (state.Settings.SourceUrls.Count == 0)
        {
            host.Children.Add(Text("Keine Download-Quellen eingerichtet. Füge in den Einstellungen Websites hinzu, dann wird hier automatisch nach Magnet-Links für dieses Spiel gesucht.", 14, Muted));
            host.Children.Add(Button("Zu den Einstellungen", async () =>
            {
                page = "settings";
                SelectNavigation("settings");
                await RenderAsync();
            }));
            return;
        }
        var search = sourceSearches.GetValueOrDefault(game.Id);
        if (search is null || !SearchMatches(search, game, state.Settings))
        {
            host.Children.Add(Text("Für dieses Spiel wurde noch nicht gesucht.", 14, Muted));
            host.Children.Add(Button("Quellen durchsuchen", () => EnsureSourceSearchAsync(game, force: true), true));
            return;
        }
        if (search.Loading)
        {
            host.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
            host.Children.Add(Text($"{state.Settings.SourceUrls.Count} {(state.Settings.SourceUrls.Count == 1 ? "Quelle wird" : "Quellen werden")} nach „{game.Title}“ durchsucht …", 14, Muted));
            return;
        }
        if (search.Error is not null)
        {
            host.Children.Add(Text(search.Error, 14, Muted));
            host.Children.Add(Button("Erneut suchen", () => EnsureSourceSearchAsync(game, force: true)));
            return;
        }
        var result = search.Result!;
        foreach (var error in result.Errors)
            host.Children.Add(Text($"{SourceLabel(error.SourceUrl)}: {error.Message}", 12, Muted));
        if (result.Downloads.Count == 0)
            host.Children.Add(Text("Auf den ausgelesenen Seiten wurden keine passenden Magnet-Links gefunden. Nicht jedes Spiel aus dem Steam-Katalog wird von deinen Quellen angeboten. Prüfe auch die Suche direkt auf der Quellwebsite; Inhalte, die erst nach Anmeldung oder durch JavaScript erscheinen, werden nicht erfasst.", 14, Muted));
        else
        {
            host.Children.Add(Text($"{result.Downloads.Count} Treffer · beste Titelübereinstimmung zuerst", 15));
            foreach (var download in result.Downloads.Take(50))
            {
                var card = new Grid { ColumnSpacing = 16 };
                card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel { Spacing = 6 };
                var title = Text(download.Title, 16);
                title.TextWrapping = TextWrapping.WrapWholeWords;
                details.Children.Add(title);
                details.Children.Add(Text(DownloadFacts(download), 12, download.IsLikely ? Accent : Muted));
                details.Children.Add(StoreLink("Quellseite öffnen ↗", download.PageUrl));
                card.Children.Add(details);
                var install = Button("↓  Herunterladen", () => StartSourceDownloadAsync(game, download), download.IsLikely);
                install.VerticalAlignment = VerticalAlignment.Center;
                ToolTipService.SetToolTip(install, $"Magnet-Link an deinen qBittorrent-Server {state.Settings.QBitUrl} übergeben");
                Grid.SetColumn(install, 1);
                card.Children.Add(install);
                host.Children.Add(Panel(card));
            }
        }
        host.Children.Add(Text($"Stand {FormatDate(search.StartedAt)} · Titel und Dateien sind nicht geprüft. Lade nur, wofür du die Rechte besitzt.", 11, Muted));
        host.Children.Add(Button("Erneut suchen", () => EnsureSourceSearchAsync(game, force: true)));
    }

    private string SourceLabel(string sourceUrl) =>
        WebsiteSources.CachedName(sourceUrl) ??
        (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ? uri.Host : sourceUrl);

    private static string DownloadFacts(SourceDownload download) =>
        $"{(download.IsCollection ? "Sammlung mit diesem Spiel – enthält weitere Titel" :
            download.IsLikely ? "Titel passt sehr wahrscheinlich" : "Release-Zusätze – bitte prüfen")} · {download.SourceName}\n" +
        $"BTIH {MagnetLink.GetInfoHash(download.MagnetUri)}";

    private async Task ShowSourceDownloadChoiceAsync(Game game)
    {
        var downloads = SourceDownloads(game);
        if (downloads.Count == 0)
            throw new InvalidOperationException("In deinen Quellen wurde kein passender Download gefunden.");
        if (downloads.Count == 1 && downloads[0].IsLikely)
        {
            await StartSourceDownloadAsync(game, downloads[0]);
            return;
        }
        var options = new RadioButtons { Header = "Titel und Quelle prüfen. Mögliche Treffer können zu einem anderen Spiel gehören." };
        foreach (var download in downloads)
            options.Items.Add(new RadioButton { Content = $"{download.Title}\n{DownloadFacts(download)}", Tag = download });
        options.SelectedIndex = 0;
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = $"„{game.Title}“ installieren",
            Content = new ScrollViewer { Content = options, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "Weiter",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary
        });
        if (result == ContentDialogResult.Primary && options.SelectedItem is RadioButton { Tag: SourceDownload selected })
            await StartSourceDownloadAsync(game, selected);
    }

    private async Task StartSourceDownloadAsync(Game game, SourceDownload download)
    {
        var snapshot = await repository.ReadAsync();
        if (!snapshot.Settings.SourceUrls.Contains(download.SourceUrl, StringComparer.Ordinal))
            throw new InvalidOperationException("Diese Quelle wurde inzwischen entfernt. Bitte erneut suchen.");
        MagnetLink.Validate(download.MagnetUri);
        await StartManagedDownloadAsync(game, download.MagnetUri,
            $"„{download.Title}“ von {download.SourceName}" +
            (download.IsCollection ? "\nAchtung: Sammlung mit mehreren Spielen, kein Einzelspiel-Download." :
                download.IsLikely ? "" : "\nZusätzliche Release-Angaben bitte auf der Quellseite prüfen."), snapshot.Settings);
    }

    private void RenderSourceSettings(StackPanel form)
    {
        form.Children.Add(Text("Download-Quellen", 22));
        form.Children.Add(Text("Füge Websites mit öffentlicher Suchfunktion hinzu. Beim Öffnen eines Spiels durchsucht Echse passende Trefferseiten nach Magnet-Links. Anmeldung, JavaScript und Browser-Prüfungen werden nicht ausgeführt.", 14, Muted));
        var list = new StackPanel { Spacing = 8 };
        if (state.Settings.SourceUrls.Count == 0)
            list.Children.Add(Text("Noch keine Quellen eingerichtet.", 13, Muted));
        foreach (var url in state.Settings.SourceUrls)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(Text(SourceLabel(url), 15));
            details.Children.Add(Text(url, 12, Muted));
            row.Children.Add(details);
            var remove = Button("Entfernen", async () =>
            {
                await repository.UpdateAsync(current => current.Settings.SourceUrls.Remove(url));
                state = await repository.ReadAsync();
                sourceSearches.Clear();
                await RenderAsync();
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            list.Children.Add(Panel(row));
        }
        form.Children.Add(list);
        var input = new TextBox { Header = "Website-Adresse hinzufügen", PlaceholderText = "https://beispiel.de/" };
        form.Children.Add(input);
        form.Children.Add(Button("Quelle prüfen & hinzufügen", async () =>
        {
            var url = WebsiteSourceService.NormalizeUrl(input.Text);
            if (state.Settings.SourceUrls.Contains(url, StringComparer.Ordinal))
                throw new InvalidOperationException("Diese Quelle ist bereits eingerichtet.");
            if (state.Settings.SourceUrls.Count >= WebsiteSourceService.MaxSources)
                throw new InvalidOperationException($"Es können höchstens {WebsiteSourceService.MaxSources} Quellen eingerichtet werden.");
            var name = await WebsiteSources.ValidateSourceAsync(url, sourceSearchCancellation.Token);
            await repository.UpdateAsync(current =>
            {
                if (current.Settings.SourceUrls.Contains(url, StringComparer.Ordinal))
                    throw new InvalidOperationException("Diese Quelle ist bereits eingerichtet.");
                if (current.Settings.SourceUrls.Count >= WebsiteSourceService.MaxSources)
                    throw new InvalidOperationException($"Es können höchstens {WebsiteSourceService.MaxSources} Quellen eingerichtet werden.");
                current.Settings.SourceUrls.Add(url);
            });
            state = await repository.ReadAsync();
            sourceSearches.Clear();
            await RenderAsync();
            Notify("Quelle hinzugefügt", name);
        }, true));
    }
}
