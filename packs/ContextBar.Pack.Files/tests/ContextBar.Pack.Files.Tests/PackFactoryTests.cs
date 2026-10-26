using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Files;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Files.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new FilesPackFactory();
}
