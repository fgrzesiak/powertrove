using System;
using System.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Packs;
using Xunit;

namespace PowerTrove.Abstractions.Testing;

/// <summary>A parameterless, side-effect-free creation contract shared by every compiled-in pack.</summary>
public abstract class PackFactoryContractTestsBase
{
    protected abstract IPackFactory CreateFactory();
    protected virtual IPackHostServices CreateHost() => new PackHostServices(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));

    [Fact]
    public void Factory_CreatesAllActionsWithOnlyExplicitTestServices()
    {
        var factory = CreateFactory();
        var pack = factory.Create(CreateHost());
        Assert.Equal(factory.Info, pack.Info);
        Assert.NotEmpty(pack.Actions);
        Assert.Equal(pack.Actions.Count, pack.Actions.Select(a => a.Descriptor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Factory_RejectsMissingHost() => Assert.Throws<ArgumentNullException>(() => CreateFactory().Create(null!));
}
