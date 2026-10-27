using System.Text;
using FileKit.EInvoice;
using FileKit.Html;
using FileKit.Pdf;
using FileKit.Xlsx;

namespace PowerTrove.Pack.EInvoice;

public sealed class InvoiceAction(FileActionServices services, string format, bool embedded, ILocalHtmlPdfPrinter? printer) : PerFileAction<HtmlPrintOptions>(services)
{
    private string TitleKey => embedded ? format == "xml" ? "ExtractTitle" : "EmbeddedTitle" : format switch { "html" => "ViewTitle", "pdf" => "PdfTitle", "csv" => "CsvTitle", _ => "ExcelTitle" };
    private string DescriptionKey => embedded ? format == "xml" ? "ExtractDescription" : "EmbeddedDescription" : format switch { "html" => "ViewDescription", "pdf" => "PdfDescription", "csv" => "CsvDescription", _ => "ExcelDescription" };
    public override ActionDescriptor Descriptor => new("powertrove.einvoice." + (embedded ? format == "xml" ? "extract-xml" : "view-embedded" : format == "html" ? "view" : "to-" + format), SR.Get(TitleKey), SR.Get(DescriptionKey),
        new HashSet<ContentKind> { ContentKind.Files }, ["invoice", "xrechnung", "zugferd", "factur-x", "ubl", "cii", format], true, true,
        Parameters: format == "pdf" ? [ActionParameterDefinition.Choice("Paper", SR.Get("Paper"), "a4", [new("a4", SR.Get("A4")), new("letter", SR.Get("Letter"))]), ActionParameterDefinition.Integer("MarginMm", SR.Get("Margin"), 10, 0, 50)] : [],
        Selection: SelectionRequirements.Any(embedded ? "pdf" : "text"));
    protected override FileInputFilter Input => new(SR.Get(embedded ? "PdfFiles" : "XmlFiles"), [embedded ? ".pdf" : ".xml"], null);
    protected override string? UnavailableReason => format == "pdf" && printer is not { IsAvailable: true } ? SR.Get("PrinterUnavailable") : null;
    protected override HtmlPrintOptions ReadOptions(ActionParameterValues values) => format == "pdf" ? new(values.GetChoice("Paper"), false, values.GetInt32("MarginMm")) : new();
    protected override string DescribeOperation(int count, HtmlPrintOptions options) => SR.Get(DescriptionKey);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> files, HtmlPrintOptions options) => [SR.Get("Limits"), SR.Get("Notice")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, HtmlPrintOptions options) { yield return new(Path.GetFileNameWithoutExtension(file.Name) + "-invoice", "." + format); }
    private async Task<(Invoice Invoice, byte[]? Xml)> Read(string path, CancellationToken ct)
    {
        await using var source = Services.FileSystem.OpenRead(path);
        if (!embedded) return (await InvoiceXmlReader.ReadAsync(source, ct).ConfigureAwait(false), null);
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0) { if (memory.Length + read > 25 * 1024 * 1024) throw new InvoiceException("PDF input limit exceeded."); await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
        return await Task.Run(() =>
        {
            var pdf = PdfDocument.Load(memory.ToArray(), new PdfReadLimits { MaxFileBytes = 25 * 1024 * 1024, MaxObjects = 100000, MaxPages = 200, MaxNestingDepth = 64, MaxDecodedStreamBytes = 16 * 1024 * 1024 });
            if (pdf.Trailer["Encrypt"] is not null) throw new InvoiceException("Encrypted invoice containers are unsupported.");
            var files = PdfEmbeddedFiles.Read(pdf, name => name.Equals("factur-x.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("zugferd-invoice.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("xrechnung.xml", StringComparison.OrdinalIgnoreCase), ct: ct);
            if (files.Count != 1) throw new InvoiceException("No unique supported embedded invoice XML."); byte[] xml = files[0].Data.ToArray(); return (InvoiceXmlReader.Parse(xml, ct), (byte[]?)xml);
        }, ct).ConfigureAwait(false);
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try { foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts)) await Read(file.Path!, ct).ConfigureAwait(false); return preview with { PreviewText = SR.Get("Preview") }; }
        catch (Exception ex) when (ex is InvoiceException or PdfException) { return preview with { IsBlocked = true, PreviewText = SR.Get("Invalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, HtmlPrintOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            var data = await Read(item.Path, ct).ConfigureAwait(false); string name = item.BaseName + "-invoice";
            if (format == "pdf")
            {
                string html = await Task.Run(() => InvoicePresentation.Html(data.Invoice, ct, false), ct).ConfigureAwait(false);
                // The generated document is encoded; the host independently validates/restricts it.
                using var source = new MemoryStream(Encoding.UTF8.GetBytes(html)); var document = await printer!.PrepareAsync(source, ct).ConfigureAwait(false);
                await output.WriteFileFromPathAsync(item.Directory, name, ".pdf", (path, token) => printer.RenderAsync(document, path, options, token), ct).ConfigureAwait(false);
            }
            else await output.WriteFileAsync(item.Directory, name, "." + format, async (stream, token) =>
            {
                if (format == "xlsx") await XlsxWriter.WriteAsync(stream, InvoicePresentation.Workbook(data.Invoice), token).ConfigureAwait(false);
                else if (format == "xml") await stream.WriteAsync(data.Xml!, token).ConfigureAwait(false);
                else
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    await Task.Run(() => { if (format == "csv") InvoicePresentation.Csv(writer, data.Invoice, token); else writer.Write(InvoicePresentation.Html(data.Invoice, token)); }, token).ConfigureAwait(false);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (HtmlPrintException ex) when (ex.Failure == HtmlPrintFailure.Cleanup) { throw new FileProcessingException(SR.Get("CleanupError"), ex); }
        catch (Exception ex) when (ex is InvoiceException or PdfException or HtmlPrintException or XlsxException) { throw new FileProcessingException(SR.Get("Invalid"), ex); }
    }
}
