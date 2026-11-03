using System.Buffers.Binary;
using PowerTrove.Core.Browser;

namespace PowerTrove.Core.Browser;

/// <summary>Single-message framing for the desktop pipe and Chrome/Edge native messaging.</summary>
public static class BrowserWireFraming
{
    public static async Task<BrowserWireProtocol.Frame?> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > BrowserWireProtocol.MaximumMessageBytes)
            throw new InvalidDataException("Browser frame exceeds the bounded size.");
        byte[] json = new byte[count];
        await stream.ReadExactlyAsync(json, token).ConfigureAwait(false);
        return BrowserWireProtocol.Decode(json);
    }

    public static async Task WriteAsync(Stream stream, BrowserWireProtocol.Frame frame, CancellationToken token)
    {
        byte[] json = BrowserWireProtocol.Encode(frame);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(json, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
