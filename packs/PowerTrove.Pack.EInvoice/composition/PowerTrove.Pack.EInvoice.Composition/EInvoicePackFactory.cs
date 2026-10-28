using PowerTrove.Abstractions.Packs;
using FileKit.Html;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.EInvoice.EInvoicePackFactory))]
namespace PowerTrove.Pack.EInvoice;

public sealed class EInvoicePackFactory : IPackFactory
{
    public PowerTrove.Abstractions.ActionPackInfo Info => EInvoicePack.Describe();
    public PowerTrove.Abstractions.IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        // A pack worker host offers no HTML printer; the pack then brings its own.
        return new EInvoicePack(host.Files, host.GetService<ILocalHtmlPdfPrinter>() ?? (OperatingSystem.IsWindows() ? new PowerTrove.Pack.EInvoice.Html.InvoiceHtmlPdfPrinter() : null));
    }
}
