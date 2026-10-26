using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Office;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Office.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new OfficePackFactory();
}
