using System.Text;

namespace FileKit.Text.Tests;

public sealed class SubtitleTests
{
    private const string Srt = "1\r\n00:00:01,250 --> 00:00:03,500\r\nGrüße 👋\r\nSecond line\r\n\r\n2\r\n00:01:00,000 --> 00:01:01,010\r\n<i>Text</i>\r\n";
    [Fact]
    public void SrtVttRoundTripRetainsMillisecondsUnicodeAndLineBreaks()
    {
        var document = Subtitles.Parse(Srt, SubtitleFormat.Srt);
        string vtt = Subtitles.Write(document, SubtitleFormat.Vtt, true);
        Assert.StartsWith("WEBVTT\r\n\r\n", vtt); Assert.Contains("00:00:01.250 --> 00:00:03.500", vtt);
        var back = Subtitles.Parse(vtt, SubtitleFormat.Vtt);
        Assert.Equal(document.Cues, back.Cues);
        Assert.Equal(Srt + "\r\n", Subtitles.Write(back, SubtitleFormat.Srt, true));
    }
    [Theory]
    [InlineData(1000, 2250)][InlineData(-250, 1000)]
    public void OffsetAppliesToBothEndsWithoutChangingText(int milliseconds, int expected)
    {
        var before = Subtitles.Parse(Srt, SubtitleFormat.Srt);
        var after = Subtitles.Shift(before, TimeSpan.FromMilliseconds(milliseconds));
        Assert.Equal(TimeSpan.FromMilliseconds(expected), after.Cues[0].Start);
        Assert.Equal(before.Cues[0].End + TimeSpan.FromMilliseconds(milliseconds), after.Cues[0].End);
        Assert.Equal(before.Cues[0].Text, after.Cues[0].Text); Assert.Equal(TimeSpan.FromMilliseconds(1250), before.Cues[0].Start);
    }
    [Fact]
    public void ShiftingBeforeZeroOrBeyondBoundsFailsInsteadOfDroppingCues()
    {
        var document = Subtitles.Parse(Srt, SubtitleFormat.Srt);
        Assert.Throws<SubtitleException>(() => Subtitles.Shift(document, TimeSpan.FromSeconds(-2)));
        Assert.Throws<SubtitleException>(() => Subtitles.Shift(document, TimeSpan.MaxValue));
    }
    [Fact]
    public void VttSettingsAndIdentifiersSurviveShiftButCannotBeSilentlyLostOnSrtConversion()
    {
        var document = Subtitles.Parse("WEBVTT\n\nspeaker-one\n00:01.000 --> 00:02.000 align:start\nHello\n", SubtitleFormat.Vtt);
        var shifted = Subtitles.Write(Subtitles.Shift(document, TimeSpan.FromSeconds(1)), SubtitleFormat.Vtt);
        Assert.Contains("speaker-one\r\n00:00:02.000 --> 00:00:03.000 align:start", shifted);
        Assert.Throws<SubtitleException>(() => Subtitles.Write(document, SubtitleFormat.Srt, true));
    }
    [Theory]
    [InlineData("00:00:01,000 --> 00:00:01,000")][InlineData("00:60:01,000 --> 00:60:02,000")]
    [InlineData("00:00:01.000 --> 00:00:02.000")][InlineData("-1:00:01,000 --> 00:00:02,000")]
    [InlineData("00:00:01,0 --> 00:00:02,000")]
    public void InvalidTimestampsAreRejected(string timing)
    { Assert.Throws<SubtitleException>(() => Subtitles.Parse("1\n" + timing + "\nText\n", SubtitleFormat.Srt)); }
    [Theory]
    [InlineData("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0\n\n")][InlineData("WEBVTT\n\nSTYLE\n::cue { color:red }\n")]
    [InlineData("WEBVTT\n\nNOTE Private note\ntext\n")][InlineData("WEBVTT\n\nREGION\nid:r1\n")]
    public void UnsupportedVttMetadataIsRejected(string text)
    { Assert.Throws<SubtitleException>(() => Subtitles.Parse(text, SubtitleFormat.Vtt)); }
    [Theory]
    [InlineData("align:wrong")][InlineData("size:101%")][InlineData("position:NaN%")][InlineData("region:missing")][InlineData("align:start align:end")]
    public void MalformedOrUnsupportedCueSettingsAreRejected(string settings)
    { Assert.Throws<SubtitleException>(() => Subtitles.Parse("WEBVTT\n\n00:01.000 --> 00:02.000 " + settings + "\nText\n", SubtitleFormat.Vtt)); }
    [Fact]
    public async Task InvalidUtf8LimitsCancellationAndUnsupportedInlineMarkupAreRejected()
    {
        using var invalid = new MemoryStream([0xff, 0xfe]);
        await Assert.ThrowsAsync<SubtitleException>(() => Subtitles.ReadAsync(invalid, SubtitleFormat.Srt));
        Assert.Throws<SubtitleException>(() => Subtitles.Parse(new string('a', Subtitles.MaxBytes + 1), SubtitleFormat.Srt));
        Assert.Throws<OperationCanceledException>(() => Subtitles.Parse(Srt, SubtitleFormat.Srt, new(true)));
        var inline = Subtitles.Parse("WEBVTT\n\n00:01.000 --> 00:03.000\n<00:02.000>text\n", SubtitleFormat.Vtt);
        Assert.Throws<SubtitleException>(() => Subtitles.Shift(inline, TimeSpan.FromSeconds(1)));
        Assert.Throws<SubtitleException>(() => Subtitles.Write(inline, SubtitleFormat.Srt, true));
    }
}
