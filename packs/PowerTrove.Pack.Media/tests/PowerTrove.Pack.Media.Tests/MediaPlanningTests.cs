namespace PowerTrove.Pack.Media.Tests;

public sealed class MediaPlanningTests
{
    [Theory]
    [InlineData("1.25", 1.25)][InlineData("01:05", 65)][InlineData("1:02:03.5", 3723.5)][InlineData("120:00", 7200)][InlineData(".5", .5)]
    public void TimeParserAcceptsSpecifiedForms(string text, double seconds)
    { Assert.True(MediaTime.TryParse(text, out var time)); Assert.Equal(seconds, time.TotalSeconds, 6); }
    [Theory]
    [InlineData("")][InlineData("-1")][InlineData("1,5")][InlineData("1:60")][InlineData("1:60:00")][InlineData("NaN")][InlineData("Infinity")][InlineData("99999999999999999999999999")][InlineData("1::2")][InlineData("1e2")]
    public void TimeParserRejectsInvalidValues(string text) => Assert.False(MediaTime.TryParse(text, out _));
    [Theory]
    [InlineData(3840, 2160, "medium", 1280, 720)]
    [InlineData(2160, 3840, "medium", 720, 1280)]
    [InlineData(640, 360, "high", 640, 360)]
    [InlineData(1920, 1080, "small", 852, 480)]
    public void CompressionIsEvenAspectPreservingAndNeverUpscales(int w, int h, string preset, int expectedW, int expectedH)
    {
        var size = MediaPlanning.CompressionSize(w, h, preset);
        Assert.Equal((expectedW, expectedH), (size.Width, size.Height)); Assert.True(size.Width <= w && size.Height <= h);
        Assert.Equal(0, size.Width % 2); Assert.Equal(0, size.Height % 2); Assert.InRange(Math.Abs((double)size.Width / size.Height - (double)w / h), 0, .01);
    }
    [Fact]
    public void GifTimesClampToDurationAndRefuseOver600Frames()
    {
        var times = MediaPlanning.GifTimes(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 12, TimeSpan.FromSeconds(2));
        Assert.Equal(12, times.Count); Assert.Equal(TimeSpan.FromSeconds(1), times[0]); Assert.All(times, t => Assert.True(t < TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPlanning.GifTimes(TimeSpan.Zero, TimeSpan.FromSeconds(30), 30, TimeSpan.FromSeconds(60)));
        Assert.Equal(100, Enumerable.Range(0, 12).Sum(i => MediaPlanning.GifDelay(i, 12)));
    }
    [Fact]
    public void FramePlanningCaps500AndUsesDistinctTimestampNames()
    {
        var times = MediaPlanning.FrameTimes(TimeSpan.FromHours(2), false, TimeSpan.Zero, TimeSpan.FromSeconds(.5));
        Assert.Equal(500, times.Count); Assert.Equal(TimeSpan.FromSeconds(249.5), times[^1]);
        Assert.Equal("name - 01m05s.jpg", MediaPlanning.FrameName("name", TimeSpan.FromSeconds(65), "jpg"));
        Assert.Equal("name - 00m00s500ms.png", MediaPlanning.FrameName("name", TimeSpan.FromSeconds(.5), "png"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPlanning.FrameTimes(TimeSpan.FromSeconds(2), true, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }
    [Fact]
    public void ProbeIgnoresTagsAndBoundsTechnicalValues()
    {
        const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":640,"height":360,"avg_frame_rate":"30000/1001","tags":{"title":"private-title"}},{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","tags":{"artist":"private-artist"}}],"format":{"duration":"65.25","bit_rate":"123456","tags":{"comment":"private-comment"}}}""";
        var info = FFmpegMediaInspector.Parse(json); Assert.Equal(65.25, info.Duration.TotalSeconds); Assert.Equal(2, info.Channels); Assert.InRange(info.FrameRate!.Value, 29.97, 29.98);
        string formatted = MediaTechnical.Format(info); Assert.Contains("h264", formatted); Assert.Contains("aac", formatted); Assert.DoesNotContain("private", formatted);
        Assert.Throws<FileProcessingException>(() => FFmpegMediaInspector.Parse(json.Replace("640", "999999")));
        Assert.Throws<FileProcessingException>(() => FFmpegMediaInspector.Parse(json.Replace("65.25", "NaN")));
    }
}
