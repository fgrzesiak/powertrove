using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Data;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Data.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new DataPackFactory();
}
