using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class ServerLogFilesTests : IDisposable
{
    private const string Day = "2026-10-16";
    private readonly List<string> _folders = new();

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // A server log with a damaged-database error, and newer FFmpeg logs from streams still running,
    // as Jellyfin keeps them in the same folder.
    private string FolderWithBusyStreams()
    {
        string folder = FolderWithDamagedDatabase();
        foreach (var kind in new[] { "Transcode", "DirectStream", "Remux" })
        {
            File.WriteAllText(
                Path.Combine(folder, $"FFmpeg.{kind}-{Day}_10-06-00_abc_1234abcd.log"),
                "frame= 1200 fps= 48 q=-1.0 size= 20480kB time=00:00:50.00 bitrate=3355.4kbits/s speed=1.9x\n");
        }

        return folder;
    }

    private string FolderWithDamagedDatabase()
    {
        string folder = NewFolder();
        string serverLog = Path.Combine(folder, "log_20261016.log");
        File.WriteAllLines(serverLog, new[]
        {
            $"[{Day} 10:00:00.000 +00:00] [INF] [1] Main: Jellyfin version: 10.11.0",
            $"[{Day} 10:05:00.000 +00:00] [ERR] [12] Jellyfin.Server: SQLite Error 11: 'database disk image is malformed'."
        });
        File.SetLastWriteTimeUtc(serverLog, DateTime.UtcNow.AddMinutes(-1));
        return folder;
    }

    private string NewFolder()
    {
        string folder = Directory.CreateTempSubdirectory("medic-logs-").FullName;
        _folders.Add(folder);
        return folder;
    }

    [Fact]
    public void Only_the_server_logs_are_chosen()
    {
        string folder = FolderWithBusyStreams();

        var file = Assert.Single(LogScanner.ServerLogs(folder, DateTime.UtcNow.AddDays(-2), 3));

        Assert.Equal("log_20261016.log", file.Name);
    }

    [Fact]
    public void A_server_log_older_than_the_window_is_left_out()
    {
        string folder = FolderWithDamagedDatabase();
        File.SetLastWriteTimeUtc(Path.Combine(folder, "log_20261016.log"), DateTime.UtcNow.AddDays(-3));

        Assert.Empty(LogScanner.ServerLogs(folder, DateTime.UtcNow.AddDays(-2), 3));
        Assert.Empty(AdminAlerts.CriticalFromLogs(folder));
    }

    [Fact]
    public void Only_the_newest_server_logs_are_read_newest_first()
    {
        string folder = NewFolder();
        for (int day = 1; day <= 4; day++)
        {
            string log = Path.Combine(folder, $"log_2026101{day}.log");
            File.WriteAllText(log, "\n");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-10 + day));
        }

        var names = LogScanner.ServerLogs(folder, DateTime.UtcNow.AddDays(-2), 3).Select(f => f.Name);

        Assert.Equal(new[] { "log_20261014.log", "log_20261013.log", "log_20261012.log" }, names);
    }

    [Fact]
    public void The_banner_finds_a_damaged_database_while_streams_are_running()
    {
        var alert = Assert.Single(AdminAlerts.CriticalFromLogs(FolderWithBusyStreams()));

        Assert.Equal("critical:database:" + Day, alert.Id);
    }

    [Fact]
    public void Each_log_folder_gets_its_own_answer()
    {
        string damaged = FolderWithDamagedDatabase();
        string healthy = NewFolder();
        File.WriteAllText(Path.Combine(healthy, "log_20261016.log"), $"[{Day} 10:00:00.000 +00:00] [INF] [1] Main: Jellyfin version: 10.11.0\n");

        Assert.Single(AdminAlerts.CriticalFromLogs(damaged));
        Assert.Empty(AdminAlerts.CriticalFromLogs(healthy));
    }

    [Fact]
    public void A_folder_read_in_the_last_two_minutes_is_not_read_again()
    {
        string folder = FolderWithDamagedDatabase();
        Assert.Single(AdminAlerts.CriticalFromLogs(folder));

        File.Delete(Path.Combine(folder, "log_20261016.log"));

        Assert.Single(AdminAlerts.CriticalFromLogs(folder));
    }
}
