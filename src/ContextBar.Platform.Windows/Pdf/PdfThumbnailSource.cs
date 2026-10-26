using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ContextBar.Platform.Windows.Pdf;

/// <summary>
/// Page thumbnails of one PDF for the visual page organizer, rendered by the PDF engine built into
/// Windows. Rendering shows the page with its stored rotation; extra rotation is applied by the UI.
/// Password-protected PDFs cannot be opened here (the caller shows a message instead).
/// </summary>
public sealed class PdfThumbnailSource : IDisposable
{
    private readonly PdfDocument _document;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PdfThumbnailSource(PdfDocument document) => _document = document;

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfThumbnailSource> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        return new PdfThumbnailSource(document);
    }

    /// <summary>Renders one page as PNG, scaled to <paramref name="width"/> device pixels.</summary>
    public async Task<IRandomAccessStream> RenderAsync(int pageIndex, uint width, CancellationToken cancellationToken)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        // Windows.Data.Pdf pages are not safe to render concurrently from one document.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var page = _document.GetPage((uint)pageIndex);
            var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = width };
            await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
            stream.Seek(0);
            return stream;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Width divided by height of the page as displayed (stored rotation applied).</summary>
    public double AspectRatio(int pageIndex)
    {
        using var page = _document.GetPage((uint)pageIndex);
        var size = page.Size;
        return size.Height > 0 ? size.Width / size.Height : 0.707;
    }

    public void Dispose() => _gate.Dispose();
}
