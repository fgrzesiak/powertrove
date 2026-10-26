using System.Collections.Generic;
using System.Linq;
using ContextBar.Abstractions.Packs;
using ContextBar.Abstractions.Office;
using ContextBar.Application.Configuration;
using ContextBar.Infrastructure.Files;
using ContextBar.Pack.Images;
using ContextBar.Pack.Media;
using ContextBar.Pack.Office;
using ContextBar.Pack.Tables;
using ContextBar.Pack.Data;

namespace ContextBar.Desktop;

/// <summary>Only compiled for the Full profile.</summary>
public static partial class BuiltInActionPacks
{
    static partial void AddOptionalPackFactories(
        List<IPackFactory> factories,
        PackHostServices host,
        IReadOnlyList<SavedImageExportPreset>? imagePresets)
    {
        var presets = (imagePresets ?? new List<SavedImageExportPreset>())
            .Select(p => ImagesPack.FromPreset(p.Id, p.Title, p.Description, p.Format,
                p.Quality, p.MaxWidth, p.MaxHeight, p.StripMetadata)).ToArray();
        host.Add(new ImagesPackOptions(presets));
        host.Add<IOfficeInputValidator>(new OfficeInputValidator());
        host.Add<IOfficeConverter>(OfficePackFactory.CreateConverter(host.GetService<IOfficeInputValidator>()!));
        // Preserve existing Full registration order.
        factories.Insert(1, new ImagesPackFactory());
        factories.Insert(3, new OfficePackFactory());
        factories.Insert(4, new MediaPackFactory());
        factories.Insert(5, new TablesPackFactory());
        factories.Insert(6, new DataPackFactory());
    }
}
