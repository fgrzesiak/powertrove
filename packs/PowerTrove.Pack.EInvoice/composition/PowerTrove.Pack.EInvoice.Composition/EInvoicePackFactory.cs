using PowerTrove.Abstractions.Packs;
using FileKit.Html;

[assembly: ActionPackFactory(typeof(PowerTrove.Pack.EInvoice.EInvoicePackFactory))]
namespace PowerTrove.Pack.EInvoice;

public sealed class EInvoicePackFactory : IPackFactory
{
    public PowerTrove.Abstractions.ActionPackInfo Info => EInvoicePack.Describe();
    public PowerTrove.Abstractions.IActionPack Create(IPackHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host); return new EInvoicePack(host.Files, host.GetService<ILocalHtmlPdfPrinter>());
    }
}
