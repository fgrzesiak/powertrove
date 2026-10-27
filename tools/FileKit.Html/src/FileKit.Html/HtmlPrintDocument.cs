using System.Text;

namespace FileKit.Html;

public enum HtmlPrintFailure { Rendering, Cleanup }
public sealed class HtmlPrintException(string message, Exception? inner = null, HtmlPrintFailure failure = HtmlPrintFailure.Rendering) : Exception(message, inner)
{ public HtmlPrintFailure Failure { get; } = failure; }
public sealed record HtmlPrintOptions(string Paper = "a4", bool Landscape = false, int MarginMillimeters = 10)
{
    public void Validate()
    {
        if (Paper is not ("a4" or "letter") || MarginMillimeters is < 0 or > 50) throw new HtmlPrintException("Invalid print options.");
    }
    public (double Width, double Height) Inches => Paper == "letter" ? (8.5, 11) : (210 / 25.4, 297 / 25.4);
}

/// <summary>Bounded UTF-8 self-contained HTML. The leading policy is parsed before
/// untrusted markup; browser-level network/script restrictions are additionally mandatory.</summary>
public sealed class HtmlPrintDocument
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const string Policy = "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'; worker-src 'none'; media-src 'none'; base-uri 'none'; form-action 'none'";
    private HtmlPrintDocument(string html) => Html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" + Policy + "\"></head><body>" + html + "</body></html>";
    public string Html { get; }
    public static HtmlPrintDocument FromText(string html, IHtmlMarkupValidator validator)
    {
        if (string.IsNullOrWhiteSpace(html) || Encoding.UTF8.GetByteCount(html) > MaxBytes || html.Contains('\0') || html.Count(c => c == '<') > 50000) throw new HtmlPrintException("Invalid HTML size or encoding.");
        return new(validator.ValidateAndNormalize(html));
    }

    public static async Task<HtmlPrintDocument> ReadAsync(Stream input, IHtmlMarkupValidator validator, CancellationToken ct = default)
    {
        using var memory = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxBytes) throw new HtmlPrintException("HTML input limit exceeded.");
            await memory.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            string html = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, (int)memory.Length).TrimStart('\uFEFF');
            return await Task.Run(() => FromText(html, validator), ct).ConfigureAwait(false);
        }
        catch (DecoderFallbackException) { throw new HtmlPrintException("HTML must be UTF-8."); }
    }
}

public interface IHtmlMarkupValidator
{
    string ValidateAndNormalize(string html);
}

public interface IHtmlBrowserProcess : IDisposable
{
    bool HasExited { get; }
}
public interface IHtmlBrowserLauncher
{
    bool IsAvailable { get; }
    IHtmlBrowserProcess Start(IReadOnlyList<string> arguments);
}

/// <summary>Shared host printing capability; document packs remain independent.</summary>
public interface ILocalHtmlPdfPrinter
{
    bool IsAvailable { get; }
    Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken);
    Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken);
}
