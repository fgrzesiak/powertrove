using ContextBar.Abstractions.Packs;
using ContextBar.Pack.Images;
using ContextBar.Abstractions.Testing;

namespace ContextBar.Pack.Images.Tests;

public sealed class PackFactoryTests : PackFactoryContractTestsBase
{
    protected override IPackFactory CreateFactory() => new ImagesPackFactory();
}
