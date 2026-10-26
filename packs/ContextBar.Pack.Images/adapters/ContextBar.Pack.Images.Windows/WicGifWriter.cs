using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContextBar.Pack.Images.Windows;

internal sealed record WicGifFrame(int Width, int Height, byte[] Pixels, int Delay);
/// <summary>One WIC implementation shared by the independent Images and Media adapters.</summary>
internal static class WicGifWriter
{
    public static async Task WriteAsync(IAsyncEnumerable<WicGifFrame> frames, Stream destination, bool loop, int maximumFrames, int minimumFrames, CancellationToken ct)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, output).AsTask(ct).ConfigureAwait(false);
        int count = 0, width = 0, height = 0;
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
        {
            if (++count > maximumFrames || frame.Width is < 1 or > 2000 || frame.Height is < 1 or > 2000 || frame.Delay is < 2 or > 1000 || frame.Pixels.Length != (long)frame.Width * frame.Height * 4) throw new InvalidDataException();
            if (count == 1)
            {
                width = frame.Width; height = frame.Height;
                var container = new BitmapPropertySet {
                    { "/logscrdesc/Width", new BitmapTypedValue((ushort)width, PropertyType.UInt16) },
                    { "/logscrdesc/Height", new BitmapTypedValue((ushort)height, PropertyType.UInt16) } };
                if (loop)
                {
                    container.Add("/appext/Application", new BitmapTypedValue(Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array));
                    container.Add("/appext/Data", new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array));
                }
                await encoder.BitmapContainerProperties.SetPropertiesAsync(container).AsTask(ct).ConfigureAwait(false);
            }
            else
            {
                if (frame.Width != width || frame.Height != height) throw new InvalidDataException();
                await encoder.GoToNextFrameAsync().AsTask(ct).ConfigureAwait(false);
            }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, frame.Pixels);
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet {
                { "/grctlext/Delay", new BitmapTypedValue((ushort)frame.Delay, PropertyType.UInt16) },
                { "/grctlext/Disposal", new BitmapTypedValue((byte)2, PropertyType.UInt8) },
                { "/grctlext/TransparencyFlag", new BitmapTypedValue(false, PropertyType.Boolean) },
                { "/imgdesc/Left", new BitmapTypedValue((ushort)0, PropertyType.UInt16) },
                { "/imgdesc/Top", new BitmapTypedValue((ushort)0, PropertyType.UInt16) }
            }).AsTask(ct).ConfigureAwait(false);
            if (output.Size > 300UL * 1024 * 1024) throw new InvalidDataException();
        }
        if (count < minimumFrames) throw new InvalidDataException();
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        if (output.Size > 300UL * 1024 * 1024) throw new InvalidDataException();
        output.Seek(0); using var encoded = output.AsStreamForRead(); await encoded.CopyToAsync(destination, ct).ConfigureAwait(false);
    }
}
