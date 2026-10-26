using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Pdf;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Pdf.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new PdfPackFactory();
}
