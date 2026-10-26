using System.Buffers.Binary;
using FileKit.Imaging.Png;
using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Tests;

public class ImageMetadataTests
{
    [Fact]
    public void Jpeg_ReportsPresenceAndDpiWithoutReturningExifValues()
    {
        using var jpeg = new MemoryStream(); jpeg.Write(new byte[] { 255, 216 });
        Segment(jpeg, 0xE0, new byte[] { 74, 70, 73, 70, 0, 1, 2, 1, 0, 96, 0, 96, 0, 0 });
        var tiff = new byte[32]; "II*\0"u8.CopyTo(tiff); BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1); BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x8825);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 4); BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1); BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18), 26);
        Segment(jpeg, 0xE1, "Exif\0\0"u8.ToArray().Concat(tiff).ToArray()); Segment(jpeg, 0xE2, "ICC_PROFILE\0secret"u8.ToArray());
        jpeg.Write(new byte[] { 255, 218 });
        var flags = ImageProbe.ProbeMetadataFlags(jpeg.ToArray());
        Assert.True(flags.HasLocation); Assert.True(flags.HasColorProfile); Assert.Equal(96d, flags.DpiX); Assert.Equal(1, flags.FrameCount);
        Assert.DoesNotContain("secret", flags.ToString());
    }
    [Fact]
    public void Png_RecognizesIccAndAlpha()
    {
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(2, 2, new(1, 2, 3, 128)), png);
        var bytes = png.ToArray(); var profile = new byte[16]; BinaryPrimitives.WriteInt32BigEndian(profile, 4); "iCCP"u8.CopyTo(profile.AsSpan(4));
        var withProfile = bytes[..33].Concat(profile).Concat(bytes[33..]).ToArray();
        var flags = ImageProbe.ProbeMetadataFlags(withProfile);
        Assert.True(flags.HasColorProfile); Assert.False(flags.HasLocation); Assert.True(flags.HasAlpha); Assert.Equal(1, flags.FrameCount);
    }
    [Fact]
    public void Gif_CountsFramesByStructureInsteadOfSearchingCompressedBytes()
    {
        using var gif = new MemoryStream(); gif.Write("GIF89a"u8); gif.Write(new byte[] { 1, 0, 1, 0, 128, 0, 0, 0, 0, 0, 255, 255, 255 });
        gif.Write(new byte[] { 0x21, 0xF9, 4, 1, 5, 0, 0, 0 });
        for (int i = 0; i < 2; i++) gif.Write(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0 });
        gif.WriteByte(0x3B); var flags = ImageProbe.ProbeMetadataFlags(gif.ToArray());
        Assert.Equal(2, flags.FrameCount); Assert.True(flags.HasAlpha); Assert.False(flags.HasLocation);
    }
    [Fact]
    public void Webp_CountsAnmfAndFlagsIcc()
    {
        using var payload = new MemoryStream(); payload.Write("WEBP"u8);
        WebpChunk(payload, "VP8X", new byte[] { 16, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        WebpChunk(payload, "ICCP", new byte[2]); WebpChunk(payload, "ANIM", new byte[6]);
        WebpChunk(payload, "ANMF", new byte[16]); WebpChunk(payload, "ANMF", new byte[16]);
        using var webp = new MemoryStream(); webp.Write("RIFF"u8); using (var writer = new BinaryWriter(webp, System.Text.Encoding.UTF8, true)) writer.Write((uint)payload.Length); webp.Write(payload.ToArray());
        var flags = ImageProbe.ProbeMetadataFlags(webp.ToArray()); Assert.Equal(2, flags.FrameCount); Assert.True(flags.HasColorProfile); Assert.True(flags.HasAlpha);
    }
    [Fact]
    public void MalformedChunkSizesAndTruncations_ThrowTypedErrors()
    {
        using var png = new MemoryStream(); PngEncoder.Encode(RasterOperations.Canvas(1, 1, default), png); var data = png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), uint.MaxValue); Assert.Throws<ImageProbeException>(() => ImageProbe.ProbeMetadataFlags(data));
        var random = new Random(29);
        for (int i = 0; i < 100; i++)
        {
            var mutation = png.ToArray(); random.NextBytes(mutation.AsSpan(8, 4));
            try { ImageProbe.ProbeMetadataFlags(mutation); }
            catch (ImageProbeException) { }
        }
        Assert.Throws<ImageProbeException>(() => ImageProbe.ProbeMetadataFlags("GIF89a"u8));
    }
    [Fact]
    public async Task MetadataScan_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageProbe.ProbeMetadataFlagsAsync(new MemoryStream(new byte[12]), cts.Token));
    }
    private static void Segment(Stream stream, byte marker, byte[] data)
    {
        stream.WriteByte(255); stream.WriteByte(marker); Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(data.Length + 2)); stream.Write(length); stream.Write(data);
    }
    private static void WebpChunk(Stream stream, string type, byte[] data)
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type)); using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true); writer.Write((uint)data.Length); writer.Write(data); if (data.Length % 2 != 0) writer.Write((byte)0);
    }
}
