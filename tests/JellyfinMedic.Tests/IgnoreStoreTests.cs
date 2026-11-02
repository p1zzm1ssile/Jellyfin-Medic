using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class IgnoreStoreTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void An_ignored_finding_stays_ignored_when_the_count_in_its_title_changes()
    {
        var before = new Finding { Area = "Server", Severity = Sev.Tip, Title = "Jellyfin ran flat out 3 times this week", Id = "server.flat-out" };
        IgnoreStore.Apply(new[] { before }, IgnoreStore.Load(_paths));
        IgnoreStore.Set(_paths, before.Key, ignored: true);

        var after = new Finding { Area = "Server", Severity = Sev.Tip, Title = "Jellyfin ran flat out 4 times this week", Id = "server.flat-out" };
        IgnoreStore.Apply(new[] { after }, IgnoreStore.Load(_paths));

        Assert.True(after.Ignored);
        Assert.Equal(before.Key, after.Key);
    }

    [Fact]
    public void A_finding_without_an_id_keeps_its_title_key()
    {
        var finding = new Finding { Area = "Libraries", Severity = Sev.Tip, Title = "Movies: real-time monitoring is on" };
        Assert.Equal("Libraries|Movies: real-time monitoring is on", IgnoreStore.KeyFor(finding));
    }

    [Fact]
    public void Showing_a_finding_again_removes_its_key()
    {
        var finding = new Finding { Area = "Plugins", Severity = Sev.Tip, Title = "2 plugin updates are available", Id = "plugins.updates" };
        string key = IgnoreStore.KeyFor(finding);
        IgnoreStore.Set(_paths, key, ignored: true);
        IgnoreStore.Set(_paths, key, ignored: false);

        IgnoreStore.Apply(new[] { finding }, IgnoreStore.Load(_paths));
        Assert.False(finding.Ignored);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
