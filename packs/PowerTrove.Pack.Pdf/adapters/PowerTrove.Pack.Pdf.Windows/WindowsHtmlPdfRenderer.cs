using FileKit.Html;

namespace PowerTrove.Pack.Pdf.Windows;

public sealed class WindowsHtmlPdfRenderer(IHtmlMarkupValidator validator) : IHtmlPdfRenderer
{
    private readonly ChromiumPdfWriter _writer = new(new WindowsHtmlBrowserLauncher());
    public bool IsAvailable => _writer.IsAvailable;
    public Task<HtmlPrintDocument> PrepareAsync(Stream source, CancellationToken cancellationToken) => HtmlPrintDocument.ReadAsync(source, validator, cancellationToken);
    public Task RenderAsync(HtmlPrintDocument document, string destinationPath, HtmlPrintOptions options, CancellationToken cancellationToken) =>
        _writer.WriteAsync(document, destinationPath, options, cancellationToken);
}
