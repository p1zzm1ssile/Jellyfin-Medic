using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class HostPlatformTests
{
    private const string CgroupV2 = "0::/";
    private const string ServiceAccountToken = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    private readonly Dictionary<string, string[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    [Fact]
    public void A_pod_under_cgroup_v2_with_no_marker_files_is_Kubernetes()
    {
        File("/proc/1/cgroup", CgroupV2);
        _env["KUBERNETES_SERVICE_HOST"] = "10.96.0.1";

        Assert.Equal(HostKind.Kubernetes, Detect());
    }

    [Fact]
    public void A_pod_is_Kubernetes_from_its_service_account_token_alone()
    {
        File("/proc/1/cgroup", CgroupV2);
        File(ServiceAccountToken, "eyJhbGciOi");

        Assert.Equal(HostKind.Kubernetes, Detect());
    }

    [Fact]
    public void A_pod_under_cgroup_v1_is_Kubernetes_from_its_kubepods_cgroup()
    {
        File("/proc/1/cgroup", "12:memory:/kubepods/burstable/pod1234/abcd", "1:name=systemd:/kubepods/burstable/pod1234/abcd");

        Assert.Equal(HostKind.Kubernetes, Detect());
    }

    [Fact]
    public void A_pod_on_the_Docker_runtime_is_still_Kubernetes()
    {
        File("/.dockerenv");
        File("/proc/1/cgroup", "12:memory:/kubepods/besteffort/pod1234/docker-abcd.scope");
        _env["KUBERNETES_SERVICE_HOST"] = "10.96.0.1";

        Assert.Equal(HostKind.Kubernetes, Detect());
    }

    [Fact]
    public void A_TrueNAS_app_on_its_older_Kubernetes_stays_TrueNAS()
    {
        File("/proc/mounts", "boot-pool/ix-applications/releases/jellyfin/volumes /config zfs rw 0 0");
        _env["KUBERNETES_SERVICE_HOST"] = "172.17.0.1";

        Assert.Equal(HostKind.TrueNas, Detect());
    }

    [Fact]
    public void An_empty_Kubernetes_variable_is_not_a_pod()
    {
        File("/proc/1/cgroup", CgroupV2);
        _env["KUBERNETES_SERVICE_HOST"] = "  ";

        Assert.Equal(HostKind.Linux, Detect());
    }

    [Fact]
    public void A_containerd_container_without_Kubernetes_stays_plain_Linux()
    {
        File("/proc/1/cgroup", CgroupV2);

        Assert.Equal(HostKind.Linux, Detect());
    }

    [Fact]
    public void Docker_and_Podman_marker_files_are_Docker()
    {
        File("/.dockerenv");
        Assert.Equal(HostKind.Docker, Detect());

        _files.Clear();
        File("/run/.containerenv");
        Assert.Equal(HostKind.Docker, Detect());
    }

    [Fact]
    public void A_Proxmox_LXC_is_recognised_from_systemd()
    {
        File("/run/systemd/container", "lxc");

        Assert.Equal(HostKind.ProxmoxLxc, Detect());
    }

    [Fact]
    public void Kubernetes_counts_as_a_container_but_not_a_Docker_one()
    {
        Assert.True(HostPlatform.IsContainerKind(HostKind.Kubernetes));
        Assert.False(HostPlatform.IsDockerLikeKind(HostKind.Kubernetes));
        Assert.False(HostPlatform.IsContainerKind(HostKind.Linux));
    }

    [Theory]
    [InlineData(HostKind.Docker, true)]
    [InlineData(HostKind.Unraid, true)]
    [InlineData(HostKind.TrueNas, true)]
    [InlineData(HostKind.Kubernetes, true)]
    [InlineData(HostKind.ProxmoxLxc, false)] // an LXC keeps the hostname it's given
    [InlineData(HostKind.Linux, false)]
    public void Only_hosts_that_name_themselves_at_random_get_the_server_name_tip(HostKind kind, bool random)
    {
        Assert.Equal(random, HostPlatform.HostnameIsRandomKind(kind));
    }

    private void File(string path, params string[] lines) => _files[path] = lines;

    private HostKind Detect() => HostPlatform.DetectLinux(
        path => _files.TryGetValue(path, out var lines) ? lines : Array.Empty<string>(),
        _files.ContainsKey,
        name => _env.TryGetValue(name, out var value) ? value : null);
}
