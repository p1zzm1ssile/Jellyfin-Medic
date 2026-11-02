using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class ResourceMonitorTests
{
    private static readonly DateTime Noon = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_cpu_time_of_an_ffmpeg_that_has_finished_stays_in_the_sum()
    {
        var jellyfin = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 1000))!.Value;
        var ffmpeg = ResourceMonitor.ParseStat(Stat(50, "ffmpeg", 1, utime: 3000))!.Value;
        // FFmpeg used one more second and exited; Jellyfin reaped it, which moves all of its time to Jellyfin's cutime.
        var jellyfinLater = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 1100, cutime: 3100))!.Value;

        double before = ResourceMonitor.TreeCpuSeconds(jellyfin, new[] { ffmpeg });
        double after = ResourceMonitor.TreeCpuSeconds(jellyfinLater, Array.Empty<ResourceMonitor.ProcessStat>());

        Assert.Equal(40, before, 3);
        Assert.Equal(42, after, 3);
    }

    [Fact]
    public void Each_process_counts_its_own_time_and_what_its_finished_children_used()
    {
        var jellyfin = ResourceMonitor.ParseStat(Stat(1, "jellyfin", 0, utime: 300, stime: 100, cutime: 200, cstime: 100))!.Value;
        var ffmpeg = ResourceMonitor.ParseStat(Stat(50, "ffmpeg", 1, utime: 40, stime: 10, cutime: 30, cstime: 20))!.Value;

        Assert.Equal(8, ResourceMonitor.TreeCpuSeconds(jellyfin, new[] { ffmpeg }), 3);
    }

    [Fact]
    public void A_spike_still_going_when_the_cooldown_ends_is_recorded_then()
    {
        Assert.False(ResourceMonitor.ShouldRecord(4, Noon.AddMinutes(-5), Noon));
        Assert.True(ResourceMonitor.ShouldRecord(4, Noon.AddMinutes(-16), Noon));
    }

    [Fact]
    public void A_spike_is_recorded_once_it_has_lasted_three_readings()
    {
        Assert.False(ResourceMonitor.ShouldRecord(2, null, Noon));
        Assert.True(ResourceMonitor.ShouldRecord(3, null, Noon));
    }

    // A /proc/<pid>/stat line as Linux writes it, up to rsslim. Times are in ticks, 100 a second.
    private static string Stat(int pid, string name, int parent, int utime, int stime = 0, int cutime = 0, int cstime = 0) =>
        $"{pid} ({name}) S {parent} {pid} {pid} 0 -1 4194560 12000 300 0 0 {utime} {stime} {cutime} {cstime} 20 0 12 0 130221903 3221225472 51200 18446744073709551615";
}
