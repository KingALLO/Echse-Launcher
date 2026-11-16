using System.Diagnostics;
using Echse.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Echse.App;

public sealed partial class MainWindow
{
    private bool installationBusy;
    private bool startingDownload;
    private sealed record TorrentProgress(TorrentStatus? Torrent, DateTimeOffset? UpdatedAt,
        DateTimeOffset CheckedAt, string? Error);
    private readonly Dictionary<(string Server, string Hash), TorrentProgress> torrentProgress = [];

    private static (string Server, string Hash) ProgressKey(InstallationDownload job) =>
        (job.ServerUrl, job.Hash.ToUpperInvariant());

    private TorrentProgress? ProgressFor(InstallationDownload job) =>
        torrentProgress.GetValueOrDefault(ProgressKey(job));

    private async Task<TorrentStatus> ReadInstallationTorrentAsync(ConnectionSettings settings, InstallationDownload job)
    {
        var key = ProgressKey(job);
        try
        {
            if (settings.QBitUrl != job.ServerUrl)
                throw new InvalidOperationException("Die qBittorrent-Adresse wurde geändert. Zum Fortsetzen die ursprüngliche Verbindung wiederherstellen: " + job.ServerUrl);
            using var client = CreateQBitClient(settings);
            var torrent = await client.GetTorrentAsync(job.Hash)
                ?? throw new InvalidOperationException("Der zugeordnete Torrent wurde in qBittorrent nicht gefunden. Download-Zuordnung lösen und erneut starten oder vorhandene Dateien manuell verknüpfen.");
            var now = DateTimeOffset.UtcNow;
            torrentProgress[key] = new(torrent, now, now, null);
            return torrent;
        }
        catch (Exception exception)
        {
            var previous = torrentProgress.GetValueOrDefault(key);
            torrentProgress[key] = new(previous?.Torrent, previous?.UpdatedAt, DateTimeOffset.UtcNow, exception.Message);
            throw;
        }
    }

    private void RenderTorrentProgress(StackPanel host, InstallationDownload? job)
    {
        host.Children.Clear();
        host.Visibility = job is not null && !job.FilesPrepared && job.Phase != InstallationPhase.Installed
            ? Visibility.Visible : Visibility.Collapsed;
        if (host.Visibility == Visibility.Collapsed || job is null) return;
        var snapshot = ProgressFor(job);
        if (snapshot?.Torrent is { } torrent)
        {
            var title = Text($"qBittorrent · {torrent.Progress:P1} · {TorrentStateLabel(torrent.State)}", 16);
            host.Children.Add(title);
            var bar = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Value = Math.Clamp(torrent.Progress * 100, 0, 100),
                Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, "qBittorrent-Downloadfortschritt");
            host.Children.Add(bar);
            var size = torrent.CompletedBytes >= 0 && torrent.TotalSize >= 0
                ? $"{FormatStorage(torrent.CompletedBytes)} von {FormatStorage(torrent.TotalSize)}"
                : "Größe nicht verfügbar";
            var eta = torrent.EtaSeconds is >= 0 and < 8_640_000
                ? TimeSpan.FromSeconds(torrent.EtaSeconds).ToString(@"d\.hh\:mm\:ss")
                : "unbekannt";
            host.Children.Add(Text($"{size} · {torrent.DownloadSpeed / 1024d / 1024d:F1} MiB/s · Restzeit: {eta}", 13, Muted));
            host.Children.Add(Text(torrent.Name, 12, Muted));
        }
        else if (snapshot?.Error is null)
        {
            host.Children.Add(Text("qBittorrent-Fortschritt wird abgerufen …", 14, Muted));
            host.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8 });
        }
        if (snapshot?.Error is { } error)
            host.Children.Add(Text("qBittorrent-Aktualisierung fehlgeschlagen: " + error, 13));
        var updated = snapshot?.UpdatedAt is { } time
            ? $"Stand {time.ToLocalTime():T}" : "Noch keine Fortschrittsdaten";
        host.Children.Add(Text(updated + (snapshot?.Error is not null
            ? " · angezeigte Werte sind möglicherweise veraltet"
            : " · automatisch alle 5 Sekunden"), 12, Muted));
    }

    private static string TorrentStateLabel(string torrentState) => torrentState switch
    {
        "downloading" or "forcedDL" => "Wird heruntergeladen",
        "metaDL" or "forcedMetaDL" => "Metadaten werden geladen",
        "stalledDL" => "Wartet auf verfügbare Quellen",
        "queuedDL" => "In der Download-Warteschlange",
        "pausedDL" or "stoppedDL" => "Download pausiert · in qBittorrent fortsetzen",
        "checkingDL" or "checkingUP" or "checkingResumeData" => "Dateien werden überprüft",
        "moving" => "Dateien werden verschoben",
        "error" => "qBittorrent meldet einen Fehler",
        "missingFiles" => "Dateien fehlen",
        "uploading" or "stalledUP" or "forcedUP" => "Download abgeschlossen · Seeding",
        "queuedUP" or "pausedUP" or "stoppedUP" => "Download abgeschlossen",
        _ => torrentState
    };

    private async Task<string?> PickDownloadDirectoryAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task StartManagedDownloadAsync(Game game, string magnet, string description, ConnectionSettings expected)
    {
        if (startingDownload) throw new InvalidOperationException("Bitte zuerst die laufende Download-Auswahl abschließen.");
        startingDownload = true;
        try
        {
            await CloseDialogAsync();
            var server = NetworkAddress.Parse(expected.QBitUrl);
            if (!server.IsLoopback && !server.IdnHost.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                if (!await ConfirmAsync("Download auf entferntem Server?",
                    $"{description}\n\n{expected.QBitUrl} ist kein lokaler Server. Der Download bleibt im Standardordner dieses Servers; Entpacken und Installation erfolgen manuell. Für den automatischen Ablauf bitte localhost als qBittorrent-Adresse verwenden. Lade nur Inhalte, für die du die Rechte besitzt.",
                    "Nur Download starten")) return;
                state = await repository.ReadAsync();
                CheckDownloadSettings(state.Settings, expected);
                using var remote = CreateQBitClient();
                await remote.AddAsync(magnet);
                Notify("Download übergeben", "Die Dateien werden auf dem entfernten qBittorrent-Server abgelegt.");
                return;
            }

            var root = expected.DownloadDirectory;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = await PickDownloadDirectoryAsync();
                if (root is null) return;
            }
            while (true)
            {
                root = GameInstallation.ValidateDirectory(root);
                var answer = await ShowDialogAsync(new ContentDialog
                {
                    Title = $"„{game.Title}“ herunterladen",
                    Content = Text($"{description}\n\nDownload-Basisordner:\n{root}\n\nEchse erstellt darunter „{DownloadedGameFiles.FolderName(game.Title)}“ (bei Bedarf mit Nummer). Dateien landen in dessen Unterordner „Download“. ZIP, 7z und RAR werden nach Abschluss entpackt. Vor jedem Setup-Start wirst du erneut gefragt.\n\nTitel und Dateien sind ungeprüft. Lade nur Inhalte, für die du die Rechte besitzt.", 14),
                    PrimaryButtonText = "Download starten",
                    SecondaryButtonText = "Ordner ändern",
                    CloseButtonText = "Abbrechen",
                    DefaultButton = ContentDialogButton.Close
                });
                if (answer == ContentDialogResult.None) return;
                if (answer == ContentDialogResult.Primary) break;
                var selected = await PickDownloadDirectoryAsync();
                if (selected is not null) root = selected;
            }
            state = await repository.ReadAsync();
            CheckDownloadSettings(state.Settings, expected);
            var existing = state.Library.FirstOrDefault(entry => entry.Game.Id == game.Id);
            if (existing?.Download is not null || existing is not null && GameInstallation.IsInstalled(existing))
                throw new InvalidOperationException("Dieses Spiel hat bereits eine Installation oder einen Download. Bitte den vorhandenen Ablauf unter Downloads fortsetzen oder die Download-Zuordnung dort lösen.");
            var hash = MagnetLink.GetInfoHash(magnet);
            using var client = CreateQBitClient();
            if (await client.GetTorrentAsync(hash) is not null)
                throw new InvalidOperationException("Dieser Torrent ist bereits in qBittorrent vorhanden. Sein Speicherpfad wird nicht ungefragt geändert. Vorhandene Dateien bitte über die Spieleinstellungen verknüpfen.");
            var directory = DownloadedGameFiles.CreateGameDirectory(root, game.Title);
            var job = new InstallationDownload { Hash = hash, ServerUrl = expected.QBitUrl, Directory = directory };
            await repository.UpdateAsync(current =>
            {
                CheckDownloadSettings(current.Settings, expected);
                var entry = current.Library.FirstOrDefault(item => item.Game.Id == game.Id);
                if (entry is null) current.Library.Add(entry = new LibraryEntry { Game = game });
                if (entry.Download is not null) throw new InvalidOperationException("Für dieses Spiel läuft bereits ein Download.");
                entry.Download = job;
                entry.InstallationDirectory = directory;
                current.Settings.DownloadDirectory = root;
            });
            try { await client.AddAsync(magnet, job.DownloadDirectory); }
            catch (Exception exception)
            {
                await SetInstallationPhaseAsync(game.Id, InstallationPhase.Failed, exception.Message);
                throw;
            }
            await RenderAsync();
            Notify("Download gestartet", $"Ziel: {job.DownloadDirectory}. Echse verarbeitet die Dateien nach Abschluss, solange es geöffnet ist.");
        }
        finally { startingDownload = false; }
    }

    private static void CheckDownloadSettings(ConnectionSettings current, ConnectionSettings expected)
    {
        if (current.QBitUrl != expected.QBitUrl || current.CatalogUrl != expected.CatalogUrl ||
            !current.SourceUrls.SequenceEqual(expected.SourceUrls))
            throw new InvalidOperationException("Die Download-Einstellungen wurden geändert. Bitte erneut versuchen.");
    }

    private async Task CheckInstallationsAsync()
    {
        if (installationBusy || startingDownload || activeDialog is not null) return;
        var snapshot = await repository.ReadAsync();
        foreach (var entry in snapshot.Library.Where(entry =>
            entry.Download?.Phase is InstallationPhase.Downloading or InstallationPhase.Preparing))
        {
            if (closing || activeDialog is not null || startingDownload || installationBusy) return;
            await ProcessInstallationAsync(entry.Game.Id);
        }
    }

    private async Task SetInstallationPhaseAsync(string gameId, InstallationPhase phase, string? error = null)
    {
        await repository.UpdateAsync(current =>
        {
            var job = current.Library.Single(entry => entry.Game.Id == gameId).Download
                ?? throw new InvalidOperationException("Die Download-Zuordnung wurde entfernt.");
            job.Phase = phase;
            job.Error = error;
        });
        state = await repository.ReadAsync();
        if (page == "library-game" && selectedGame?.Id == gameId)
            refreshLibraryGameStatus?.Invoke(state.Library.Single(entry => entry.Game.Id == gameId));
    }

    private async Task ProcessInstallationAsync(string gameId)
    {
        if (installationBusy) throw new InvalidOperationException("Eine Installation wird bereits verarbeitet. Bitte kurz warten.");
        installationBusy = true;
        try
        {
            state = await repository.ReadAsync();
            var entry = state.Library.Single(item => item.Game.Id == gameId);
            var job = entry.Download ?? throw new InvalidOperationException("Keine Download-Zuordnung vorhanden.");
            if (job.Phase == InstallationPhase.Installed && GameInstallation.IsInstalled(entry)) return;
            if (!job.FilesPrepared)
            {
                var torrent = await ReadInstallationTorrentAsync(state.Settings, job);
                if (page == "library-game" && selectedGame?.Id == gameId)
                    refreshLibraryGameStatus?.Invoke(entry);
                DownloadedGameFiles.ValidateTorrent(job, torrent);
                if (!torrent.IsComplete)
                {
                    if (job.Phase != InstallationPhase.Downloading || job.Error is not null)
                        await SetInstallationPhaseAsync(gameId, InstallationPhase.Downloading);
                    return;
                }
                await SetInstallationPhaseAsync(gameId, InstallationPhase.Preparing);
                var prepared = await DownloadedGameFiles.ExtractAsync(job);
                await repository.UpdateAsync(current =>
                {
                    var download = current.Library.Single(item => item.Game.Id == gameId).Download!;
                    download.PreparedDirectory = prepared;
                    download.FilesPrepared = true;
                });
            }
            await CompleteInstallationAsync(gameId);
        }
        catch (Exception exception)
        {
            ShowError(exception);
            await SetInstallationPhaseAsync(gameId, InstallationPhase.Failed, exception.Message);
        }
        finally
        {
            installationBusy = false;
            if (page == "library-game" && selectedGame?.Id == gameId &&
                state.Library.FirstOrDefault(entry => entry.Game.Id == gameId) is { } current)
                refreshLibraryGameStatus?.Invoke(current);
        }
    }

    private async Task CompleteInstallationAsync(string gameId)
    {
        var entry = (await repository.ReadAsync()).Library.Single(item => item.Game.Id == gameId);
        var job = entry.Download!;
        if (job.PreparedDirectory is not null) GameInstallation.ValidateDirectory(job.PreparedDirectory);
        var files = await DownloadedGameFiles.DiscoverAsync(job.Directory);
        if (job.SetupStarted && !job.SetupCompleted)
        {
            await SetInstallationPhaseAsync(gameId, InstallationPhase.AwaitingExecutable);
            if (activeDialog is not null || startingDownload || closing) return;
            if (!await ConfirmAsync("Ist das Setup erfolgreich abgeschlossen?",
                "Echse konnte den erfolgreichen Abschluss des zuvor gestarteten Setups nicht bestätigen. Bitte nur fortfahren, wenn das Setup inzwischen erfolgreich beendet wurde. Andernfalls abbrechen und unter Downloads „Setup erneut auswählen“ verwenden.",
                "Abgeschlossen, EXE suchen")) return;
            await repository.UpdateAsync(current =>
                current.Library.Single(item => item.Game.Id == gameId).Download!.SetupCompleted = true);
        }
        if (!job.SetupStarted && files.Installers.Count > 0)
        {
            await SetInstallationPhaseAsync(gameId, InstallationPhase.AwaitingSetup);
            if (activeDialog is not null || startingDownload || closing) return;
            var setup = await ChooseInstallationFileAsync("Setup auswählen", files.Installers, job.Directory);
            if (setup is null) return;
            var destination = Path.Combine(job.Directory, "Spiel");
            if (!await ConfirmAsync("Darf Echse dieses Setup ausführen?",
                $"Spiel: {entry.Game.Title}\nDatei: {setup}\n\nDie Datei wurde nicht auf Sicherheit geprüft. Führe sie nur aus, wenn du der Quelle vertraust. Windows kann zusätzlich nach Administratorrechten fragen.\n\nWähle im sichtbaren Setup möglichst diesen Installationsordner:\n{destination}\n\nEchse setzt keine unbeaufsichtigten Installationsparameter und umgeht keine Windows-Rückfragen.",
                "Setup ausführen")) return;
            DownloadedGameFiles.RejectLinks(Path.GetDirectoryName(setup)!);
            if (!File.Exists(setup)) throw new FileNotFoundException("Das Setup wurde nicht gefunden.", setup);
            Directory.CreateDirectory(destination);
            await repository.UpdateAsync(current =>
            {
                var download = current.Library.Single(item => item.Game.Id == gameId).Download!;
                // Persist before starting: after a restart an installer must never run again without consent.
                download.SetupStarted = true;
                download.Phase = InstallationPhase.AwaitingExecutable;
            });
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup)!
            }) ?? throw new InvalidOperationException("Das Setup konnte nicht gestartet werden.");
            await process.WaitForExitAsync();
            if (process.ExitCode is not (0 or 3010 or 1641))
                throw new InvalidOperationException($"Das Setup wurde mit Code {process.ExitCode} beendet. Bitte die Installation prüfen.");
            await repository.UpdateAsync(current =>
                current.Library.Single(item => item.Game.Id == gameId).Download!.SetupCompleted = true);
            if (process.ExitCode != 0)
                ShowNotice("Neustart angefordert", "Das Setup fordert einen Windows-Neustart an. Echse startet Windows nicht automatisch neu.", InfoBarSeverity.Warning);
            files = await DownloadedGameFiles.DiscoverAsync(job.Directory);
        }
        await SetInstallationPhaseAsync(gameId, InstallationPhase.AwaitingExecutable);
        entry = (await repository.ReadAsync()).Library.Single(item => item.Game.Id == gameId);
        var candidates = files.Executables.ToList();
        if (entry.InstallationDirectory is { } selected && !selected.Equals(job.Directory, StringComparison.OrdinalIgnoreCase))
            candidates.AddRange((await DownloadedGameFiles.DiscoverAsync(selected)).Executables);
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            ShowNotice("Noch keine Spiel-EXE gefunden",
                "Unter Downloads kannst du erneut prüfen, ein Setup erneut freigeben oder eine vorhandene EXE verknüpfen. Bei Installation außerhalb des Spielordners zuerst den tatsächlichen Installationsordner auswählen.",
                InfoBarSeverity.Warning);
            return;
        }
        if (activeDialog is not null || startingDownload || closing) return;
        var executable = await ChooseInstallationFileAsync("Spiel-EXE auswählen", candidates, job.Directory);
        if (executable is null) return;
        await repository.UpdateAsync(current =>
        {
            var item = current.Library.Single(item => item.Game.Id == gameId);
            GameInstallation.LinkExecutable(item, executable);
        });
        state = await repository.ReadAsync();
        refreshLibrary?.Invoke();
        if (ShowingLibrary) UpdateLibrarySidebar();
        if (page == "library-game" && selectedGame?.Id == gameId)
            refreshLibraryGameStatus?.Invoke(state.Library.Single(item => item.Game.Id == gameId));
        Notify("Spiel bereit", $"„{entry.Game.Title}“ ist verknüpft und kann über „Spielen“ gestartet werden.");
    }

    private async Task<string?> ChooseInstallationFileAsync(string title, IReadOnlyList<string> paths, string root)
    {
        if (paths.Count == 1) return paths[0];
        var choices = new RadioButtons { Header = "Mehrere Dateien gefunden. Bitte die richtige Datei auswählen." };
        foreach (var path in paths)
            choices.Items.Add(new RadioButton { Content = Path.GetRelativePath(root, path), Tag = path });
        choices.SelectedIndex = 0;
        var answer = await ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = choices, MaxHeight = 420 },
            PrimaryButtonText = "Auswählen", CloseButtonText = "Später", DefaultButton = ContentDialogButton.Close
        });
        return answer == ContentDialogResult.Primary ? (choices.SelectedItem as RadioButton)?.Tag as string : null;
    }

    private static string InstallationStatus(InstallationDownload job) => job.Phase switch
    {
        InstallationPhase.Downloading => "Download läuft · Abschluss wird automatisch geprüft",
        InstallationPhase.Preparing => "Archive werden entpackt",
        InstallationPhase.AwaitingSetup => "Setup wartet auf deine Freigabe",
        InstallationPhase.AwaitingExecutable when job.SetupStarted && !job.SetupCompleted =>
            "Setup gestartet · Abschluss noch nicht bestätigt",
        InstallationPhase.AwaitingExecutable => "Spiel-EXE suchen oder auswählen",
        InstallationPhase.Installed => "Installiert · Spiel-EXE verknüpft",
        _ => "Installation angehalten: " + job.Error
    };

    private void RenderInstallationJobs(StackPanel target)
    {
        foreach (var entry in state.Library.Where(item => item.Download is not null))
        {
            var job = entry.Download!;
            var item = new StackPanel { Spacing = 10 };
            item.Children.Add(Text(entry.Game.Title, 20));
            item.Children.Add(Text(InstallationStatus(job), 14, Muted));
            item.Children.Add(Text(job.Directory, 12, Muted));
            if (job.Phase != InstallationPhase.Installed)
            {
                var resume = Button(job.Phase == InstallationPhase.AwaitingSetup ? "Setup freigeben" : "Erneut prüfen / fortsetzen",
                    () => ProcessInstallationAsync(entry.Game.Id), true);
                resume.IsEnabled = !installationBusy;
                item.Children.Add(resume);
                if (job.SetupStarted)
                {
                    var retry = Button("Setup erneut auswählen", async () =>
                    {
                        if (installationBusy) throw new InvalidOperationException("Bitte die laufende Installation abwarten.");
                        await repository.UpdateAsync(current =>
                        {
                            var download = current.Library.Single(value => value.Game.Id == entry.Game.Id).Download!;
                            download.SetupStarted = false;
                            download.SetupCompleted = false;
                        });
                        await ProcessInstallationAsync(entry.Game.Id);
                    });
                    retry.IsEnabled = !installationBusy;
                    item.Children.Add(retry);
                }
                item.Children.Add(Button("Installationsordner auswählen", () => SelectGameDirectoryAsync(entry.Game.Id)));
                item.Children.Add(Button("Vorhandene EXE verknüpfen", () => LinkExecutableAsync(entry.Game.Id)));
            }
            item.Children.Add(Button("Spiel öffnen", () => OpenLibraryGameAsync(entry.Game)));
            var forget = Button("Download-Zuordnung lösen", async () =>
            {
                if (installationBusy) throw new InvalidOperationException("Bitte die laufende Installation abwarten.");
                if (!await ConfirmAsync("Download-Zuordnung lösen?",
                    "Echse überwacht diesen Download danach nicht mehr. Der Torrent, alle Dateien und eine bereits verknüpfte Spiel-EXE bleiben erhalten.", "Zuordnung lösen")) return;
                if (installationBusy) throw new InvalidOperationException("Bitte die laufende Installation abwarten.");
                await repository.UpdateAsync(current =>
                    current.Library.Single(value => value.Game.Id == entry.Game.Id).Download = null);
                await RenderAsync();
            });
            forget.IsEnabled = !installationBusy;
            item.Children.Add(forget);
            target.Children.Add(Panel(item));
        }
    }
}
