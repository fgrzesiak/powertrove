using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Abstractions.Recipes;
using PowerTrove.Core.Workflows;
using PowerTrove.Abstractions.Packs;

namespace PowerTrove.Desktop;

/// <summary>
/// Host-owned actions. The default Core profile compiles in no pack: file tools arrive as signed
/// downloads. The Base and Full profiles compile packs in for development and offline builds.
/// </summary>
public static partial class BuiltInActionPacks
{
    public static IReadOnlyList<IActionPack> Create(
        IWorkflowRunner workflowRunner,
        IFileRenameService? renamer = null)
    {
        var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var host = new PackHostServices(typeof(BuiltInActionPacks).Assembly.GetName().Version ?? new Version(0, 1, 0), services);
        if (renamer is not null) host.Add<IFileRenameService>(renamer);
        var factories = new List<IPackFactory> { new RecipePackFactory(workflowRunner) };
        AddBasePackFactories(factories, host);
        AddOptionalPackFactories(factories, host);
        return factories.Select(factory => factory.Create(host)).ToArray();
    }

    // Each profile file is excluded at compile time where it does not apply; the invocation then compiles away.
    static partial void AddBasePackFactories(List<IPackFactory> factories, PackHostServices host);
    static partial void AddOptionalPackFactories(List<IPackFactory> factories, PackHostServices host);
}
