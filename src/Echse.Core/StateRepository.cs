using System.Text.Json;

namespace Echse.Core;

public sealed class StateRepository(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string filePath = Path.Combine(directory, "state.json");
    private LauncherState state = new();
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return;
            await using var stream = File.OpenRead(filePath);
            var loaded = await JsonSerializer.DeserializeAsync<LauncherState>(stream, JsonOptions)
                ?? throw new InvalidDataException("Die gespeicherten Launcher-Daten sind leer.");
            if (loaded.Version != 1 || loaded.Profile is null || loaded.Settings is null ||
                loaded.Library is null || loaded.Views is null || loaded.CatalogCache is null ||
                loaded.Library.Any(entry => entry is null || entry.Game is null) || loaded.SteamDetails is null ||
                loaded.SteamDetails.Any(game => game is null) ||
                (loaded.SteamCatalog is { } steam &&
                    (steam.Popular is null || steam.NewReleases is null || steam.Specials is null || steam.Upcoming is null)))
                throw new InvalidDataException("Die gespeicherten Launcher-Daten haben ein ungültiges Format.");
            loaded.Settings.SourceUrls = loaded.Settings.SourceUrls?.Where(url => !string.IsNullOrWhiteSpace(url)).ToList() ?? [];
            state = loaded;
        }
        finally { gate.Release(); }
    }

    public async Task<LauncherState> ReadAsync()
    {
        await gate.WaitAsync();
        try { return Clone(state); }
        finally { gate.Release(); }
    }

    public async Task UpdateAsync(Action<LauncherState> update)
    {
        await gate.WaitAsync();
        try
        {
            var next = Clone(state);
            update(next);
            Directory.CreateDirectory(directory);
            var temporary = filePath + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, next, JsonOptions);
                    await stream.FlushAsync();
                }
                File.Move(temporary, filePath, true);
                state = next;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally { gate.Release(); }
    }

    private static LauncherState Clone(LauncherState value) =>
        JsonSerializer.Deserialize<LauncherState>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
