using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.Core.Tests.Extensibility;

public sealed class ImagePreviewProtocolTests
{
    [Fact]
    public void ExplicitRenderingRequestSurvivesTheWorkerBoundaryWithoutMutatingSavedOptions()
    {
        var options = new Dictionary<string, string> { ["Opacity"] = "50" };
        var request = ImagePreviewRequest.Create(options);
        Assert.False(ImagePreviewRequest.IsRequested(new DictionaryParameters(options)));
        Assert.True(ImagePreviewRequest.IsRequested(request));
        var selection = new FileSelectionPayload([new ProtocolFile(@"C:\inputs\generated.png", "generated.png", ".png", false, 100)]);
        var message = new PreviewMessage(Guid.NewGuid(), "powertrove.image.watermark-text", selection, request.Values, DateTimeOffset.UtcNow.AddMinutes(1));
        var restored = Assert.IsType<PreviewMessage>(PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(message)));
        Assert.True(ImagePreviewRequest.IsRequested(new DictionaryParameters(restored.Parameters)));
        Assert.Equal("50", restored.Parameters["Opacity"]); Assert.Single(options);
    }

    [Fact]
    public void PreviewSurvivesTheWorkerBoundaryWithoutChangingLegacyConstructors()
    {
        var frame = new ImagePreviewData(1280, 640, 1, 1, Convert.ToBase64String(Png())) { OriginalWidth = 200, OriginalHeight = 100 };
        var preview = new ActionPreview(true, "Summary", [], []) { ImagePreview = frame };
        var wire = PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Guid.NewGuid(), preview));
        var restored = Assert.IsType<PreviewResultMessage>(PackMessageSerializer.DeserializeLine(wire)).ToPreview();
        Assert.Equal(frame, restored.ImagePreview); Assert.True(restored.ImagePreview!.TryDecodePng(out var bytes)); Assert.Equal(Png(), bytes);
        Assert.DoesNotContain(frame.PngBase64, restored.ToString());
        Assert.NotNull(typeof(ActionPreview).GetConstructor([typeof(bool), typeof(string), typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<string>), typeof(bool), typeof(IReadOnlyList<RenamePreviewRow>), typeof(ActionConfirmation), typeof(IReadOnlyList<ActionParameterChoiceSet>)]));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(0, 1)]
    [InlineData(30001, 1)]
    [InlineData(30000, 30000)]
    public void InvalidOriginalDimensionsCannotReachTheResizeView(int? width, int? height)
    {
        var frame = new ImagePreviewData(1, 1, 1, 1, Convert.ToBase64String(Png())) { OriginalWidth = width, OriginalHeight = height };
        Assert.False(frame.TryDecodePng(out _));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(30000, 30000, 1, 1)]
    [InlineData(1, 1, 513, 1)]
    [InlineData(1, 1, 2, 1)]
    public void InvalidOrMismatchedDimensionsAreRejectedBeforeTheUiDecoder(int sw, int sh, int pw, int ph)
    {
        var preview = new ActionPreview(true, "Summary", [], []) { ImagePreview = new(sw, sh, pw, ph, Convert.ToBase64String(Png())) };
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Guid.NewGuid(), preview)));
    }

    [Fact]
    public void MalformedOrOversizedPayloadsAreRejectedAndErrorsContainNoImageData()
    {
        foreach (var text in new[] { "not an image", new string('A', ImagePreviewData.MaximumEncodedLength + 1) })
        {
            var preview = new ActionPreview(true, "Summary", [], []) { ImagePreview = new(1, 1, 1, 1, text) };
            Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Guid.NewGuid(), preview)));
        }
    }

    private static byte[] Png()
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 1); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, true)) deflate.Write(new byte[] { 0,255,0,0,255 });
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []); return output.ToArray();
    }
    private static void Chunk(Stream output, string name, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        byte[] type = Encoding.ASCII.GetBytes(name); output.Write(type); output.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in type.Concat(data)) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
}
