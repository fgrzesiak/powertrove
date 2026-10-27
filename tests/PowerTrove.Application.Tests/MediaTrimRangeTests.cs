using PowerTrove.Application.Media;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Application.Tests;

public sealed class MediaTrimRangeTests
{
    [Fact]
    public void SavedTimesSeedTheRangeAndSlidersCannotCrossOrEscapeTheSource()
    {
        var range = new MediaTrimRange(TimeSpan.FromSeconds(4), new Dictionary<string, string> { ["Start"] = "0:01.25", ["End"] = "3.5", ["Precise"] = "true" });
        Assert.Equal(TimeSpan.FromSeconds(1.25), range.Start); Assert.Equal(TimeSpan.FromSeconds(3.5), range.End);
        range.SetStart(9); Assert.Equal(MediaTrimRange.MinimumLength, range.Length);
        range.SetStart(-10); Assert.Equal(TimeSpan.Zero, range.Start);
        range.SetEnd(-1); Assert.Equal(MediaTrimRange.MinimumLength, range.Length);
        range.SetEnd(100); Assert.Equal(range.Duration, range.End);
        range.SetStart(1.125); range.SetEnd(2.875);
        Assert.Equal("1.125", range.Parameters().Values["Start"]); Assert.Equal("2.875", range.Parameters().Values["End"]);
        Assert.Equal("true", range.Parameters().Values["Precise"]);
    }
    [Theory]
    [InlineData("broken", "")][InlineData("3", "2")][InlineData("4", "")][InlineData("0", "9")]
    public void IncompatibleTypedTimesResetVisiblyToTheWholeSource(string start, string end)
    {
        var range = new MediaTrimRange(TimeSpan.FromSeconds(4), new Dictionary<string, string> { ["Start"] = start, ["End"] = end });
        Assert.True(range.InitialRangeAdjusted); Assert.Equal(TimeSpan.Zero, range.Start); Assert.Equal(range.Duration, range.End);
    }
    [Fact]
    public void PlaybackStartsInsideTheSelectionAndStopsAtItsEnd()
    {
        var range = new MediaTrimRange(TimeSpan.FromSeconds(4), new Dictionary<string, string>());
        range.SetStart(1); range.SetEnd(3); range.SetPosition(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(1), range.PreparePlayback());
        range.SetPosition(TimeSpan.FromSeconds(2)); Assert.Equal(TimeSpan.FromSeconds(2), range.PreparePlayback());
        Assert.False(range.ReachedEnd(TimeSpan.FromSeconds(2.999))); Assert.True(range.ReachedEnd(TimeSpan.FromSeconds(3)));
        range.SetPosition(TimeSpan.FromSeconds(3)); Assert.Equal(TimeSpan.FromSeconds(1), range.PreparePlayback());
        range.SetPosition(TimeSpan.FromDays(8)); Assert.Equal(range.Duration, range.Position);
    }
    [Fact]
    public void NonFiniteSliderValuesAndUnsupportedDurationsNeverBecomeActionParameters()
    {
        var range = new MediaTrimRange(TimeSpan.FromSeconds(4), new Dictionary<string, string>());
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) { range.SetStart(value); range.SetEnd(value); }
        Assert.Equal("0", range.Parameters().Values["Start"]); Assert.Equal("", range.Parameters().Values["End"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTrimRange(TimeSpan.Zero, new Dictionary<string, string>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTrimRange(TimeSpan.FromDays(8), new Dictionary<string, string>()));
    }
    [Fact]
    public void VisualEntryRequiresOneFreshAvailableAudioOrVideoFile()
    {
        var one = Snapshots.ForPaths(@"C:\synthetic\clip.mp4");
        Assert.True(MediaTrimRange.CanEdit(one));
        Assert.False(MediaTrimRange.CanEdit(one.Stale()));
        Assert.False(MediaTrimRange.CanEdit(Snapshots.ForPaths(@"C:\synthetic\one.mp4", @"C:\synthetic\two.mp4")));
        Assert.False(MediaTrimRange.CanEdit(Snapshots.ForPaths(@"C:\synthetic\captions.srt")));
        Assert.False(MediaTrimRange.CanEdit(null));
    }
}
