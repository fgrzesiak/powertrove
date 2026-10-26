using System.Runtime.CompilerServices;
using ContextBar.Abstractions.Files;
using ContextBar.Pack.Images;

namespace ContextBar.Pack.Images.Windows;

public sealed class WindowsAnimatedGifEncoder : IAnimatedGifEncoder
{
    public async Task EncodeAsync(IAsyncEnumerable<ImageRaster> frames, Stream destination, GifEncodingOptions options, CancellationToken ct)
    {
        async IAsyncEnumerable<WicGifFrame> Convert([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var frame in frames.WithCancellation(token).ConfigureAwait(false))
            {
                if (frame.Width != options.Width || frame.Height != options.Height) throw new InvalidDataException();
                yield return new(frame.Width, frame.Height, frame.Bgra32, options.DelayCentiseconds);
            }
        }
        try
        {
            if (options.Width is < 1 or > 2000 || options.Height is < 1 or > 2000 || options.DelayCentiseconds is < 2 or > 1000) throw new InvalidDataException();
            await WicGifWriter.WriteAsync(Convert(ct), destination, options.Loop, 300, 2, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new FileProcessingException(ExtendedImageMessages.Get("GifFailed"), ex); }
    }
}
