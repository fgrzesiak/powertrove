using System.Runtime.CompilerServices;
using ContextBar.Pack.Media;


namespace ContextBar.Pack.Media.Windows;

public sealed class WindowsMediaGifEncoder : IGifFrameEncoder
{
    public async Task EncodeAsync(IAsyncEnumerable<MediaGifFrame> frames, Stream destination, CancellationToken ct)
    {
        async IAsyncEnumerable<WicGifFrame> Convert([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var f in frames.WithCancellation(token).ConfigureAwait(false)) yield return new(f.Width, f.Height, f.Bgra32, f.DelayCentiseconds);
        }
        try { await WicGifWriter.WriteAsync(Convert(ct), destination, true, 600, 1, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw MediaMessages.ProcessingFailed(ex); }
    }
}
