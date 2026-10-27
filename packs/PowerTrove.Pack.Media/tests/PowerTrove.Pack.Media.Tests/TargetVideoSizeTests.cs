namespace PowerTrove.Pack.Media.Tests;

public sealed class TargetVideoSizeTests
{
    [Fact]
    public void ByteBudgetReservesAudioAndContainerOverheadAndRespectsQualityCeiling()
    {
        Assert.Equal(122666, MediaPlanning.TargetVideoBitrate(TimeSpan.FromSeconds(60), 2000000, true, 2500000));
        Assert.Equal(250666, MediaPlanning.TargetVideoBitrate(TimeSpan.FromSeconds(60), 2000000, false, 2500000));
        Assert.Equal(1000000, MediaPlanning.TargetVideoBitrate(TimeSpan.FromSeconds(1), 2000000, true, 1000000));
    }
    [Fact]
    public void InvalidDurationOrUnreachableBudgetFailsExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPlanning.TargetVideoBitrate(TimeSpan.Zero, 2000000, true, 2500000));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPlanning.TargetVideoBitrate(TimeSpan.FromHours(1), 1000000, true, 2500000));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPlanning.TargetVideoBitrate(TimeSpan.FromSeconds(60), long.MaxValue, false, 2500000));
    }
}
