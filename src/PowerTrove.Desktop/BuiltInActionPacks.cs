using System;
using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Application.Configuration;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Platform.Windows.Imaging;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Pdf;
using PowerTrove.Abstractions.Recipes;
using PowerTrove.Core.Workflows;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Ocr;

namespace PowerTrove.Desktop;

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
