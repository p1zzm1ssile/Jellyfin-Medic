using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MedicPicks.Api;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;

namespace MedicPicks.Tests;

public sealed class DiscoverAccessTests
{
    private static readonly Guid Child = Guid.NewGuid();

    [Fact]
    public void An_account_with_parental_limits_gets_Discover_only_when_an_admin_ticked_it()
    {
        var config = new PluginConfiguration { EnableDiscover = true };
        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: true));
        Assert.True(DiscoverAccess.Allowed(config, Child, parentalLimits: false));

        config.DiscoverAllowedLimitedUserIds = new[] { Child.ToString("N") };
        Assert.True(DiscoverAccess.Allowed(config, Child, parentalLimits: true));

        config.EnableDiscover = false;
        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: true));
    }

    [Fact]
    public void An_unticked_account_gets_no_Discover_whatever_its_limits()
    {
        var config = new PluginConfiguration { EnableDiscover = true, DiscoverDisabledUserIds = new[] { Child.ToString() } };

        Assert.False(DiscoverAccess.Allowed(config, Child, parentalLimits: false));
    }

    [Fact]
    public void A_maximum_rating_counts_as_a_parental_limit()
    {
        Assert.False(DiscoverAccess.HasParentalLimits(new User("kid", "provider", "reset")));
        Assert.True(DiscoverAccess.HasParentalLimits(new User("kid", "provider", "reset") { MaxParentalRatingScore = 12 }));
    }

    [Theory]
    [InlineData(PreferenceKind.BlockedTags)]
    [InlineData(PreferenceKind.AllowedTags)]
    public void Tag_rules_count_as_parental_limits(PreferenceKind kind)
    {
        var tagged = new User("kid", "provider", "reset");
        tagged.SetPreference(kind, new[] { "horror" });
        Assert.True(DiscoverAccess.HasParentalLimits(tagged));

        var empty = new User("kid", "provider", "reset");
        empty.SetPreference(kind, new[] { string.Empty });
        Assert.False(DiscoverAccess.HasParentalLimits(empty));
    }

    // Older servers named the rating limit MaxParentalAgeRating; only properties are read by name.
    private sealed class LegacyUser
    {
        public int? MaxParentalAgeRating { get; set; }
    }

    [Fact]
    public void A_maximum_rating_under_its_older_name_counts_as_a_parental_limit()
    {
        Assert.False(DiscoverAccess.HasParentalLimits(new LegacyUser()));
        Assert.True(DiscoverAccess.HasParentalLimits(new LegacyUser { MaxParentalAgeRating = 12 }));
        Assert.False(DiscoverAccess.HasParentalLimits(null));
    }

    [Fact]
    public void A_persons_rebuilds_start_at_least_the_gap_apart()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, PicksController.WaitBeforeRebuild(null, now));
        Assert.Equal(PicksController.MinRebuildGap - TimeSpan.FromSeconds(5), PicksController.WaitBeforeRebuild(now.AddSeconds(-5), now));
        Assert.Equal(TimeSpan.Zero, PicksController.WaitBeforeRebuild(now - PicksController.MinRebuildGap, now));
    }

    [Fact]
    public void A_names_leading_words_are_every_shorter_run_of_words_from_its_start()
    {
        Assert.Equal(new[] { "seven", "seven deadly" }, PicksEngine.LeadingWords("seven deadly sins"));
        Assert.Empty(PicksEngine.LeadingWords("avatar"));
    }
}
