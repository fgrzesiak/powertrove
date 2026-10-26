using PowerTrove.Abstractions.Recipes;
using PowerTrove.Core.Recipes;
using PowerTrove.Core;
using PowerTrove.Core.Workflows;
using System.Text.Json;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Pdf;
using PowerTrove.Pack.Office;
using PowerTrove.Pack.Tables;
using PowerTrove.Pack.Data;
using PowerTrove.Abstractions.Packs;


namespace PowerTrove.Architecture.Tests;

/// <summary>Catalog-wide guarantees for all built-in packs (ids, parameters, manifests).</summary>
public class ActionPackCatalogTests
{
    private class MockRecipePack : IActionPack
    {
        public ActionPackInfo Info { get; } = new ActionPackInfo("powertrove.recipes", "Recipes", "0.1.0", "PowerTrove");
        public IReadOnlyList<IAction> Actions { get; }

        public MockRecipePack(IWorkflowRunner workflowRunner, FileActionServices services)
        {
            var recipe = new RecipeDefinition(
                "powertrove.recipe.images-for-email",
                "Prepare images for email",
                "Resizes images to max 1600 px, compresses them to 75% quality, and zips the result.",
                SelectionRequirements.Multiple(new[] { "image" }),
                new List<RecipeStep>
                {
                    new RecipeStep("powertrove.image.resize", new Dictionary<string, string> { { "MaxDimension", "1600" }, { "Enlarge", "false" } }),
                    new RecipeStep("powertrove.image.compress", new Dictionary<string, string> { { "Quality", "75" } }),
                    new RecipeStep("powertrove.files.zip", new Dictionary<string, string>())
                }
            );

            Actions = new IAction[] { new RecipeAction(recipe, workflowRunner, services) };
        }
    }

    private static IReadOnlyList<IActionPack> CreatePacks()
    {
        var services = new FileActionServices(new PhysicalOutputFileSystem(), new OutputPlanner(new PhysicalOutputFileSystem()), new ExtensionOnlyTypeVerifier());
        var actionRegistry = new ActionRegistry();
        var workflowRunner = new WorkflowRunner(actionRegistry);
        var host = new PackHostServices(new Version(0, 1, 0), services)
            .Add(new CompositeImageEngine(Array.Empty<IImageEngine>()))
            .Add(new CompositeMediaEngine(Array.Empty<IMediaEngine>()));
        var packs = PackFactoriesTests.Factories().Select(f => f.Create(host))
            .Concat(new IActionPack[] { new MockRecipePack(workflowRunner, services) }).ToArray();

        foreach (var pack in packs)
        {
            foreach (var action in pack.Actions)
                actionRegistry.Register(action);
        }

        return packs;
    }

    [Fact]
    public void ActionIds_AreUniqueAndNamespaced()
    {
        var ids = CreatePacks().SelectMany(p => p.Actions).Select(a => a.Descriptor.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.Matches("^powertrove\\.[a-z]+\\.[a-z0-9-]+$", id));
    }

    [Fact]
    public void ActionsAcceptingFiles_DeclareMatchingSelectionRequirements()
    {
        var fileActions = CreatePacks()
            .SelectMany(p => p.Actions)
            .Where(a => a.Descriptor.Accepts.Contains(ContentKind.Files));

        foreach (var action in fileActions)
        {
            Assert.True(action.Descriptor.Selection is not null, $"{action.Descriptor.Id} declares no selection requirements.");

            if (action is PowerTrove.Abstractions.Files.FileActionBase<PowerTrove.Abstractions.ActionParameters> fileActionBaseObj)
            {
                // This branch shouldn't hit since FileActionBase is generic on TOptions and we can't easily cast to it safely without reflection.
                // Actually we can use reflection:
            }

            var type = action.GetType();
            var minInputCountProp = type.GetProperty("MinimumInputCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (minInputCountProp != null)
            {
                int minInputCount = (int)minInputCountProp.GetValue(action)!;
                Assert.Equal(minInputCount, action.Descriptor.Selection!.MinItems);
            }
        }
    }

    [Fact]
    public void FileDataMilestoneActions_AreRegistered()
    {
        var ids = CreatePacks().SelectMany(p => p.Actions).Select(a => a.Descriptor.Id).ToHashSet();
        foreach (string id in new[] { "powertrove.files.rename", "powertrove.files.add-to-zip", "powertrove.files.compare-text", "powertrove.files.sort-by-type", "powertrove.data.xlsx-to-csv", "powertrove.data.xlsx-to-json", "powertrove.data.csv-to-xlsx", "powertrove.table.combine-workbook" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void AdvancedMediaActions_AreRegisteredWithoutOptionalEngines()
    {
        var ids = CreatePacks().Single(p => p.Info.Id == "powertrove.media").Actions.Select(a => a.Descriptor.Id).ToHashSet();
        foreach (string id in new[] { "powertrove.media.info", "powertrove.media.trim", "powertrove.video.compress", "powertrove.media.merge", "powertrove.video.to-gif", "powertrove.video.extract-frames", "powertrove.audio.normalize", "powertrove.video.rotate" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void PackIds_AreUnique()
    {
        var ids = CreatePacks().Select(p => p.Info.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void StableIds_UsedBySettingsAndAliases_StillExist()
    {
        // Preferences, history, pins and aliases.json refer to these ids.
        string[] stable =
        {
            "powertrove.office.word-to-pdf", "powertrove.office.excel-to-pdf", "powertrove.office.powerpoint-to-pdf", "powertrove.office.pdf-to-word",
            "powertrove.files.count",
            "powertrove.image.convert-webp", "powertrove.image.convert-avif", "powertrove.image.resize", "powertrove.image.remove-metadata",
            "powertrove.audio.convert-mp3", "powertrove.audio.convert-flac", "powertrove.audio.convert-wav",
            "powertrove.video.convert-mp4", "powertrove.video.convert-webm", "powertrove.video.extract-audio", "powertrove.video.remove-audio", "powertrove.video.embed-subtitles", "powertrove.video.slideshow", "powertrove.video.still-image",
            "powertrove.table.export-csv", "powertrove.table.export-json", "powertrove.table.export-markdown", "powertrove.table.export-sql-insert",
            "powertrove.recipe.images-for-email", "powertrove.pdf.header-footer", "powertrove.pdf.stamp-selected-image", "powertrove.pdf.remove-blank", "powertrove.pdf.to-text", "powertrove.pdf.edit-metadata", "powertrove.pdf.repair", "powertrove.pdf.extract-images"
        };
        var ids = CreatePacks().SelectMany(p => p.Actions).Select(a => a.Descriptor.Id).ToHashSet();
        Assert.All(stable, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void FileChangingActions_RequirePreview()
    {
        foreach (var action in CreatePacks().SelectMany(p => p.Actions).Where(a => a.Descriptor.ChangesFiles))
            Assert.True(action.Descriptor.RequiresPreview, $"{action.Descriptor.Id} changes files and must require a preview.");
    }

    [Fact]
    public void ImageToolIds_AreRegisteredEvenWithoutAnOptionalRasterCodec()
    {
        var ids = CreatePacks().Single(p => p.Info.Id == "powertrove.images").Actions.Select(a => a.Descriptor.Id).ToHashSet();
        foreach (string suffix in new[] { "crop-aspect", "pad", "target-size", "optimize-png", "grayscale", "stitch", "collage", "info", "to-ico", "convert-other" })
            Assert.Contains("powertrove.image." + suffix, ids);
    }

    [Fact]
    public void RemainingImageToolIds_AreRegisteredWithoutPlatformPorts()
    {
        var ids = CreatePacks().Single(p => p.Info.Id == "powertrove.images").Actions.Select(a => a.Descriptor.Id).ToHashSet();
        foreach (string suffix in new[] { "rotate-right", "rotate-left", "rotate-180", "flip-horizontal", "flip-vertical", "auto-rotate", "watermark-text", "watermark-image", "to-gif", "to-text", "blur-faces", "remove-location" })
            Assert.Contains("powertrove.image." + suffix, ids);
    }

    [Fact]
    public async Task OtherImageFormats_RankBelowDedicatedConverters()
    {
        var fs = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier());
        var engines = new CompositeImageEngine(new IImageEngine[] { new CatalogImageEngine() });
        var pack = new ImagesPack(engines, services, rasterCodec: new CatalogRasterCodec());
        var registry = new ActionRegistry();
        registry.Register(pack.Actions.Single(a => a.Descriptor.Id == "powertrove.image.convert-jpeg"));
        registry.Register(pack.Actions.Single(a => a.Descriptor.Id == "powertrove.image.convert-other"));
        var results = await new PowerTrove.Core.Search.ActionSearchEngine(registry).SearchAsync("", Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "catalog.png")));
        Assert.Equal("powertrove.image.convert-jpeg", results[0].Action.Descriptor.Id);
        Assert.True(results[0].Rank > results[1].Rank);
    }

    private sealed class CatalogImageEngine : IImageEngine
    {
        public string Name => "catalog"; public bool CanPreserveMetadata => false;
        public bool CanDecode(string f) => true; public bool CanEncode(string f) => true;
        public Task TransformAsync(Stream s, Stream d, ImageTransformRequest r, CancellationToken ct) => throw new InvalidOperationException("Ranking must not decode images.");
    }
    private sealed class CatalogRasterCodec : IImageRasterCodec
    {
        public bool CanDecode(string f) => true; public bool CanEncode(string f) => true;
        public Task<ImageRaster> DecodeAsync(Stream s, string f, long max, CancellationToken ct) => throw new InvalidOperationException("Ranking must not decode images.");
        public Task EncodeAsync(ImageRaster r, Stream s, string f, int quality, CancellationToken ct) => throw new InvalidOperationException("Ranking must not encode images.");
    }

    [Fact]
    public void ParameterDefaults_AreValid()
    {
        foreach (var parameter in CreatePacks().SelectMany(p => p.Actions).SelectMany(a => a.Descriptor.Parameters ?? Array.Empty<ActionParameterDefinition>()))
        {
            Assert.False(string.IsNullOrWhiteSpace(parameter.Title));
            switch (parameter.Kind)
            {
                case ActionParameterKind.Integer:
                    int value = int.Parse(parameter.DefaultValue!);
                    Assert.InRange(value, parameter.Minimum ?? int.MinValue, parameter.Maximum ?? int.MaxValue);
                    break;
                case ActionParameterKind.Choice:
                    Assert.Contains(parameter.Choices!, c => c.Value == parameter.DefaultValue);
                    break;
                case ActionParameterKind.Boolean:
                    Assert.Contains(parameter.DefaultValue, new[] { "true", "false" });
                    break;
            }
        }
    }

    [Fact]
    public void Manifests_AreGeneratedAndRoundTrip()
    {
        foreach (var pack in CreatePacks())
        {
            var manifest = ActionPackManifest.Create(pack);
            string json = ActionPackManifest.ToJson(manifest);
            var parsed = JsonSerializer.Deserialize<ActionManifest>(json)!;

            Assert.Equal(pack.Info.Id, parsed.Id);
            Assert.Equal(pack.Actions.Count, parsed.Actions.Count);
            Assert.All(parsed.Actions, a => Assert.Contains("files", a.InputKinds.Concat(new[] { "files", "table" })));
        }
    }
}
