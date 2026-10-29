using System.Globalization;
using FileKit.Imaging.Raster;

namespace PowerTrove.Desktop;

internal static class ImageEditorInput
{
    public const string ActionId = "powertrove.image.edit";
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".avif", ".heic", ".heif", ".svg" };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
        { ActionId, "powertrove.image.pad", "powertrove.image.crop-aspect",
          "powertrove.image.rotate-right", "powertrove.image.rotate-left", "powertrove.image.rotate-180", "powertrove.image.svg-to-png" };

    public static bool CanEdit(ContextSnapshot snapshot) => snapshot.Status == ContextStatus.Available && snapshot.Content is FileSelection { Files.Count: 1 } files
        && files.Files[0].IsAvailableLocally && files.Files[0].IsAccessible && !files.Files[0].IsDirectory && Sources.Contains(files.Files[0].Extension ?? string.Empty);
    public static bool CanOffer(string id, ContextSnapshot snapshot) => Actions.Contains(id) && CanEdit(snapshot);
    public static bool OpensDirectly(string id, ContextSnapshot snapshot)
    {
        if (!CanEdit(snapshot)) return false;
        bool svg = string.Equals(((FileSelection)snapshot.Content).Files[0].Extension, ".svg", StringComparison.OrdinalIgnoreCase);
        return id == ActionId || (!svg &&
            id is "powertrove.image.rotate-right" or "powertrove.image.rotate-left" or "powertrove.image.rotate-180");
    }

    public static DictionaryParameters InitialParameters(ActionDescriptor descriptor, IReadOnlyDictionary<string, string>? saved) =>
        new((descriptor.Parameters ?? []).ToDictionary(p => p.Name,
            p => descriptor.CanRememberParameters && saved is not null && saved.TryGetValue(p.Name, out var value)
                ? value : p.DefaultValue ?? string.Empty, StringComparer.OrdinalIgnoreCase));

    public static ImageCanvasLayout Seed(int width, int height, string origin, IReadOnlyDictionary<string, string> values)
    {
        int Read(string name, int fallback = 0) => values.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        bool Flag(string name) => values.TryGetValue(name, out var text) && bool.TryParse(text, out bool value) && value;
        var natural = ImageCanvasLayout.Resolve(width, height);
        if (origin == ActionId) return ImageCanvasLayout.Resolve(width, height, Read("CanvasWidth"), Read("CanvasHeight"), Read("ImageWidth"), Read("ImageHeight"), Read("OffsetX"), Read("OffsetY"), Read("Angle"));
        if (origin is "powertrove.image.resize" or "powertrove.image.svg-to-png")
        {
            bool svg = origin.EndsWith("svg-to-png", StringComparison.Ordinal);
            int cw = Math.Clamp(Read(svg ? "Width" : "MaxWidth", natural.CanvasWidth), 1, 10000);
            int ch = Math.Clamp(Read(svg ? "Height" : "MaxHeight", natural.CanvasHeight), 1, 10000);
            var fit = ImageCanvasLayout.Fit(width, height, cw, ch);
            if (!svg && !Flag("AllowUpscale") && fit.ImageWidth > width)
                fit = ImageCanvasLayout.Resolve(width, height);
            return svg && Flag("ExactCanvas") ? fit : new(fit.ImageWidth, fit.ImageHeight, fit.ImageWidth, fit.ImageHeight);
        }
        if (origin.StartsWith("powertrove.image.rotate-", StringComparison.Ordinal))
        {
            int angle = origin.EndsWith("right", StringComparison.Ordinal) ? 90 : origin.EndsWith("left", StringComparison.Ordinal) ? -90 : -180;
            return natural with { CanvasWidth = Math.Abs(angle) == 90 ? natural.CanvasHeight : natural.CanvasWidth,
                CanvasHeight = Math.Abs(angle) == 90 ? natural.CanvasWidth : natural.CanvasHeight, Angle = angle };
        }
        if (origin == "powertrove.image.pad")
        {
            int side = Math.Max(natural.CanvasWidth, natural.CanvasHeight);
            bool border = values.TryGetValue("Mode", out var mode) && mode == "border";
            int padding = Math.Clamp(Read("BorderPx", 20), 1, 2000);
            var square = natural with { CanvasWidth = border ? natural.CanvasWidth + 2 * padding : side,
                CanvasHeight = border ? natural.CanvasHeight + 2 * padding : side };
            try { square.Validate(); return square; } catch (RasterException) { return natural; }
        }
        if (origin == "powertrove.image.crop-aspect")
        {
            string ratio = values.TryGetValue("Ratio", out var value) ? value : "1:1";
            if (ratio == "custom") ratio = values.TryGetValue("CustomRatio", out value) ? value : "1:1";
            var parts = ratio.Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rw)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double rh) && double.IsFinite(rw) && double.IsFinite(rh) && rw > 0 && rh > 0)
            {
                double r = rw / rh;
                int cw = Math.Max(1, (int)Math.Floor(Math.Min(natural.CanvasWidth, natural.CanvasHeight * r)));
                int ch = Math.Max(1, (int)Math.Floor(Math.Min(natural.CanvasHeight, natural.CanvasWidth / r)));
                var cropped = natural with { CanvasWidth = cw, CanvasHeight = ch };
                if (values.TryGetValue("Anchor", out var anchor))
                    cropped = cropped with { OffsetX = anchor == "left" ? (natural.ImageWidth - cw) / 2 : anchor == "right" ? -(natural.ImageWidth - cw) / 2 : 0,
                        OffsetY = anchor == "top" ? (natural.ImageHeight - ch) / 2 : anchor == "bottom" ? -(natural.ImageHeight - ch) / 2 : 0 };
                return cropped;
            }
        }
        return natural;
    }

    public static DictionaryParameters Parameters(ImageCanvasLayout l) => new(new Dictionary<string, string> {
        ["CanvasWidth"] = l.CanvasWidth.ToString(CultureInfo.InvariantCulture), ["CanvasHeight"] = l.CanvasHeight.ToString(CultureInfo.InvariantCulture),
        ["ImageWidth"] = l.ImageWidth.ToString(CultureInfo.InvariantCulture), ["ImageHeight"] = l.ImageHeight.ToString(CultureInfo.InvariantCulture),
        ["OffsetX"] = l.OffsetX.ToString(CultureInfo.InvariantCulture), ["OffsetY"] = l.OffsetY.ToString(CultureInfo.InvariantCulture), ["Angle"] = l.Angle.ToString(CultureInfo.InvariantCulture) });
}
