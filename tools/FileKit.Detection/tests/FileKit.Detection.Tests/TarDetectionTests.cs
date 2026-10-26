namespace FileKit.Detection.Tests;

public sealed class TarDetectionTests
{
    [Fact]
    public void UstarMagicAndGzipHaveDistinctDetectedFormats()
    {
        byte[] header = new byte[512]; "ustar"u8.CopyTo(header.AsSpan(257));
        Assert.Equal(FileFormat.Tar, FileSignature.Detect(header).Format);
        Assert.Equal(FileFormat.Gzip, FileSignature.Detect(new byte[] { 0x1F, 0x8B, 8 }).Format);
        Assert.Equal(FileFormat.Unknown, FileSignature.Detect(header.AsSpan(0, 261)).Format);
    }
}
