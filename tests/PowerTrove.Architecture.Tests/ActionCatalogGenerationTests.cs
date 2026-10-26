using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Core;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Architecture.Tests;

/// <summary>Produces discovery metadata from the same factories as the built-in packs.</summary>
public sealed class ActionCatalogGenerationTests
{
    internal static string Generate(IEnumerable<IActionPack> packs) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1",
        packs = packs.Select(p => new
        {
            manifest = ActionPackManifest.Create(p),
            // Bundle sizes do not exist yet. Zero means unknown, never a fabricated estimate.
            downloadBytes = 0L
        }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    [Fact]
    public void GenerateBuiltInCatalog_FromFactories_PreservesDescriptorsAndDoesNotDuplicateInstalledActions()
    {
        var packs = PackFactoriesTests.Factories().Select(f => f.Create(PackFactoriesTests.Host())).ToArray();
        string json = Generate(packs);
        var installed = ActionCatalog.Read(json, packs.Select(p => p.Info), new Version(0, 1, 0), "windows", "x64");
        Assert.Equal(packs.SelectMany(p => p.Actions).Select(a => a.Descriptor.Id), installed.Entries.Select(e => e.Action.Id));
        Assert.All(installed.Entries, e => Assert.Equal(PackInstallState.Installed, e.State));
        var registry = new ActionRegistry();
        foreach (var action in packs.SelectMany(p => p.Actions)) registry.Register(action);
        int count = registry.GetAllActions().Count();
        registry.RegisterCatalog(installed);
        Assert.Equal(count, registry.GetAllActions().Count());

        var missing = ActionCatalog.Read(json, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0), "windows", "x64");
        Assert.All(missing.Entries, e => Assert.Equal(PackInstallState.NotInstalled, e.State));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterCatalog(missing));
        var discovery = new ActionRegistry(); discovery.RegisterCatalog(missing);
        Assert.Equal(count, discovery.GetAllActions().Count());
        Assert.All(discovery.GetAllActions(), a => Assert.IsType<CatalogAction>(a));
        // Optional manual-preview artifact, confined to test output, never the source tree.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "catalog.generated.json"), json);
    }

    [Fact]
    public void AllBundledDesktop_DefaultCatalogHasNoMissingPacks_AndIsCopiedToOutput()
    {
        string root = PackFactoriesTests.FindRoot();
        var catalog = ActionCatalog.Read(File.ReadAllText(Path.Combine(root, "src/PowerTrove.Desktop/catalog.json")),
            PackFactoriesTests.Factories().Select(f => f.Info), new Version(0, 1, 0), "windows", "x64");
        Assert.Empty(catalog.Entries);
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/PowerTrove.Desktop/PowerTrove.Desktop.csproj"));
        Assert.Contains(project.Descendants("Content"), element =>
            (string?)element.Attribute("Include") == "catalog.json" && (string?)element.Element("CopyToOutputDirectory") == "PreserveNewest");
    }
}
