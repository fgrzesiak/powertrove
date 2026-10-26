using System.Buffers.Binary;
using System.Text;
using PowerTrove.Core.Activation;

namespace PowerTrove.Core.Tests;

public class ActivationProtocolTests
{
    [Fact]
    public async Task FramingRoundTripsFragmentedUnicodeSelection()
    {
        var message = new ExplicitSelectionMessage([@"C:\fixtures\Grüße one.pdf", @"D:\fixtures\two.png"], "contextbar.pdf.organize");
        using var wire = new MemoryStream();
        await ActivationProtocol.WriteAsync(wire, message, default);
        using var fragmented = new FragmentedStream(wire.ToArray());
        var decoded = Assert.IsType<ExplicitSelectionMessage>(await ActivationProtocol.ReadAsync(fragmented, default));
        Assert.Equal(message.FilePaths, decoded.FilePaths);
        Assert.Equal(message.ActionId, decoded.ActionId);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(65537)] [InlineData(int.MaxValue)]
    public async Task InvalidHeaderIsRejectedWithoutReadingPayload(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        Assert.Null(await ActivationProtocol.ReadAsync(stream, default));
        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("not json")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Type\":\"ActivatePalette\"}")]
    [InlineData("{\"Version\":2,\"Type\":\"ActivatePalette\"}")]
    [InlineData("{\"Version\":\"1\",\"Type\":\"ActivatePalette\"}")]
    [InlineData("{\"Version\":1,\"Type\":\"ActivatePalette\",\"FilePaths\":[]}")]
    [InlineData("{\"Version\":1,\"Type\":\"Execute\"}")]
    [InlineData("{\"Version\":1,\"Type\":\"ExplicitSelection\",\"FilePaths\":[]}")]
    public void MalformedDuplicateUnknownOrWrongVersionPayloadsFailClosed(string json) =>
        Assert.Null(ActivationProtocol.Decode(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData(@"\\server\share\one.pdf")] [InlineData(@"\\?\C:\one.pdf")]
    [InlineData(@"C:one.pdf")] [InlineData(@"C:\one.pdf:secret")]
    [InlineData(@"C:\a\..\one.pdf")] [InlineData(@"C:\a\one.pdf.")]
    [InlineData(@"C:/one.pdf")] [InlineData("https://example.test/a.pdf")]
    public void RemoteDeviceRelativeAndNonCanonicalPathsAreRejected(string path)
    {
        Assert.False(ActivationProtocol.IsLocalPath(path));
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(new ExplicitSelectionMessage([path])));
    }

    [Fact]
    public void LimitsApplyToBothDirectionsWithoutTruncatingTheSelection()
    {
        var tooMany = Enumerable.Repeat(@"C:\fixture.pdf", 101).ToArray();
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(new ExplicitSelectionMessage(tooMany)));
        string json = System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Type = "ExplicitSelection", FilePaths = tooMany });
        Assert.Null(ActivationProtocol.Decode(Encoding.UTF8.GetBytes(json)));
        var tooLarge = Enumerable.Repeat("C:\\" + new string('ü', 1000) + ".pdf", 100).ToArray();
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(new ExplicitSelectionMessage(tooLarge)));
        Assert.Null(ActivationProtocol.Decode(new byte[ActivationProtocol.MaxMessageBytes + 1]));
    }

    [Fact]
    public void MainArgumentVectorStartsAtZeroAndInvalidExplicitInputNeverBecomesGeneric()
    {
        Assert.IsType<ActivatePaletteMessage>(ActivationProtocol.ParseArguments([]));
        var selection = Assert.IsType<ExplicitSelectionMessage>(ActivationProtocol.ParseArguments(["--organize-pages", @"C:\one.pdf"]));
        Assert.Equal("contextbar.pdf.organize", selection.ActionId);
        Assert.Null(ActivationProtocol.ParseArguments(["--open-selection"]));
        Assert.Null(ActivationProtocol.ParseArguments(["--open-selection", @"\\server\share\one.pdf"]));
        Assert.Null(ActivationProtocol.ParseArguments(["--execute", "contextbar.pdf.organize"]));
    }

    [Fact]
    public async Task TruncatedAndCancelledReadsDoNotBecomeActivations()
    {
        using var truncated = new MemoryStream([10, 0, 0, 0, 123]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ActivationProtocol.ReadAsync(truncated, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var input = new MemoryStream(new byte[8]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ActivationProtocol.ReadAsync(input, cancelled.Token));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
