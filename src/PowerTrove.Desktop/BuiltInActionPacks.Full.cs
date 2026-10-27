using System.Collections.Generic;
using System.Linq;
using PowerTrove.Abstractions.Packs;
using PowerTrove.Abstractions.Office;
using PowerTrove.Application.Configuration;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Media;
using PowerTrove.Pack.Office;
using PowerTrove.Pack.Tables;
using PowerTrove.Pack.Data;
using PowerTrove.Pack.EInvoice;

namespace PowerTrove.Desktop;

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
        factories.Insert(7, new EInvoicePackFactory());
    }
}
