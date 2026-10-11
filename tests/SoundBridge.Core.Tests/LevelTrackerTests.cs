using SoundBridge.Core.Audio;

namespace SoundBridge.Core.Tests;

public sealed class LevelTrackerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    [Fact]
    public void Starts_silent()
    {
        var tracker = new LevelTracker();

        Assert.False(tracker.IsPlaying);
        Assert.Equal(0f, tracker.Level);
    }

    [Fact]
    public void Sound_counts_as_playing_at_once()
    {
        var tracker = new LevelTracker();

        Assert.True(tracker.Push(0.02f, Frame));

        Assert.True(tracker.IsPlaying);
        Assert.InRange(tracker.Level, 0.3f, 0.6f);
    }

    [Fact]
    public void A_silent_stream_is_never_playing()
    {
        var tracker = new LevelTracker();

        for (var i = 0; i < 200; i++)
        {
            Assert.False(tracker.Push(0f, Frame));
        }

        Assert.False(tracker.IsPlaying);
        Assert.Equal(0f, tracker.Level);
    }

    [Fact]
    public void A_gap_between_tracks_does_not_end_playing()
    {
        var tracker = new LevelTracker();
        tracker.Push(0.05f, Frame);

        Assert.Equal(0, Feed(tracker, 0f, TimeSpan.FromSeconds(3.5)));
        Assert.True(tracker.IsPlaying);

        tracker.Push(0.05f, Frame);
        Assert.Equal(0, Feed(tracker, 0f, TimeSpan.FromSeconds(3.5)));
        Assert.True(tracker.IsPlaying);
    }

    [Fact]
    public void Lasting_silence_ends_playing_and_reports_the_change_once()
    {
        var tracker = new LevelTracker();
        tracker.Push(0.05f, Frame);

        var changes = Feed(tracker, 0f, TimeSpan.FromSeconds(6));

        Assert.False(tracker.IsPlaying);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Level_rises_at_once_and_falls_gradually()
    {
        var tracker = new LevelTracker();
        tracker.Push(1f, Frame);
        Assert.Equal(1f, tracker.Level);

        tracker.Push(0f, TimeSpan.FromMilliseconds(100));
        Assert.InRange(tracker.Level, 0.7f, 0.85f);

        Feed(tracker, 0f, TimeSpan.FromSeconds(1));
        Assert.Equal(0f, tracker.Level);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.0001f, 0f)]
    [InlineData(0.001f, 0f)]
    [InlineData(0.0316f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(4f, 1f)]
    public void Peaks_are_mapped_over_a_sixty_decibel_range(float peak, float expected)
    {
        Assert.Equal(expected, LevelTracker.Scale(peak), 2);
    }

    [Fact]
    public void Not_a_number_is_treated_as_silence()
    {
        var tracker = new LevelTracker();

        tracker.Push(float.NaN, Frame);

        Assert.False(tracker.IsPlaying);
        Assert.Equal(0f, tracker.Level);
    }

    [Fact]
    public void History_holds_recent_levels_oldest_first()
    {
        var tracker = new LevelTracker();
        Feed(tracker, 0.01f, TimeSpan.FromSeconds(1));
        Feed(tracker, 1f, TimeSpan.FromMilliseconds(200));

        var history = new float[LevelTracker.HistoryLength];
        tracker.CopyHistory(history);

        Assert.Equal(1f, history[^1]);
        Assert.InRange(history[^8], 0.3f, 0.4f);
        Assert.Equal(0f, history[0]);
    }

    [Fact]
    public void A_shorter_buffer_gets_the_newest_entries()
    {
        var tracker = new LevelTracker();
        Feed(tracker, 0.01f, TimeSpan.FromSeconds(1));
        Feed(tracker, 1f, TimeSpan.FromMilliseconds(200));

        var recent = new float[3];
        tracker.CopyHistory(recent);

        Assert.All(recent, value => Assert.Equal(1f, value));
    }

    [Fact]
    public void Step_progress_runs_from_zero_to_one_within_a_history_slot()
    {
        var tracker = new LevelTracker();

        tracker.Push(0.5f, TimeSpan.FromMilliseconds(20));
        Assert.InRange(tracker.StepProgress, 0.35f, 0.45f);

        tracker.Push(0.5f, TimeSpan.FromMilliseconds(40));
        Assert.InRange(tracker.StepProgress, 0.15f, 0.25f);
    }

    [Fact]
    public void Reset_forgets_everything_and_reports_whether_playing_ended()
    {
        var tracker = new LevelTracker();
        Feed(tracker, 0.5f, TimeSpan.FromSeconds(1));

        Assert.True(tracker.Reset());

        Assert.False(tracker.IsPlaying);
        Assert.Equal(0f, tracker.Level);
        var history = new float[LevelTracker.HistoryLength];
        tracker.CopyHistory(history);
        Assert.All(history, value => Assert.Equal(0f, value));
        Assert.False(tracker.Reset());
    }

    /// <summary>Feeds the same peak frame by frame for <paramref name="duration"/> and counts playing changes.</summary>
    private static int Feed(LevelTracker tracker, float peak, TimeSpan duration)
    {
        var changes = 0;
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Frame)
        {
            if (tracker.Push(peak, Frame))
            {
                changes++;
            }
        }

        return changes;
    }
}
