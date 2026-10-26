using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

/// <summary>Renders PDF pages to images. Implemented with the Windows PDF renderer (Windows.Data.Pdf).</summary>
public interface IPdfPageRenderer
{
    bool IsAvailable { get; }

    /// <summary>
    /// Renders the given zero-based pages (all when null) into <paramref name="directory"/>
    /// as "<paramref name="baseName"/> - page N" files. The directory is owned by the caller.
    /// </summary>
    Task RenderToDirectoryAsync(
        string pdfPath,
        string directory,
        string baseName,
        IReadOnlyList<int>? pageIndices,
        PdfRenderOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    Task<PageRaster> RenderGrayAsync(string path, int pageIndex, int maxWidthPx, CancellationToken cancellationToken);

    Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken);
}

/// <param name="Format">"png" or "jpeg".</param>
/// <param name="Dpi">Resolution, 72..600.</param>
public sealed record PageRaster(int Width, int Height, byte[] Gray8);

/// <param name="Format">"png" or "jpeg".</param>
public sealed record PdfRenderOptions(string Format, int Dpi);

public static class PdfSupport
{
    public static readonly FileInputFilter PdfInput = new("PDF files", new[] { ".pdf" }, new[] { FileFormats.Pdf });

    /// <summary>Built per descriptor so the texts follow the current UI language.</summary>
    public static ActionParameterDefinition PagesParameter => ActionParameterDefinition.Text(
        "Pages", SR.GetString("PdfSupport_Pages_Title"), "1", SR.GetString("PdfSupport_Pages_Description"), "1-3, 5");

    /// <summary>Loads a PDF off the calling thread and maps library errors to user-facing messages.</summary>
    public static async Task<PdfDocument> LoadAsync(string path, CancellationToken cancellationToken, string? password = null)
    {
        try
        {
            return await PdfDocument.OpenAsync(path, password: password, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PdfException ex)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>Runs a CPU-bound PDF operation on the thread pool with typed error mapping.</summary>
    public static Task RunAsync(Action work, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            work();
        }
        catch (PdfException ex)
        {
            throw Wrap(ex);
        }
    }, cancellationToken);

    public static FileProcessingException Wrap(PdfException exception) => exception switch
    {
        PdfPasswordRequiredException => new FileProcessingException(SR.GetString("PdfSupport_Error_PasswordRequired"), exception),
        PdfEncryptedException => new FileProcessingException(SR.GetString("PdfSupport_Error_Encrypted"), exception),
        PdfLimitExceededException => new FileProcessingException(SR.GetString("PdfSupport_Error_LimitExceeded"), exception),
        PdfFormatException => new FileProcessingException(SR.GetString("PdfSupport_Error_Damaged"), exception),
        PdfOperationException => new FileProcessingException(SR.GetString("PdfSupport_Error_OperationFailed"), exception),
        _ => new FileProcessingException(SR.GetString("PdfSupport_Error_Damaged"), exception)
    };

    /// <summary>Loads an image for embedding off the calling thread; unreadable images get an image-specific message.</summary>
    public static Task<PdfImage> LoadImageAsync(Func<PdfImage> load, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            return load();
        }
        catch (PdfFormatException ex)
        {
            throw new FileProcessingException(SR.GetString("PdfSupport_Error_UnsupportedImage"), ex);
        }
        catch (PdfException ex)
        {
            throw Wrap(ex);
        }
    }, cancellationToken);

    /// <summary>Requires <paramref name="order"/> to list every page exactly once, with a localized message otherwise.</summary>
    public static void ValidatePermutation(IReadOnlyList<int> order, int pageCount)
    {
        var seen = new HashSet<int>();
        foreach (int page in order)
            if (!seen.Add(page))
                throw new FileProcessingException(string.Format(SR.GetString("PdfSupport_Error_PageListedTwice"), page + 1));
        for (int page = 0; page < pageCount; page++)
            if (!seen.Contains(page))
                throw new FileProcessingException(string.Format(SR.GetString("PdfSupport_Error_PageMissing"), page + 1));
    }

    /// <summary>Syntax check without a page count (the real count is validated per file).</summary>
    public static string? ValidatePageSyntax(string text, bool allowEmpty, bool allowDuplicates = false)
    {
        if (allowEmpty && (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "all", StringComparison.OrdinalIgnoreCase)))
            return null;
        return PageRange.IsValidSyntax(text) ? null : "Invalid page range syntax.";
    }

    public static IReadOnlyList<int>? ParsePages(string text, int pageCount, bool allowAll, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        if (allowAll && (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "all", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!PageRange.TryParse(text, pageCount, out var pages, out var error, allowDuplicates, cancellationToken))
            throw new FileProcessingException(error!);
        return pages;
    }
}
