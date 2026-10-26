using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Tables;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Tables.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new TablesPackFactory();
}
