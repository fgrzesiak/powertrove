using PowerTrove.Abstractions.Packs;
using PowerTrove.Pack.Files;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Pack.Files.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new FilesPackFactory();
}
