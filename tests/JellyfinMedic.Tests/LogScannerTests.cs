using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class LogScannerTests
{
    private static Finding Repeated(string source, string message, int count) =>
        Assert.Single(LogScanner.Findings(new List<RepeatedError> { new() { Source = source, Message = message, Count = count } }));

    [Fact]
    public void A_repeated_error_keeps_its_key_when_its_count_changes()
    {
        var before = Repeated("Emby.Server.Implementations.Session", "Connection refused", 150);
        var after = Repeated("Emby.Server.Implementations.Session", "Connection refused", 4200);

        Assert.NotEqual(before.Title, after.Title);
        Assert.Equal(IgnoreStore.KeyFor(before), IgnoreStore.KeyFor(after));
    }

    [Fact]
    public void Two_errors_with_the_same_count_have_different_keys()
    {
        var first = Repeated("Emby.Server.Implementations.Session", "Connection refused", 150);
        var second = Repeated("Jellyfin.Api.Middleware", "The operation has timed out", 150);

        Assert.NotEqual(IgnoreStore.KeyFor(first), IgnoreStore.KeyFor(second));
    }

    [Fact]
    public void A_long_message_still_gives_a_key_short_enough_to_ignore()
    {
        // The Ignore endpoint refuses keys over 500 characters.
        var finding = Repeated("Emby.Server.Implementations.Session", new string('x', 2000), 150);

        Assert.True(IgnoreStore.KeyFor(finding).Length <= 500);
    }
}
