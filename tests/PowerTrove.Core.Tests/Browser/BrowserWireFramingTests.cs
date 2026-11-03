using System.Buffers.Binary;
using PowerTrove.Core.Browser;

namespace PowerTrove.Core.Tests.Browser;

public class BrowserWireFramingTests
{
    [Fact]
    public async Task HandshakeAndRequestUseNativeMessagingFraming()
    {
        BrowserWireProtocol.Frame hello = new BrowserWireProtocol.Hello("chrome", "0.1.0", 1, 1, ["context.fast"]);
        using var memory = new MemoryStream();
        await BrowserWireFraming.WriteAsync(memory, hello, CancellationToken.None);
        byte[] bytes = memory.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0,4)));
        memory.Position = 0;
        var parsed = Assert.IsType<BrowserWireProtocol.Hello>(
            await BrowserWireFraming.ReadAsync(memory, CancellationToken.None));
        Assert.Equal("chrome", parsed.Browser);
    }

    [Fact]
    public async Task OversizedFramesAreRejectedBeforeAllocation()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, BrowserWireProtocol.MaximumMessageBytes + 1);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BrowserWireFraming.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJsonDoesNotTurnIntoBrowserContent()
    {
        using var stream = new MemoryStream();
        var payload = "{\"type\":\"request\",\"action\":\"runShell\"}"u8.ToArray();
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await stream.WriteAsync(payload);
        stream.Position = 0;
        Assert.Null(await BrowserWireFraming.ReadAsync(stream, CancellationToken.None));
    }
}
