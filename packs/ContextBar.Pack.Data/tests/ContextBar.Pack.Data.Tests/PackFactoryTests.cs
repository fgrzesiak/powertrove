using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Data;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Data.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new DataPackFactory();
}
