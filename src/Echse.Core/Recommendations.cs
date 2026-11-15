namespace Echse.Core;

public static class Recommendations
{
    public static IReadOnlyList<Game> ForUser(IEnumerable<Game> catalog, LauncherState state, int count = 8)
    {
        var games = catalog.ToList();
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games.Concat(state.Library.Select(entry => entry.Game)).DistinctBy(game => game.Id))
        {
            var entry = state.Library.FirstOrDefault(item => item.Game.Id == game.Id);
            state.Views.TryGetValue(game.Id, out var views);
            var interest = Math.Log(1 + Math.Max(0, views)) +
                (entry is null ? 0 : 2 + Math.Log(1 + Math.Max(0, entry.PlaySeconds) / 3600) +
                 (entry.Favorite ? 4 : 0));
            foreach (var tag in Features(game))
                weights[tag] = weights.GetValueOrDefault(tag) + interest;
        }
        var owned = state.Library.Select(entry => entry.Game.Id).ToHashSet(StringComparer.Ordinal);
        return games.Where(game => !owned.Contains(game.Id))
            .OrderByDescending(game => Features(game).Sum(tag => weights.GetValueOrDefault(tag)))
            .ThenByDescending(game => game.Popularity)
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(count).ToList();
    }

    private static IEnumerable<string> Features(Game game) =>
        game.Tags.Append(game.Genre).Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
