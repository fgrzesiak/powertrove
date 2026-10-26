using System.Reflection;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Testing;
using PowerTrove.Pack.Data;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Office;
using PowerTrove.Pack.Pdf;
using PowerTrove.Pack.Tables;

namespace PowerTrove.Architecture.Tests;

public sealed class PackFactoriesTests
{
    internal static IPackFactory[] Factories() => new IPackFactory[]
    {
        new FilesPackFactory(), new ImagesPackFactory(), new PdfPackFactory(), new OfficePackFactory(),
        new MediaPackFactory(), new TablesPackFactory(), new DataPackFactory()
    };
    internal static PackHostServices Host() => new(new Version(0, 1, 0), FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier()));

    [Fact]
    public void EveryPackAssembly_DeclaresExactlyOnePublicParameterlessFactory()
    {
        var assemblies = Factories().Select(f => f.GetType().Assembly).ToArray();
        Assert.Equal(assemblies.Length, assemblies.Distinct().Count());
        foreach (var assembly in assemblies)
        {
            var attribute = Assert.Single(assembly.GetCustomAttributes<ActionPackFactoryAttribute>());
            Assert.True(attribute.FactoryType.IsPublic);
            Assert.False(attribute.FactoryType.IsAbstract);
            Assert.True(typeof(IPackFactory).IsAssignableFrom(attribute.FactoryType));
            Assert.Equal(assembly, attribute.FactoryType.Assembly);
            Assert.NotNull(attribute.FactoryType.GetConstructor(Type.EmptyTypes));
            var factory = Assert.IsAssignableFrom<IPackFactory>(Activator.CreateInstance(attribute.FactoryType));
            Assert.Equal(factory.Info, factory.Create(Host()).Info);
        }
    }

    [Fact]
    public void FactoriesCoverEveryPackProject()
    {
        var root = FindRoot();
        var projects = Directory.GetDirectories(Path.Combine(root, "packs")).Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(projects, Factories().Select(f => f.GetType().Assembly.GetName().Name!.Replace(".Composition", "", StringComparison.Ordinal)).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void FactoryDescriptors_MatchPreRefactorConstructorGraphInOrder()
    {
        var host = Host();
        var image = new CompositeImageEngine(Array.Empty<IImageEngine>());
        var media = new CompositeMediaEngine(Array.Empty<IMediaEngine>());
        host.Add(image).Add(media);
        // Keep this explicit graph as the snapshot of the construction path before CB-155.
        IActionPack[] before =
        {
            new FilesPack(host.Files), new ImagesPack(image, host.Files), new PdfPack(host.Files), new OfficePack(host.Files),
            new MediaPack(media, host.Files), new TablesPack(host.Files.Planner, host.Files.FileSystem, host.Files), new DataPack(host.Files)
        };
        var after = Factories().Select(f => f.Create(host)).ToArray();
        Assert.Equal(before.Select(p => p.Info), after.Select(p => p.Info));
        Assert.Equal(before.SelectMany(p => p.Actions).Select(a => a.Descriptor.Id), after.SelectMany(p => p.Actions).Select(a => a.Descriptor.Id));
        for (int i = 0; i < before.Length; i++)
            Assert.Equal(ActionPackManifest.ToJson(ActionPackManifest.Create(before[i])), ActionPackManifest.ToJson(ActionPackManifest.Create(after[i])));
    }

    [Fact]
    public void ImageOptions_PreserveSavedPresetsAtTheEnd()
    {
        var preset = ImagesPack.FromPreset("test", "Saved", "Description", "webp", 80, 800, 600, true);
        var host = Host().Add(new ImagesPackOptions(new[] { preset }));
        var actions = new ImagesPackFactory().Create(host).Actions;
        Assert.Equal(preset.Id, actions[^1].Descriptor.Id);
        Assert.Single(actions.Where(a => a.Descriptor.Id == preset.Id));
    }

    [Fact]
    public void HostCapabilities_AreTypedAndDoNotSilentlyReplaceServices()
    {
        var engine = new CompositeImageEngine(Array.Empty<IImageEngine>());
        var host = Host().Add(engine);
        Assert.Same(engine, host.GetService<CompositeImageEngine>());
        Assert.Null(host.GetService<IImageRasterCodec>());
        Assert.Throws<ArgumentException>(() => host.Add(engine));
    }

    internal static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerTrove.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
