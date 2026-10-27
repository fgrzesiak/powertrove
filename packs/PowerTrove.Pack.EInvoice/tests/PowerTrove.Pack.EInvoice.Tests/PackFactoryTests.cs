using PowerTrove.Abstractions.Packs;
namespace PowerTrove.Pack.EInvoice.Tests;
public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new EInvoicePackFactory();
}
