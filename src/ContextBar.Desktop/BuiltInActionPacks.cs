using System;
using System.Collections.Generic;
using System.Linq;
using ContextBar.Abstractions.Files;
using ContextBar.Application.Configuration;
using ContextBar.Infrastructure.Files;
using ContextBar.Platform.Windows.Imaging;
using ContextBar.Pack.Files;
using ContextBar.Pack.Pdf;
using ContextBar.Abstractions.Recipes;
using ContextBar.Core.Workflows;
using ContextBar.Abstractions.Packs;
using ContextBar.Abstractions.Ocr;

namespace ContextBar.Desktop;

/// <summary>The base profile always contains Files and PDF. Recipe actions remain host-owned.</summary>
public static partial class BuiltInActionPacks
{
    public static IReadOnlyList<IActionPack> Create(
        IReadOnlyList<SavedImageExportPreset>? imagePresets,
        IWorkflowRunner workflowRunner,
        IFileRenameService? renamer = null)
    {
        var services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
        var host = new PackHostServices(typeof(BuiltInActionPacks).Assembly.GetName().Version ?? new Version(0, 1, 0), services)
            .Add<ITextRecognizer>(new WindowsTextRecognizer());
        if (renamer is not null) host.Add<IFileRenameService>(renamer);
        var factories = new List<IPackFactory>
        {
            new FilesPackFactory(),
            new PdfPackFactory(new WindowsTextRecognizer()),
            new RecipePackFactory(workflowRunner)
        };
        AddOptionalPackFactories(factories, host, imagePresets);
        return factories.Select(factory => factory.Create(host)).ToArray();
    }

    // Excluded at compile time in Base. The partial invocation then compiles away.
    static partial void AddOptionalPackFactories(
        List<IPackFactory> factories,
        PackHostServices host,
        IReadOnlyList<SavedImageExportPreset>? imagePresets);
}
