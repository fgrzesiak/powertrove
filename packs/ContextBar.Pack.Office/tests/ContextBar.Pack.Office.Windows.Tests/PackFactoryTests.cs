using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Office;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Office.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new OfficePackFactory();
}
