using FileKit.Html;
namespace PowerTrove.Pack.EInvoice;

public sealed class EInvoicePack : IActionPack
{
    public EInvoicePack(FileActionServices services, ILocalHtmlPdfPrinter? printer = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Actions = new IAction[] { new InvoiceAction(services, "html", false, printer), new InvoiceAction(services, "pdf", false, printer), new InvoiceAction(services, "csv", false, printer), new InvoiceAction(services, "xlsx", false, printer), new InvoiceAction(services, "xml", true, printer), new InvoiceAction(services, "html", true, printer) };
    }
    public ActionPackInfo Info => Describe();
    internal static ActionPackInfo Describe() => new("powertrove.einvoice", SR.Get("Pack"), "0.1.0", "PowerTrove");
    public IReadOnlyList<IAction> Actions { get; }
}
