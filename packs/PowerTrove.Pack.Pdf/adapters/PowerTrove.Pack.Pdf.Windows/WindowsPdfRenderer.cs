using System.Globalization;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Pdf;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PowerTrove.Pack.Pdf.Windows;

/// <summary>Renders PDF pages with the PDF engine built into Windows (Windows.Data.Pdf).</summary>
public sealed class WindowsPdfRenderer : IPdfPageRenderer
{
    /// <summary>Upper bound for a single rendered page, to keep memory bounded at high DPI.</summary>
    private const double MaxPixelsPerPage = 80_000_000;
    private const int MaxSideLength = 16_384;

    public bool IsAvailable => true;

    public async Task<PageRaster> RenderGrayAsync(string path, int pageIndex, int maxWidthPx, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPx);
        cancellationToken.ThrowIfCancellationRequested();
        var document = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        using var page = document.GetPage((uint)pageIndex);

        var (width, height) = GetGrayRenderSize(page.Size.Width, page.Size.Height, maxWidthPx);

        var renderOptions = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
            BitmapEncoderId = global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, renderOptions).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore).AsTask(cancellationToken).ConfigureAwait(false);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitmap.PixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitmap.PixelHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitmap.PixelWidth, Math.Min(maxWidthPx, MaxSideLength));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitmap.PixelHeight, MaxSideLength);
        long pixelCount = (long)bitmap.PixelWidth * bitmap.PixelHeight;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelCount, (long)MaxPixelsPerPage);
        // The Windows decoder does not support Gray8 conversion for every rendered PNG.
        // Decode opaque BGRA and derive luminance explicitly, preserving the raster bounds.
        var bgra = new byte[checked((int)pixelCount * 4)];
        bitmap.CopyToBuffer(bgra.AsBuffer());
        var pixels = new byte[checked((int)pixelCount)];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            int offset = i * 4;
            pixels[i] = (byte)((77 * bgra[offset + 2] + 150 * bgra[offset + 1] + 29 * bgra[offset] + 128) >> 8);
        }

        return new PageRaster(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    internal static (int Width, int Height) GetGrayRenderSize(double width, double height, int maxWidthPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPx);
        if (!double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        // Divide before multiplying so even extreme finite dimensions cannot overflow.
        double scale = Math.Min(1, Math.Min(Math.Min(maxWidthPx, MaxSideLength) / width, MaxSideLength / height));
        width *= scale;
        height *= scale;
        if (width * height > MaxPixelsPerPage)
        {
            double reduce = Math.Sqrt(MaxPixelsPerPage / (width * height));
            width *= reduce;
            height *= reduce;
        }

        // Rounding down keeps the integer raster within the pixel budget.
        return (Math.Max(1, (int)Math.Floor(width)), Math.Max(1, (int)Math.Floor(height)));
    }

    public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        return (int)document.PageCount;
    }

    public async Task RenderToDirectoryAsync(
        string pdfPath,
        string directory,
        string baseName,
        IReadOnlyList<int>? pageIndices,
        PdfRenderOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var document = await LoadAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        var pages = pageIndices ?? Enumerable.Range(0, (int)document.PageCount).ToList();
        int digits = Math.Max(2, document.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        bool jpeg = string.Equals(options.Format, "jpeg", StringComparison.OrdinalIgnoreCase);
        int dpi = Math.Clamp(options.Dpi, 72, 600);

        for (int i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage((uint)pages[i]);

            // Page.Size is in device-independent pixels (1/96 inch).
            double scale = dpi / 96.0;
            double width = page.Size.Width * scale, height = page.Size.Height * scale;
            if (width * height > MaxPixelsPerPage)
            {
                double reduce = Math.Sqrt(MaxPixelsPerPage / (width * height));
                width *= reduce;
                height *= reduce;
            }

            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
                BitmapEncoderId = jpeg ? global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId : global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };

            string name = $"{baseName} - page {(pages[i] + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}{(jpeg ? ".jpg" : ".png")}";
            await using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await page.RenderToStreamAsync(file.AsRandomAccessStream(), renderOptions).AsTask(cancellationToken).ConfigureAwait(false);
            progress?.Report((double)(i + 1) / pages.Count);
        }
    }

    private static async Task<PdfDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            return await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Windows reports password-protected files with a specific HRESULT.
            const int PasswordRequired = unchecked((int)0x8007052B);
            throw new FileProcessingException(ex.HResult == PasswordRequired
                ? "Password-protected PDFs are not supported yet."
                : "The PDF could not be opened.", ex);
        }
    }
}
