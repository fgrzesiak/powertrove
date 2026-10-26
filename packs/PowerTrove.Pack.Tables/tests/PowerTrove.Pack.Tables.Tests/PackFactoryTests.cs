using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Tables;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Tables.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new TablesPackFactory();
}
