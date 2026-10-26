using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Media;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Media.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new MediaPackFactory();
}
