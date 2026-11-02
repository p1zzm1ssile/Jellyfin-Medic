using JellyfinMedic.Services;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace JellyfinMedic.Tests;

public sealed class TranscodingChecksTests
{
    [Theory]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.amf)]
    [InlineData(HardwareAccelerationType.videotoolbox)]
    public void Windows_and_macOS_get_the_checks_for_the_acceleration_method_they_use(HardwareAccelerationType method)
    {
        var titles = Titles(new EncodingOptions { HardwareAccelerationType = method }, isLinux: false);

        Assert.Equal(new[] { "HEVC isn't decoded on the GPU", "Transcodes are only made as H264", "Tone mapping is off" }, titles);
    }

    [Fact]
    public void Windows_and_macOS_with_acceleration_off_keep_their_findings()
    {
        var titles = Titles(new EncodingOptions(), isLinux: false);

        Assert.Equal(new[] { "Hardware acceleration is off", "Trickplay reads every frame" }, titles);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.qsv, true, false, false)]
    [InlineData(HardwareAccelerationType.vaapi, true, false, false)]
    [InlineData(HardwareAccelerationType.videotoolbox, false, true, false)]
    [InlineData(HardwareAccelerationType.nvenc, true, false, true)]
    [InlineData(HardwareAccelerationType.qsv, false, true, true)]
    public void Tone_mapping_through_VPP_counts_for_QSV_and_VAAPI_and_through_VideoToolbox_for_VideoToolbox(
        HardwareAccelerationType method, bool vpp, bool videoToolbox, bool tipExpected)
    {
        var encoding = new EncodingOptions
        {
            HardwareAccelerationType = method,
            EnableTonemapping = false,
            EnableVppTonemapping = vpp,
            EnableVideoToolboxTonemapping = videoToolbox
        };

        Assert.Equal(tipExpected, Titles(encoding, isLinux: false).Contains("Tone mapping is off"));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.none, "intel", new[] { "Your GPU isn't being used", "Trickplay reads every frame" })]
    [InlineData(HardwareAccelerationType.none, "none", new[] { "No GPU available to Jellyfin", "Trickplay reads every frame" })]
    [InlineData(HardwareAccelerationType.nvenc, "none", new[] { "Hardware acceleration is on, but Jellyfin can't see the GPU", "Tone mapping is off" })]
    [InlineData(HardwareAccelerationType.nvenc, "nvidia", new[] { "HEVC isn't decoded on the GPU", "Transcodes are only made as H264", "Tone mapping is off" })]
    [InlineData(HardwareAccelerationType.qsv, "amd", new[] { "Hardware acceleration type doesn't match your GPU", "HEVC isn't decoded on the GPU", "Transcodes are only made as H264", "Tone mapping is off" })]
    [InlineData(HardwareAccelerationType.qsv, "intel", new[] { "HEVC isn't decoded on the GPU", "Transcodes are only made as H264", "Intel low-power encoding is off", "Tone mapping is off" })]
    public void A_Linux_server_keeps_its_findings(HardwareAccelerationType method, string gpu, string[] expected)
    {
        var titles = Titles(new EncodingOptions { HardwareAccelerationType = method }, isLinux: true, Gpu(gpu));

        Assert.Equal(expected, titles);
    }

    [Fact]
    public void Two_formats_off_the_GPU_are_named_together()
    {
        var titles = Titles(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc, HardwareDecodingCodecs = new[] { "vc1" } }, isLinux: false);

        Assert.Contains("H264 and HEVC aren't decoded on the GPU", titles);
    }

    [Theory]
    [InlineData("HEVC isn't decoded on the GPU")]
    [InlineData("H264 and HEVC aren't decoded on the GPU")]
    public void The_decoding_finding_links_to_its_setting_however_many_formats_it_names(string title)
    {
        var finding = new JellyfinMedic.Api.Finding { Title = title };

        SettingLinks.Attach(new[] { finding });

        Assert.Equal("Transcoding|HardwareDecodingCodecs", finding.Setting);
    }

    [Fact]
    public void VPP_tone_mapping_doesnt_count_on_an_AMD_GPU()
    {
        // Jellyfin only uses VPP on its Intel paths, so on AMD with VAAPI the switch does nothing.
        var encoding = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableTonemapping = false, EnableVppTonemapping = true };

        Assert.Contains("Tone mapping is off", Titles(encoding, isLinux: true, Gpu("amd")));
    }

    private static List<string> Titles(EncodingOptions encoding, bool isLinux, HardwareInfo? hw = null) =>
        DiagnosticsEngine.AccelerationChecks(hw ?? new HardwareInfo(), encoding, new ServerConfiguration(), isLinux, isContainer: false)
            .Select(f => f.Title)
            .ToList();

    // What SystemProbe reports on Linux for a GPU it can see: a render node, or the NVIDIA device.
    private static HardwareInfo Gpu(string vendor) => new()
    {
        GpuVendor = vendor,
        RenderNodes = vendor is "intel" or "amd" ? new List<string> { "/dev/dri/renderD128" } : new List<string>(),
        NvidiaDevice = vendor == "nvidia"
    };
}
