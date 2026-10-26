using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Pdf;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new PdfPackFactory();
}
