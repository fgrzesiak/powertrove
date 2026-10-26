using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Images;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Images.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new ImagesPackFactory();
}
