using FileKit.Html;

namespace PowerTrove.Pack.Browser;

/// <summary>
/// Dialog-free, local print of the page the browser saved as MHTML.
/// Edge runs with a throwaway profile, scripts disabled and network blocked in a bounded job;
/// the archive's resources come only from the archive.
/// </summary>
internal static class BrowserPdfPrint
{
    /// <summary>Prints the page as the browser saved it (MHTML), with its layout, styles and images.</summary>
    public static async Task<byte[]> RenderArchiveAsync(byte[] mhtml, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mhtml is not { Length: > 0 } || mhtml.LongLength > ChromiumPdfWriter.MaxArchiveBytes)
            throw new HtmlPrintException("Browser page archive is unavailable.");
        var writer = new ChromiumPdfWriter(new WindowsHtmlBrowserLauncher());
        if (!writer.IsAvailable) throw new HtmlPrintException("Microsoft Edge is required for PDF export.");
        string directory = Path.Combine(Path.GetTempPath(), "PowerTrove-Browser-Pdf-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "page.mhtml"), file = Path.Combine(directory, "page.pdf");
        try
        {
            await File.WriteAllBytesAsync(archive, mhtml, token).ConfigureAwait(false);
            await writer.WriteArchiveAsync(archive, file, new HtmlPrintOptions(), token).ConfigureAwait(false);
            return await ReadPdfAsync(file, token).ConfigureAwait(false);
        }
        finally
        {
            // No on-disk page content may survive a failed render/cancel.
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<byte[]> ReadPdfAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file)) throw new HtmlPrintException("PDF output is unavailable.");
        long length = new FileInfo(file).Length;
        if (length < 5 || length > 64L * 1024 * 1024)
            throw new HtmlPrintException("Printed PDF exceeds the safe limit.");
        byte[] bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
        if (!bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new HtmlPrintException("Invalid printed PDF.");
        return bytes;
    }
}
