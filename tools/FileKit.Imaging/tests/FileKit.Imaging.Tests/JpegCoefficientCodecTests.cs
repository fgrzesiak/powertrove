using FileKit.Imaging.Jpeg;

namespace FileKit.Imaging.Tests;

public sealed class JpegCoefficientCodecTests
{
    private static byte[] Fixture() => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.jpg"));

    [Fact]
    public void BaselineDecodesAndReencodesIdenticalCoefficients()
    {
        var input = JpegCoefficientCodec.Decode(Fixture());
        Assert.NotEmpty(input.Components);
        var output = JpegCoefficientCodec.Decode(JpegCoefficientCodec.Encode(input));
        Assert.Equal((input.Width, input.Height), (output.Width, output.Height));
        Assert.Equal(input.Components.Count, output.Components.Count);
        for (int i = 0; i < input.Components.Count; i++)
            Assert.Equal(input.Components[i].Coefficients, output.Components[i].Coefficients);
    }

    [Fact]
    public void IdentityRoundTripRetainsAppAndCommentMarkers()
    {
        var input = JpegCoefficientCodec.Decode(Fixture());
        var output = JpegCoefficientCodec.Decode(
            JpegCoefficientCodec.Transform(Fixture(), JpegCoefficientTransform.Identity));
        Assert.Equal(input.Markers.Select(m => m.Marker), output.Markers.Select(m => m.Marker));
    }

    [Fact]
    public void FourQuarterTurnsRestoreCoefficientsOnCompleteMcus()
    {
        var original = JpegCoefficientCodec.Decode(Fixture());
        int h = original.Components.Max(c => c.HorizontalSampling);
        int v = original.Components.Max(c => c.VerticalSampling);
        if (original.Width % (h * 8) != 0 || original.Height % (v * 8) != 0)
        {
            Assert.Throws<JpegCoefficientException>(() => JpegCoefficientCodec.TransformCoefficients(
                original, JpegCoefficientTransform.Rotate90));
            return;
        }

        var current = original;
        for (int turn = 0; turn < 4; turn++)
            current = JpegCoefficientCodec.TransformCoefficients(
                current, JpegCoefficientTransform.Rotate90);
        for (int i = 0; i < original.Components.Count; i++)
            Assert.Equal(original.Components[i].Coefficients, current.Components[i].Coefficients);
    }

    [Fact]
    public void RestartMarkersPreserveCoefficientValues()
    {
        var original = JpegCoefficientCodec.Decode(Fixture());
        original.RestartInterval = 1;
        var output = JpegCoefficientCodec.Encode(original);
        var roundTrip = JpegCoefficientCodec.Decode(output);
        Assert.Equal(1, roundTrip.RestartInterval);
        for (int i = 0; i < original.Components.Count; i++)
            Assert.Equal(original.Components[i].Coefficients,
                roundTrip.Components[i].Coefficients);
    }

    [Fact]
    public void MalformedInputsAndCancellationAreRejected()
    {
        var data = Fixture();
        Assert.Throws<JpegCoefficientException>(() => JpegCoefficientCodec.Decode(data.AsMemory(0, 8)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            JpegCoefficientCodec.Decode(data, cancellation.Token));
    }
}
