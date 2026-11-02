using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class PluginDirectoryTests
{
    private static readonly DateTime At = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_fetched_list_is_kept_for_6_hours()
    {
        var list = new PluginDirectory();

        Assert.True(PluginDirectoryService.Fresh(At, list, At.AddHours(5)));
        Assert.False(PluginDirectoryService.Fresh(At, list, At.AddHours(6)));
    }

    [Fact]
    public void A_failed_fetch_is_tried_again_after_2_minutes()
    {
        var failed = new PluginDirectory { Error = "Couldn't reach it." };

        Assert.True(PluginDirectoryService.Fresh(At, failed, At.AddMinutes(1)));
        Assert.False(PluginDirectoryService.Fresh(At, failed, At.AddMinutes(2)));
    }
}
