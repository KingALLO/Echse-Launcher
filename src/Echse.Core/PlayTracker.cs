using System.Diagnostics;

namespace Echse.Core;

public sealed class PlayTracker(StateRepository repository)
{
    private readonly Dictionary<string, Task> sessions = [];
    private readonly CancellationTokenSource stopping = new();
    public event Action<Exception>? Error;

    public bool IsRunning(string id)
    {
        lock (sessions) return sessions.ContainsKey(id);
    }

    public async Task LaunchAsync(string id)
    {
        var state = await repository.ReadAsync();
        var entry = state.Library.Single(item => item.Game.Id == id);
        var path = entry.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bitte zuerst eine vorhandene lokale EXE-Datei auswählen.");

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sessions)
        {
            if (stopping.IsCancellationRequested)
                throw new InvalidOperationException("Der Launcher wird gerade geschlossen.");
            if (!sessions.TryAdd(id, completion.Task))
                throw new InvalidOperationException("Dieses Spiel läuft bereits.");
        }
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!
            }) ?? throw new InvalidOperationException("Das Spiel konnte nicht gestartet werden.");
            var clock = Stopwatch.StartNew();
            process.Exited += (_, _) => { lock (clock) clock.Stop(); };
            process.EnableRaisingEvents = true;
            if (process.HasExited) { lock (clock) clock.Stop(); }
            try
            {
                await repository.UpdateAsync(current =>
                {
                    var game = current.Library.Single(item => item.Game.Id == id);
                    game.LastPlayed = DateTimeOffset.UtcNow;
                    game.LaunchCount++;
                });
            }
            catch (Exception exception) { Error?.Invoke(exception); }
            _ = TrackAsync(id, process, clock, completion);
        }
        catch
        {
            process?.Dispose();
            lock (sessions) sessions.Remove(id);
            completion.TrySetResult();
            throw;
        }
    }

    private async Task TrackAsync(string id, Process process, Stopwatch clock, TaskCompletionSource completion)
    {
        double credited = 0;
        async Task SaveElapsedAsync()
        {
            double elapsed;
            lock (clock) elapsed = clock.Elapsed.TotalSeconds;
            await repository.UpdateAsync(state =>
                state.Library.Single(entry => entry.Game.Id == id).PlaySeconds += elapsed - credited);
            credited = elapsed;
        }
        try
        {
            var exited = process.WaitForExitAsync();
            while (!exited.IsCompleted && !stopping.IsCancellationRequested)
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(15), stopping.Token));
                if (exited.IsCompleted)
                    await exited;
                try { await SaveElapsedAsync(); }
                catch (Exception exception) { Error?.Invoke(exception); }
            }
            lock (clock) clock.Stop();
            try { await SaveElapsedAsync(); }
            catch (Exception exception) { Error?.Invoke(exception); }
        }
        catch (Exception exception) { Error?.Invoke(exception); }
        finally
        {
            process.Dispose();
            lock (sessions) sessions.Remove(id);
            completion.TrySetResult();
        }
    }

    public async Task StopAsync()
    {
        Task[] pending;
        lock (sessions)
        {
            stopping.Cancel();
            pending = sessions.Values.ToArray();
        }
        await Task.WhenAll(pending);
    }
}
