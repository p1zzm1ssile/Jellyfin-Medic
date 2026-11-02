using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class StorageWatchTests
{
    [Fact]
    public void Two_drives_holding_the_same_library_keep_different_names()
    {
        var first = new DriveStatus { Key = "/mnt/disk1", MountPoint = "/mnt/disk1", Holds = "Films" };
        var second = new DriveStatus { Key = "/mnt/disk2", MountPoint = "/mnt/disk2", Holds = "Films" };

        Assert.NotEqual(first.StableName, second.StableName);
    }

    [Fact]
    public void A_drive_without_a_mount_point_is_named_by_what_it_holds()
    {
        // Without a mount point, Key is built from the size and free space, which change from day to day.
        var drive = new DriveStatus { Key = "931.5/120.3", Holds = "Films" };

        Assert.Equal("Films", drive.StableName);
    }

    [Fact]
    public void A_drive_found_from_a_folder_carries_its_mount_point()
    {
        var drive = Assert.Single(StorageWatch.Drives(new[] { ("Films", Path.GetTempPath()) }));

        Assert.False(string.IsNullOrEmpty(drive.MountPoint));
        Assert.Equal(drive.MountPoint, drive.StableName);
    }
}
