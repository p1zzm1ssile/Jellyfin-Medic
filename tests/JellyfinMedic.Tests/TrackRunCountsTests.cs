using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

// Reasons are passed by name: they are internal, and a test's parameters must be public.
public sealed class TrackRunCountsTests
{
    [Theory]
    [InlineData(nameof(TrackCleaner.Unchanged.StalePlan))]
    [InlineData(nameof(TrackCleaner.Unchanged.Linked))]
    [InlineData(nameof(TrackCleaner.Unchanged.NoSpace))]
    [InlineData(nameof(TrackCleaner.Unchanged.ReplacedMeanwhile))]
    public void A_file_Medic_chose_not_to_touch_is_left_alone(string reason)
    {
        var progress = new TrackRunProgress();

        Assert.False(TrackCleaner.Tally(progress, Enum.Parse<TrackCleaner.Unchanged>(reason), 700));

        Assert.Equal((1, 0, 1, 700L), (progress.Skipped, progress.Failed, progress.Done, progress.BytesDone));
    }

    [Theory]
    [InlineData(nameof(TrackCleaner.Unchanged.Unreadable))]
    [InlineData(nameof(TrackCleaner.Unchanged.BadCopy))]
    public void A_file_Medic_tried_and_couldnt_clean_failed(string reason)
    {
        var progress = new TrackRunProgress();

        Assert.True(TrackCleaner.Tally(progress, Enum.Parse<TrackCleaner.Unchanged>(reason), 700));

        Assert.Equal((0, 1, 1, 700L), (progress.Skipped, progress.Failed, progress.Done, progress.BytesDone));
    }

    [Fact]
    public void Every_reason_is_sorted()
    {
        // A reason added later must be placed on purpose, so each one is covered by a test above.
        Assert.Equal(6, Enum.GetValues<TrackCleaner.Unchanged>().Length);
    }
}
