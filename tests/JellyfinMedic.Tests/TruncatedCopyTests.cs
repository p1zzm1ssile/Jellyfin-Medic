using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class TruncatedCopyTests
{
    // What FFprobe prints for "-show_entries packet=pts_time,duration_time -of csv=p=0" near the end of a 60 s film.
    private const string SoundEnd = "50.023000,0.100000\n50.000000,0.023000\n59.900000,0.100000\n60.000000,0.023000\n";

    // The same film cut in half: its header still says 60 s, but the packets stop near 30 s.
    private const string CutEnd = "24.961000,0.023000\n25.023000,0.100000\n29.814000,0.023000\n";

    private static TrackCleaner.ProbedFile Probed(int video, int audio, double? seconds, string? endProblem = null) =>
        new(Enumerable.Repeat((0, "video", "und"), video).Concat(Enumerable.Repeat((0, "audio", "eng"), audio)).ToList(), seconds, endProblem);

    [Fact]
    public void The_last_packet_ends_at_the_latest_timestamp_plus_its_duration_whatever_the_stream_order()
    {
        Assert.Equal(60.023, TrackCleaner.LastPacketEnd(SoundEnd)!.Value, 3);
        Assert.Equal(29.837, TrackCleaner.LastPacketEnd(CutEnd)!.Value, 3);
        Assert.Equal(60.1, TrackCleaner.LastPacketEnd("60.000000,0.100000\n59.950000,0.023000\n")!.Value, 3);
    }

    [Fact]
    public void Packets_without_a_timestamp_are_skipped_and_a_missing_duration_counts_as_none()
    {
        Assert.Equal(12.5, TrackCleaner.LastPacketEnd("N/A,0.100000\n12.500000,N/A\n\n")!.Value, 3);
        Assert.Null(TrackCleaner.LastPacketEnd("N/A,N/A\n"));
        Assert.Null(TrackCleaner.LastPacketEnd(string.Empty));
    }

    [Fact]
    public void A_copy_whose_packets_reach_its_stated_length_checks_out()
    {
        Assert.Null(TrackCleaner.EndProblem(SoundEnd, 0, 60.046));
        // Within the same slack as the length check: 2 s, or 1% of a long film.
        Assert.Null(TrackCleaner.EndProblem("58.100000,0.023000\n", 0, 60.046));
        Assert.Null(TrackCleaner.EndProblem("7100.000000,0.040000\n", 0, 7160));
        // Timestamps that start late, as in a transport stream, are measured from the start.
        Assert.Null(TrackCleaner.EndProblem("61.400000,0.040000\n", 1.4, 60.0));
    }

    [Fact]
    public void A_copy_whose_packets_stop_well_before_its_stated_length_is_refused()
    {
        Assert.Equal("it stops at 30 s though it says it lasts 60 s", TrackCleaner.EndProblem(CutEnd, 0, 60.046));
        Assert.Equal("it stops at 57 s though it says it lasts 60 s", TrackCleaner.EndProblem("57.000000,0.023000\n", 0, 60.046));
        Assert.Equal("it stops at 7000 s though it says it lasts 7160 s", TrackCleaner.EndProblem("7000.000000,0.040000\n", 0, 7160));
        Assert.Equal("it stops at 32 s though it says it lasts 60 s", TrackCleaner.EndProblem("62.000000,0.040000\n", 30, 60));
    }

    [Fact]
    public void A_copy_whose_end_cant_be_read_is_refused()
    {
        Assert.Equal("FFprobe couldn't read its end", TrackCleaner.EndProblem(string.Empty, 0, 60));
        Assert.Equal("FFprobe couldn't read its end", TrackCleaner.EndProblem(null, 0, 60));
    }

    [Fact]
    public void The_end_is_read_from_shortly_before_the_stated_end_to_past_it()
    {
        Assert.Equal("50.046%+70", TrackCleaner.EndInterval(0, 60.046));
        Assert.Equal("0%+70", TrackCleaner.EndInterval(0, 4));
        Assert.Equal("7151.4%+70", TrackCleaner.EndInterval(1.4, 7160));
    }

    [Fact]
    public void An_earlier_copy_cut_short_cannot_take_the_originals_place()
    {
        Assert.Equal("it stops at 30 s though it says it lasts 60 s",
            TrackCleaner.CheckLeftover(Probed(1, 2, 60), Probed(1, 1, 60, "it stops at 30 s though it says it lasts 60 s"), 1000, 900));
        Assert.Equal("FFprobe couldn't read its end", TrackCleaner.CheckLeftover(null, Probed(1, 1, 60, "FFprobe couldn't read its end"), 0, 900));
        // What the copy lacks is still named first.
        Assert.Equal("it has no audio", TrackCleaner.CheckLeftover(Probed(1, 2, 60), Probed(1, 0, 60, "FFprobe couldn't read its end"), 1000, 900));
    }

    [Fact]
    public void A_cleaned_copy_cut_short_cannot_replace_the_original()
    {
        Assert.Null(TrackCleaner.CheckOutput(Probed(1, 2, 60), Probed(1, 1, 60), 1, 0, 1000, 900));
        Assert.Equal("it stops at 30 s though it says it lasts 60 s",
            TrackCleaner.CheckOutput(Probed(1, 2, 60), Probed(1, 1, 60, "it stops at 30 s though it says it lasts 60 s"), 1, 0, 1000, 900));
        Assert.Equal("FFprobe couldn't read it", TrackCleaner.CheckOutput(Probed(1, 2, 60), null, 1, 0, 1000, 900));
    }
}
