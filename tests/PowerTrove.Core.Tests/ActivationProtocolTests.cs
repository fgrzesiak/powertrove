using System.Buffers.Binary;
using System.Text;
using PowerTrove.Core.Activation;

namespace PowerTrove.Core.Tests;

public class ActivationProtocolTests
{
    [Fact]
    public async Task FramingRoundTripsFragmentedUnicodeSelection()
    {
        var message = new ExplicitSelectionMessage([@"C:\fixtures\Grüße one.pdf", @"D:\fixtures\two.png"], "powertrove.pdf.organize");
        using var wire = new MemoryStream();
        await ActivationProtocol.WriteAsync(wire, message, default);
        using var fragmented = new FragmentedStream(wire.ToArray());
        var decoded = Assert.IsType<ExplicitSelectionMessage>(await ActivationProtocol.ReadAsync(fragmented, default));
        Assert.Equal(message.FilePaths, decoded.FilePaths);
        Assert.Equal(message.ActionId, decoded.ActionId);
    }

    [Fact]
    public void BrowserConnectLinksParseWithoutUserEnteredIds()
    {
        const string id = "abcdefghijklmnopabcdefghijklmnop";
        var pair = ActivationProtocol.ParseArguments(["--browser-connect",
            "powertrove://connect?browser=chrome&id=" + id]);
        var connect = Assert.IsType<BrowserPairingMessage>(pair);
        Assert.Equal("chrome", connect.Browser);
        Assert.Equal(id, connect.ExtensionId);
        Assert.Equal(connect, ActivationProtocol.Decode(ActivationProtocol.Encode(connect)));
        Assert.Equal("edge", Assert.IsType<BrowserPairingMessage>(
            ActivationProtocol.ParseBrowserConnectUri("powertrove://connect?browser=edge&id=" + id)).Browser);
    }

    [Theory]
    [InlineData("powertrove://connect?browser=firefox&id=abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("powertrove://connect?browser=chrome&id=not-an-id")]
    [InlineData("powertrove://connect?browser=chrome&id=abcdefghijklmnopabcdefghijklmnop&extra=true")]
    [InlineData("powertrove://connect?browser=chrome&id=abcdefghijklmnopabcdefghijklmnop&id=abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("http://connect?browser=chrome&id=abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("powertrove://wrong?browser=chrome&id=abcdefghijklmnopabcdefghijklmnop")]
    public void InvalidBrowserPairingLinksFailClosed(string link)
    {
        Assert.Null(ActivationProtocol.ParseBrowserConnectUri(link));
        Assert.Null(ActivationProtocol.ParseArguments(["--browser-connect", link]));
    }

    [Fact]
    public void ExplicitBrowserTableRoundTripsAsBoundedTypedCells()
    {
        var table = new BrowserTableMessage("https://example.org/data", "Public data",
            new List<IReadOnlyList<string>> { new[] { "Name", "Count" }, new[] { "A", "1" } });
        var parsed = Assert.IsType<BrowserTableMessage>(
            ActivationProtocol.Decode(ActivationProtocol.Encode(table)));
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("Name", parsed.Rows[0][0]);
        Assert.DoesNotContain("Public data",table.ToString());
    }

    [Fact]
    public void ExplicitBrowserTableRejectsOversizeAndInvalidUrls()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "private" } };
        Assert.False(ActivationProtocol.IsBrowserTable(new BrowserTableMessage("file:///private","Title",rows)));
        Assert.Throws<InvalidDataException>(()=>ActivationProtocol.Encode(
            new BrowserTableMessage("https://example.org", "Title",
                Enumerable.Range(0,101).Select(_=>(IReadOnlyList<string>)new[]{"x"}).ToArray())));
        Assert.Throws<InvalidDataException>(()=>ActivationProtocol.Encode(
            new BrowserTableMessage("https://example.org", "Title",
                new[] { (IReadOnlyList<string>)new [] { new string('x',513) } })));
    }

    [Fact]
    public async Task BrowserTextSelectionRoundTripsWithoutFilePaths()
    {
        var message = new BrowserSelectionMessage("Grüße 🌍", "https://example.org/page", "Sample title");
        using var wire = new MemoryStream();
        await ActivationProtocol.WriteAsync(wire, message, default);
        wire.Position = 0;
        var actual = Assert.IsType<BrowserSelectionMessage>(await ActivationProtocol.ReadAsync(wire, default));
        Assert.Equal(message, actual);
        Assert.False(Encoding.UTF8.GetString(ActivationProtocol.Encode(message)).Contains("FilePaths"));
    }

    [Theory]
    [InlineData("file:///secret")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@example.org/")]
    public void BrowserSelectionRejectsUnsafeUrls(string url)
    {
        var message = new BrowserSelectionMessage("hello", url, "Page");
        Assert.False(ActivationProtocol.IsBrowserSelection(message));
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(message));
    }

    [Fact]
    public void BrowserSelectionRejectsOversizeAndUnpairedSurrogate()
    {
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(
            new BrowserSelectionMessage(new string('x', ActivationProtocol.MaxBrowserTextCharacters + 1),
                "https://example.org", "title")));
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Encode(
            new BrowserSelectionMessage("\ud800", "https://example.org", "title")));
        Assert.Null(ActivationProtocol.Decode(Encoding.UTF8.GetBytes(
            "{\"Version\":1,\"Type\":\"BrowserSelection\",\"Text\":\"ok\",\"Url\":\"https://example.org\",\"Title\":\"x\",\"FilePaths\":[]}")));
        Assert.Null(ActivationProtocol.Decode(Encoding.UTF8.GetBytes(
            "{\"Version\":1,\"Type\":\"BrowserSelection\",\"Text\":\"one\",\"Text\":\"two\",\"Url\":\"https://example.org\",\"Title\":\"x\"}")));
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
        Assert.Equal("powertrove.pdf.organize", selection.ActionId);
        Assert.Null(ActivationProtocol.ParseArguments(["--open-selection"]));
        Assert.Null(ActivationProtocol.ParseArguments(["--open-selection", @"\\server\share\one.pdf"]));
        Assert.Null(ActivationProtocol.ParseArguments(["--execute", "powertrove.pdf.organize"]));
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
