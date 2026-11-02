using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class SystemProbeTests
{
    private const string UsbLink = "../devices/pci0000:00/0000:00:14.0/usb2/2-1/2-1:1.0/host6/target6:0:0/6:0:0:0/block/sdb";
    private const string SataLink = "../devices/pci0000:00/0000:00:17.0/ata3/host2/target2:0:0/2:0:0:0/block/sdb";

    private readonly Dictionary<string, string[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

    [Fact]
    public void In_a_container_the_driver_in_proc_alone_is_not_an_NVIDIA_GPU()
    {
        Assert.False(SystemProbe.NvidiaPresent(Array.Empty<string>(), procGpus: true, inContainer: true));
    }

    [Fact]
    public void In_a_container_given_only_its_second_GPU_NVIDIA_is_present()
    {
        Assert.True(SystemProbe.NvidiaPresent(new[] { "nvidiactl", "nvidia1", "nvidia-uvm" }, procGpus: true, inContainer: true));
        Assert.True(SystemProbe.NvidiaPresent(new[] { "nvidia1" }, procGpus: false, inContainer: true));
    }

    [Fact]
    public void In_a_container_the_control_node_alone_counts()
    {
        Assert.True(SystemProbe.NvidiaPresent(new[] { "nvidiactl" }, procGpus: false, inContainer: true));
    }

    [Fact]
    public void In_a_container_other_NVIDIA_nodes_are_not_a_GPU()
    {
        Assert.False(SystemProbe.NvidiaPresent(new[] { "nvidia-uvm", "nvidia-modeset", "nvidia-caps" }, procGpus: false, inContainer: true));
    }

    [Fact]
    public void Outside_a_container_the_driver_in_proc_is_enough()
    {
        Assert.True(SystemProbe.NvidiaPresent(Array.Empty<string>(), procGpus: true, inContainer: false));
        Assert.False(SystemProbe.NvidiaPresent(Array.Empty<string>(), procGpus: false, inContainer: false));
    }

    [Fact]
    public void A_USB_disk_that_says_it_spins_is_unknown()
    {
        Rotational("sdb", "1");
        _links["/sys/block/sdb"] = UsbLink;

        Assert.Null(Spinning("/dev/sdb1"));
    }

    [Fact]
    public void A_USB_disk_that_says_it_does_not_spin_is_an_SSD()
    {
        Rotational("sdb", "0");
        _links["/sys/block/sdb"] = UsbLink;

        Assert.False(Spinning("/dev/sdb1"));
    }

    [Fact]
    public void A_SATA_disk_that_says_it_spins_is_a_hard_drive()
    {
        Rotational("sdb", "1");
        _links["/sys/block/sdb"] = SataLink;

        Assert.True(Spinning("/dev/sdb1"));
    }

    [Fact]
    public void A_virtual_disk_is_unknown()
    {
        Rotational("vda", "1");

        Assert.Null(Spinning("/dev/vda1"));
    }

    private void Rotational(string disk, string value) => _files[$"/sys/block/{disk}/queue/rotational"] = new[] { value };

    private bool? Spinning(string device) => SystemProbe.Spinning(
        device,
        path => _files.TryGetValue(path, out var lines) ? lines : Array.Empty<string>(),
        path => _links.TryGetValue(path, out var target) ? target : null);
}
