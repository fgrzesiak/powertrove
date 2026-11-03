using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FileKit.Detection;
using FileKit.Text;

namespace PowerTrove.Pack.Markdown;

internal enum ImageMode { Preview, Link, Embed }

/// <summary>Counts only; never sources or paths.</summary>
internal sealed record ImageReport(int Embedded, int Remote, int Unavailable, int Unsupported)
{
    public static ImageReport None { get; } = new(0, 0, 0, 0);
}

/// <param name="EmbeddedLength">Characters of embedded images, taken from the embedding budget.</param>
internal sealed record BuiltDocument(string Body, string Title, ImageReport Images, int RemovedContent, long EmbeddedLength = 0);

/// <summary>Reads Markdown, resolves its images under the companion-file rule and assembles HTML documents.</summary>
internal sealed partial class MarkdownDocuments(IOutputFileSystem fileSystem, IMarkdownEngine engine)
{
    public const long MaximumMarkdownBytes = 4L * 1024 * 1024;
    public const long MaximumHtmlInputBytes = 5L * 1024 * 1024;
    private const long MaximumImageBytes = 10L * 1024 * 1024;
    private const long MaximumSvgBytes = 2L * 1024 * 1024;
    /// <summary>Base64 characters embedded in one exported HTML file.</summary>
    private const long HtmlEmbedBudget = 48L * 1024 * 1024;
    public static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn"];

    public async Task<string> ReadTextAsync(string path, long maximumBytes, CancellationToken ct)
    {
        try
        {
            await using var input = fileSystem.OpenRead(path);
            return (await TextDecoding.ReadAsync(input, maximumBytes, ct).ConfigureAwait(false)).Text;
        }
        catch (TextLimitException) { throw new FileProcessingException(SR.Get("TooLarge")); }
        catch (DecoderFallbackException) { throw new FileProcessingException(SR.Get("InvalidText")); }
    }

    /// <param name="keepRemote">Keeps internet image addresses (exported HTML); previews and PDFs never load them.</param>
    /// <param name="anchorPrefix">Keeps anchors apart when several documents share one page.</param>
    public async Task<BuiltDocument> BuildAsync(string markdown, string documentPath, ImageMode mode, long embedBudget, CancellationToken ct, bool keepRemote = false, string? anchorPrefix = null)
    {
        var rendered = await Task.Run(() => engine.Render(markdown, ct), ct).ConfigureAwait(false);
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        int embedded = 0, remote = 0, unavailable = 0, unsupported = 0;
        long budget = embedBudget;
        foreach (string source in rendered.ImageSources)
        {
            ct.ThrowIfCancellationRequested();
            string? mapped = null;
            switch (Classify(source, documentPath, out string? relative))
            {
                case ImageKind.Data: mapped = source.Trim(); break;
                case ImageKind.Remote:
                    if (keepRemote) mapped = source.Trim(); else remote++;
                    break;
                case ImageKind.Local when mode == ImageMode.Preview:
                    mapped = DocumentPreviewData.ResourceBaseUri + string.Join('/', relative!.Split('/').Select(Uri.EscapeDataString));
                    break;
                case ImageKind.Local when mode == ImageMode.Link: mapped = source.Trim(); break;
                case ImageKind.Local:
                    (mapped, bool missing) = await EmbedAsync(documentPath, relative!, budget, ct).ConfigureAwait(false);
                    if (mapped is not null) { budget -= mapped.Length; embedded++; }
                    else if (missing) unavailable++; else unsupported++;
                    break;
                default: unsupported++; break;
            }
            map[source] = mapped;
        }
        string body = await Task.Run(() => rendered.ToHtml(source => map.GetValueOrDefault(source), ct, anchorPrefix), ct).ConfigureAwait(false);
        string title = string.IsNullOrWhiteSpace(rendered.Title) ? Path.GetFileNameWithoutExtension(documentPath) : rendered.Title!.Trim();
        if (title.Length > DocumentPreviewData.MaximumTitleLength) title = title[..DocumentPreviewData.MaximumTitleLength];
        return new(body, title, new(embedded, remote, unavailable, unsupported), rendered.RemovedContent, embedBudget - budget);
    }

    /// <summary>The viewer's document: images come from the host's document origin, never the network.</summary>
    public static string PreviewDocument(BuiltDocument document)
    {
        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src "
            + DocumentPreviewData.ResourceBaseUri.TrimEnd('/') + " data:; style-src 'unsafe-inline'\"><title>" + WebUtility.HtmlEncode(document.Title)
            + "</title><style>" + MarkdownStyles.Document + MarkdownStyles.DarkScheme + "</style></head><body><article class=\"markdown-body\">"
            + document.Body + "</article></body></html>";
        if (html.Length > DocumentPreviewData.MaximumHtmlLength) throw new MarkdownLimitException("The rendered document exceeds the preview limit.");
        return html;
    }

    /// <summary>A standalone static page; the policy forbids scripts and only allows images and inline styles.</summary>
    public static string ExportDocument(BuiltDocument document) =>
        "<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
        + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data: file: http: https:; style-src 'unsafe-inline'\">\n"
        + "<meta name=\"generator\" content=\"PowerTrove\">\n<title>" + WebUtility.HtmlEncode(document.Title) + "</title>\n<style>" + MarkdownStyles.Document
        + "</style>\n</head>\n<body>\n<article class=\"markdown-body\">\n" + document.Body + "\n</article>\n</body>\n</html>\n";

    /// <summary>Content for the shared Chromium print document, which adds its own restrictive policy.</summary>
    public static string PrintDocument(BuiltDocument document) =>
        "<style>" + MarkdownStyles.Document + MarkdownStyles.Print + "</style><article class=\"markdown-body\">" + document.Body + "</article>";

    public static long HtmlBudget => HtmlEmbedBudget;

    /// <summary>The shared print document holds at most 5 MiB, so images get what the text leaves.</summary>
    public static long PrintBudget(string markdown) =>
        Math.Max(0, FileKit.Html.HtmlPrintDocument.MaxBytes - 3L * Encoding.UTF8.GetByteCount(markdown) - MarkdownStyles.Document.Length - 64 * 1024);

    private enum ImageKind { Data, Remote, Local, Unsupported }

    private static ImageKind Classify(string source, string documentPath, out string? relative)
    {
        relative = null;
        string value = source.Trim();
        if (value.Length == 0) return ImageKind.Unsupported;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value.Length <= 14_000_000 && SafeDataImage().IsMatch(value) ? ImageKind.Data : ImageKind.Unsupported;
        if (value.StartsWith("//", StringComparison.Ordinal)) return ImageKind.Remote;
        if (Scheme().Match(value) is { Success: true } scheme)
            return scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" && Uri.TryCreate(value, UriKind.Absolute, out _) ? ImageKind.Remote : ImageKind.Unsupported;
        int cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];
        string decoded;
        try { decoded = Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return ImageKind.Unsupported; }
        if (!CompanionResources.TryResolve(documentPath, decoded, out string full)) return ImageKind.Unsupported;
        relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, full).Replace('\\', '/');
        return ImageKind.Local;
    }

    private async Task<(string? DataUri, bool Missing)> EmbedAsync(string documentPath, string relative, long budget, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            await using var input = fileSystem.OpenCompanionRead(documentPath, relative);
            using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + read > MaximumImageBytes || (memory.Length + read) * 4 / 3 > budget) return (null, false);
                memory.Write(buffer, 0, read);
            }
            bytes = memory.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return (null, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, true); }
        string? type = FileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, FileSignature.HeaderLength))).Format switch
        {
            FileFormat.Png => "image/png", FileFormat.Jpeg => "image/jpeg", FileFormat.Gif => "image/gif",
            FileFormat.WebP => "image/webp", FileFormat.Avif => "image/avif", FileFormat.Bmp => "image/bmp",
            _ => IsSvg(relative, bytes) ? "image/svg+xml" : null
        };
        if (type is null) return (null, false);
        string uri = "data:" + type + ";base64," + Convert.ToBase64String(bytes);
        return uri.Length > budget ? (null, false) : (uri, false);
    }

    /// <summary>SVG is shown only as an image, where browsers run no scripts and load no resources.</summary>
    private static bool IsSvg(string relative, byte[] bytes)
    {
        if (!relative.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || bytes.Length > MaximumSvgBytes) return false;
        string head;
        try { head = new UTF8Encoding(false, true).GetString(bytes, 0, Math.Min(bytes.Length, 4096)); }
        catch (DecoderFallbackException) { return false; }
        return head.Contains("<svg", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^data:image/(png|jpeg|jpg|gif|webp|avif|bmp|svg\+xml)(;charset=[A-Za-z0-9_-]+)?(;base64)?,[A-Za-z0-9+/=%._~!$&'()*,;:@-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeDataImage();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.-]*):", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Scheme();
}
