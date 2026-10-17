using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MedicPicks.Configuration;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Who gets Discover picks. Discover suggests titles from TMDb, which parental limits can't filter, so an account
/// with a rating limit or tag rules only gets it when an admin has ticked it for Discover since.
/// </summary>
public static class DiscoverAccess
{
    /// <summary>Discover is on for the server, the person isn't unticked, and an account with parental limits was ticked.</summary>
    public static bool Allowed(PluginConfiguration config, Guid userId, bool parentalLimits) =>
        config.EnableDiscover
        && !Listed(config.DiscoverDisabledUserIds, userId)
        && (!parentalLimits || Listed(config.DiscoverAllowedLimitedUserIds, userId));

    private static bool Listed(string[]? ids, Guid userId) =>
        (ids ?? Array.Empty<string>()).Any(id => Guid.TryParse(id, out var g) && g == userId);

    /// <summary>
    /// Whether a Jellyfin user has a maximum rating, blocked tags or allowed tags. The rating is read by name, as its
    /// property was renamed between Jellyfin versions; false when none can be read.
    /// </summary>
    public static bool HasParentalLimits(object? user)
    {
        if (user is null)
        {
            return false;
        }

        var type = user.GetType();
        foreach (string name in new[] { "MaxParentalRatingScore", "MaxParentalAgeRating" })
        {
            if (type.GetProperty(name)?.GetValue(user) is not null)
            {
                return true;
            }
        }

        // GetPreference is an extension method, so it's called on the User type rather than found on the instance.
        return user is User jellyfinUser
            && (HasTags(jellyfinUser, PreferenceKind.BlockedTags) || HasTags(jellyfinUser, PreferenceKind.AllowedTags));
    }

    private static bool HasTags(User user, PreferenceKind kind) => user.GetPreference(kind).Length > 0;
}
