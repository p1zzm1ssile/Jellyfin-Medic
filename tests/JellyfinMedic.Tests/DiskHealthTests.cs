using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class DiskHealthTests : IDisposable
{
    private readonly string _ini = Path.GetTempFileName();

    [Fact]
    public void An_NVMe_drive_at_60_degrees_is_fine_but_a_spinning_disk_at_56_is_hot()
    {
        Write(Disk("disk1", rotational: "1", temp: 56), Disk("cache", rotational: "0", temp: 60));

        var finding = Assert.Single(DiskHealth.CheckFile(_ini));
        Assert.Equal("A disk is running hot", finding.Title);
        Assert.Equal("disk1: 56°C", finding.Current);
    }

    [Fact]
    public void An_NVMe_drive_over_its_own_limit_is_hot()
    {
        Write(Disk("cache", rotational: "0", temp: 72));

        Assert.Equal("cache: 72°C", Assert.Single(DiskHealth.CheckFile(_ini)).Current);
    }

    [Fact]
    public void Errors_are_reported_as_failing_and_heat_on_its_own()
    {
        Write(Disk("disk2", rotational: "1", temp: 58, errors: 3));

        var findings = DiskHealth.CheckFile(_ini);
        Assert.Contains(findings, f => f.Title == "A disk may be failing" && f.Current == "disk2: 3 read/write errors");
        Assert.Contains(findings, f => f.Title == "A disk is running hot" && f.Current == "disk2: 58°C");
    }

    [Fact]
    public void A_disabled_or_missing_disk_with_no_device_present_is_reported()
    {
        Write(Disk("disk1", rotational: "1", temp: 0, status: "DISK_NP_DSBL"), Disk("disk2", rotational: "1", temp: 0, status: "DISK_NP_MISSING"));

        var finding = Assert.Single(DiskHealth.CheckFile(_ini));
        Assert.Equal("2 disks need attention", finding.Title);
        Assert.Equal("disk1: NP_DSBL; disk2: NP_MISSING", finding.Current);
    }

    [Fact]
    public void A_new_disk_and_an_empty_slot_are_not_reported()
    {
        Write(Disk("disk3", rotational: "1", temp: 30, status: "DISK_NEW"), Disk("disk4", rotational: "1", temp: 0, status: "DISK_NP"));

        Assert.Empty(DiskHealth.CheckFile(_ini));
    }

    [Fact]
    public void A_disabled_disk_with_a_new_one_in_its_place_is_still_reported()
    {
        // The array stays degraded until the new disk is rebuilt.
        Write(Disk("disk1", rotational: "1", temp: 30, status: "DISK_DSBL_NEW"));

        Assert.Equal("disk1: DSBL_NEW", Assert.Single(DiskHealth.CheckFile(_ini)).Current);
    }

    public void Dispose() => File.Delete(_ini);

    private static string Disk(string name, string rotational, int temp, int errors = 0, string status = "DISK_OK") =>
        $"[\"{name}\"]\nname=\"{name}\"\nrotational=\"{rotational}\"\ntemp=\"{temp}\"\nnumErrors=\"{errors}\"\nstatus=\"{status}\"\n";

    private void Write(params string[] disks) => File.WriteAllText(_ini, string.Concat(disks));
}
