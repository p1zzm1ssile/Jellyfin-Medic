using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace MedicPicks.Tests;

public sealed class PicksEngineTests
{
    private static readonly DateTime Earlier = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_episode_with_no_series_is_left_out_and_the_other_titles_stay()
    {
        // A video at the root of a TV library is an episode of no series.
        var film = new Movie { Id = Guid.NewGuid(), Name = "Heat" };
        var loose = new Episode { Id = Guid.NewGuid(), Name = "Loose Episode S01E01", SeriesId = Guid.Empty };
        var show = new Series { Id = Guid.NewGuid(), Name = "Severance" };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Good News About Hell", SeriesId = show.Id };

        var titles = PicksEngine.RollUpWatched(new BaseItem[] { film, loose, episode }, Library(show), _ => null);

        Assert.Equal(new[] { film.Id, show.Id }.Order(), titles.Keys.Order());
    }

    [Fact]
    public void Episodes_count_towards_their_series_which_keeps_the_latest_play()
    {
        var show = new Series { Id = Guid.NewGuid(), Name = "Severance" };
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = show.Id };
        var second = new Episode { Id = Guid.NewGuid(), SeriesId = show.Id };
        var played = new Dictionary<Guid, DateTime?> { [first.Id] = Later, [second.Id] = Earlier };

        var titles = PicksEngine.RollUpWatched(new BaseItem[] { first, second }, Library(show), item => played[item.Id]);

        var title = Assert.Single(titles.Values);
        Assert.Same(show, title.Item);
        Assert.Equal(2, title.Count);
        Assert.Equal(Later, title.LastPlayed);
    }

    // Finds a series as Jellyfin 12.2's library does, which refuses an empty id.
    private static Func<Guid, BaseItem?> Library(params Series[] series) => id =>
        id == Guid.Empty
            ? throw new ArgumentException("Guid can't be empty", nameof(id))
            : series.FirstOrDefault(s => s.Id == id);
}
