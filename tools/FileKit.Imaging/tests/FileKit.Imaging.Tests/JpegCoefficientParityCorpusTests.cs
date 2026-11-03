using System.Text.Json;
using FileKit.Imaging.Jpeg;

namespace FileKit.Imaging.Tests;

/// <summary>
/// Opt-in bit-for-bit coefficient parity against JSON baselines exported by
/// the previous BitMiracle.LibJpeg.NET implementation in a separate checkout.
/// </summary>
public sealed class JpegCoefficientParityCorpusTests
{
    private sealed record Baseline(int Width, int Height, Component[] Components);
    private sealed record Component(
        int Id, int HorizontalSampling, int VerticalSampling,
        int BlockWidth, int BlockHeight, short[] Coefficients);

    [Fact]
    public void LocalReferenceCorpusMatchesAllQuantizedCoefficients()
    {
        string? root = Environment.GetEnvironmentVariable("POWERTROVE_JPEG_COEFFICIENT_CORPUS");
        if (string.IsNullOrWhiteSpace(root)) return;
        Assert.True(Directory.Exists(root), "JPEG coefficient corpus path is missing.");

        var files = Directory.GetFiles(root, "*.jpg")
            .Concat(Directory.GetFiles(root, "*.jpeg")).Order().ToArray();
        Assert.True(files.Length >= 4, "At least four baseline/progressive, varied-sampling JPEG fixtures are required.");

        foreach (string file in files)
        {
            var parsed = JpegCoefficientCodec.Decode(File.ReadAllBytes(file));
            Verify(parsed, Reference(file + ".libjpeg.json"));

            int maxH = parsed.Components.Max(c => c.HorizontalSampling);
            int maxV = parsed.Components.Max(c => c.VerticalSampling);
            if (parsed.Width % (8 * maxH) != 0 || parsed.Height % (8 * maxV) != 0)
            {
                Assert.Throws<JpegCoefficientException>(() =>
                    JpegCoefficientCodec.TransformCoefficients(parsed, JpegCoefficientTransform.Rotate90));
                continue;
            }

            foreach (var operation in Enum.GetValues<JpegCoefficientTransform>())
            {
                string baseline = file + "." + operation + ".libjpeg.json";
                Assert.True(File.Exists(baseline), $"Missing independent LibJpeg coefficient reference: {baseline}");
                var transformed = JpegCoefficientCodec.Transform(
                    File.ReadAllBytes(file), operation);
                Verify(JpegCoefficientCodec.Decode(transformed), Reference(baseline));
            }
        }
    }

    private static Baseline Reference(string path)
    {
        Assert.True(File.Exists(path), "Missing independent baseline: " + path);
        return JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid reference coefficient JSON: " + path);
    }

    private static void Verify(JpegCoefficientImage actual, Baseline expected)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Components.Length, actual.Components.Count);
        for (int i = 0; i < expected.Components.Length; i++)
        {
            var a = actual.Components[i];
            var e = expected.Components[i];
            Assert.Equal((e.Id, e.HorizontalSampling, e.VerticalSampling,
                e.BlockWidth, e.BlockHeight),
                (a.Id, a.HorizontalSampling, a.VerticalSampling, a.BlockWidth, a.BlockHeight));
            Assert.Equal(e.Coefficients, a.Coefficients);
        }
    }
}
