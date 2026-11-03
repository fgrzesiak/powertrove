using System.Text;
using System.Text.Json;
using PowerTrove.Core.Browser;

namespace PowerTrove.Core.Tests.Browser;

public sealed class BrowserWireProtocolTests
{
    private static readonly Guid Id = Guid.Parse("f1e841c0-01f0-4ad0-8052-10a222619d1f");

    [Fact]
    public void GeneratedFixturesAreValidatedByTheCoreCodec()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "BrowserProtocol", "frames.json");
        using var fixtures = JsonDocument.Parse(File.ReadAllText(file));
        var root = fixtures.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        foreach (var fixture in root.GetProperty("valid").EnumerateArray())
        {
            byte[] raw = Encoding.UTF8.GetBytes(fixture.GetString()!);
            var frame = BrowserWireProtocol.Decode(raw);
            Assert.NotNull(frame);
            var encoded = BrowserWireProtocol.Encode(frame);
            Assert.NotNull(BrowserWireProtocol.Decode(encoded));
        }
        foreach (var fixture in root.GetProperty("invalid").EnumerateArray())
            Assert.Null(BrowserWireProtocol.Decode(Encoding.UTF8.GetBytes(fixture.GetString()!)));
    }

    [Fact]
    public void HandshakeChoosesOnlySupportedProtocolVersions()
    {
        Assert.Equal(1, BrowserWireProtocol.Negotiate(1, 1));
        Assert.Equal(1, BrowserWireProtocol.Negotiate(1, 2));
        Assert.Null(BrowserWireProtocol.Negotiate(2, 3));
        Assert.Null(BrowserWireProtocol.Negotiate(0, 1));
        Assert.Null(BrowserWireProtocol.Negotiate(3, 2));
    }

    [Fact]
    public void DuplicateFieldsAndOversizedMessagesFailClosed()
    {
        string duplicate = "{\"type\":\"cancel\",\"id\":\"f1e841c0-01f0-4ad0-8052-10a222619d1f\",\"id\":\"f1e841c0-01f0-4ad0-8052-10a222619d1f\"}";
        Assert.Null(BrowserWireProtocol.Decode(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Null(BrowserWireProtocol.Decode(new byte[BrowserWireProtocol.MaximumMessageBytes + 1]));
        Assert.Null(BrowserWireProtocol.Decode(Encoding.UTF8.GetBytes("{\"type\":\"cancel\",\"id\":\"invalid\"}")));
    }

    [Fact]
    public void DiagnosticOutputDoesNotExposePageContent()
    {
        var frame = new BrowserWireProtocol.Request(Id, "getContext", 350, WindowTitle: "PRIVATE-TEST-TITLE");
        Assert.DoesNotContain("PRIVATE-TEST-TITLE", frame.ToString());
        Assert.Contains("Redacted", frame.ToString());
        var result = JsonDocument.Parse("{\"selectedText\":\"PRIVATE-TEST-SELECTION\"}").RootElement.Clone();
        var response = new BrowserWireProtocol.Response(Id, true, result);
        Assert.DoesNotContain("PRIVATE-TEST-SELECTION", response.ToString());
    }

    [Fact]
    public void OrderedChunksAssembleAndRejectInterleaving()
    {
        using var buffer = new BrowserChunkBuffer(Id, "text");
        buffer.Append(new BrowserWireProtocol.Chunk(Id, 0, false, "text", Convert.ToBase64String("Hello"u8)));
        Assert.False(buffer.IsComplete);
        Assert.Throws<InvalidDataException>(() => buffer.Append(new BrowserWireProtocol.Chunk(Id, 2, true, "text", "IQ==")));
        buffer.Append(new BrowserWireProtocol.Chunk(Id, 1, true, "text", "IQ=="));
        Assert.True(buffer.IsComplete);
        Assert.Equal("Hello!", Encoding.UTF8.GetString(buffer.Complete()));
        Assert.Throws<InvalidDataException>(() => buffer.Append(new BrowserWireProtocol.Chunk(Id, 2, true, "text", "IQ==")));
    }

    [Fact]
    public void TextChunkAssemblyEnforcesTotalSizeLimit()
    {
        using var buffer = new BrowserChunkBuffer(Id, "text");
        string chunk = Convert.ToBase64String(new byte[BrowserWireProtocol.MaximumChunkBytes]);
        for (int i = 0; i < 4; i++)
            buffer.Append(new BrowserWireProtocol.Chunk(Id, i, false, "text", chunk));
        Assert.Throws<InvalidDataException>(() =>
            buffer.Append(new BrowserWireProtocol.Chunk(Id, 4, true, "text", "AQ==")));
    }

    [Fact]
    public void CancelledTransferDoesNotProducePartialResult()
    {
        using var buffer = new BrowserChunkBuffer(Id, "text");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            buffer.Append(new BrowserWireProtocol.Chunk(Id, 0, true, "text", "IQ=="), cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => buffer.Complete());
    }
}
