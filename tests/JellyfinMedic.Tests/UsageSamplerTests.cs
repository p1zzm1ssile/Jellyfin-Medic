using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class UsageSamplerTests
{
    [Fact]
    public void A_paused_session_is_left_out_and_only_converted_video_counts_as_a_transcode()
    {
        var playing = new[]
        {
            Transcode("Video", videoDirect: false),
            Transcode("Video", videoDirect: true),
            Transcode("Audio", videoDirect: false),
            Transcode("Video", videoDirect: false, paused: true)
        };

        Assert.Equal((3, 1), UsageSampler.Count(playing));
    }

    [Fact]
    public void A_transcode_counts_before_ffmpeg_first_reports_on_it()
    {
        Assert.Equal((1, 1), UsageSampler.Count(new[] { Transcode("Video", videoDirect: null) }));
    }

    // SettingsReader reads sessions by property name, so a plain object stands in for Jellyfin's. TranscodingInfo
    // is null until FFmpeg first reports progress, and music reports its video as not copied.
    private static object Transcode(string mediaType, bool? videoDirect, bool paused = false) => new
    {
        NowPlayingItem = new { Name = "Something", MediaType = mediaType },
        PlayState = new { PlayMethod = "Transcode", IsPaused = paused },
        TranscodingInfo = videoDirect is { } direct ? new { IsVideoDirect = direct } : null
    };
}
