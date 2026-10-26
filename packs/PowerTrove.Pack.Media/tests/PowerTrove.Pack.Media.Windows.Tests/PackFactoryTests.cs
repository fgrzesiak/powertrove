using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Media;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Media.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new MediaPackFactory();
}
