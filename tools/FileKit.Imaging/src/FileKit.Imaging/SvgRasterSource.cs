using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FileKit.Imaging;

public sealed class SvgInputException(string message) : Exception(message);

/// <summary>A validated static SVG subset: shapes, text and gradients, with no external
/// resources, stylesheets, scripting, animation, embedded images or recursive references.</summary>
public sealed partial class SvgRasterSource
{
    private SvgRasterSource(string xml, int width, int height, double viewBoxWidth, double viewBoxHeight)
    { Xml = xml; Width = width; Height = height; ViewBoxWidth = viewBoxWidth; ViewBoxHeight = viewBoxHeight; }
    public string Xml { get; }
    public int Width { get; }
    public int Height { get; }
    public double ViewBoxWidth { get; }
    public double ViewBoxHeight { get; }
    private const int MaxBytes = 1024 * 1024;
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    { "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "linearGradient", "radialGradient", "stop", "title", "desc" };
    private static readonly HashSet<string> Presentation = new(StringComparer.Ordinal)
    { "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "opacity", "display", "visibility", "font-family", "font-size", "font-style", "font-weight", "text-anchor", "dominant-baseline", "stop-color", "stop-opacity" };
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    { "id", "viewBox", "width", "height", "version", "preserveAspectRatio", "transform", "d", "points", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "dx", "dy", "rotate", "textLength", "lengthAdjust", "gradientUnits", "gradientTransform", "spreadMethod", "offset", "fx", "fy", "fr", "style" };
    public static Task<SvgRasterSource> ReadAsync(Stream input, int maxWidth, int maxHeight, CancellationToken ct = default) =>
        ReadAsync(input, maxWidth, maxHeight, false, ct);

    public static async Task<SvgRasterSource> ReadAsync(Stream input, int maxWidth, int maxHeight, bool exactCanvas, CancellationToken ct = default)
    {
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxBytes) throw new SvgInputException("SVG byte limit exceeded.");
            await memory.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        byte[] bytes = memory.ToArray();
        return await Task.Run(() => Prepare(bytes, maxWidth, maxHeight, exactCanvas, ct), ct).ConfigureAwait(false);
    }
    public static SvgRasterSource Prepare(byte[] bytes, int maxWidth, int maxHeight, CancellationToken ct = default) =>
        Prepare(bytes, maxWidth, maxHeight, false, ct);

    public static SvgRasterSource Prepare(byte[] bytes, int maxWidth, int maxHeight, bool exactCanvas, CancellationToken ct = default)
    {
        if (bytes.Length is < 1 or > MaxBytes || maxWidth is < 1 or > 10000 || maxHeight is < 1 or > 10000 || (long)maxWidth * maxHeight > 40000000)
            throw new SvgInputException("SVG size limit exceeded.");
        try
        {
            using var input = new MemoryStream(bytes);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes, MaxCharactersFromEntities = 0 });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XNamespace svg = "http://www.w3.org/2000/svg";
            if (document.Root?.Name != svg + "svg" || document.DescendantNodes().Any(n => n is XProcessingInstruction or XDocumentType)) throw new SvgInputException("Expected a static SVG root.");
            int count = 0;
            foreach (var element in document.Root.DescendantsAndSelf())
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 5000 || element.Ancestors().Count() > 64 || element.Name.Namespace != svg || !Elements.Contains(element.Name.LocalName))
                    throw new SvgInputException("Unsupported or excessive SVG elements.");
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration) { if (attribute.Value != svg.NamespaceName) throw new SvgInputException("Unsupported SVG namespace."); continue; }
                    string name = attribute.Name.LocalName;
                    if (attribute.Name.NamespaceName.Length != 0 || (!Attributes.Contains(name) && !Presentation.Contains(name))) throw new SvgInputException("Unsupported SVG attribute.");
                    if (name == "style")
                    {
                        foreach (string declaration in attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            int colon = declaration.IndexOf(':');
                            if (colon < 1 || !Presentation.Contains(declaration[..colon].Trim())) throw new SvgInputException("Unsupported SVG style.");
                            ValidateValue(declaration[..colon].Trim(), declaration[(colon + 1)..].Trim());
                        }
                    }
                    else ValidateValue(name, attribute.Value);
                }
            }
            var root = document.Root;
            double x = 0, y = 0, width, height;
            string? viewBox = (string?)root.Attribute("viewBox");
            if (viewBox is not null)
            {
                string[] values = viewBox.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
                if (values.Length != 4) throw new SvgInputException("Invalid viewBox.");
                x = Number(values[0]); y = Number(values[1]); width = Number(values[2]); height = Number(values[3]);
            }
            else
            {
                width = Length((string?)root.Attribute("width")); height = Length((string?)root.Attribute("height"));
                root.SetAttributeValue("viewBox", string.Create(CultureInfo.InvariantCulture, $"0 0 {width} {height}"));
            }
            if (width < 1e-6 || height < 1e-6) throw new SvgInputException("Invalid SVG dimensions.");
            double scale = Math.Min(maxWidth / width, maxHeight / height);
            int outputWidth = exactCanvas ? maxWidth : Math.Max(1, (int)Math.Round(width * scale));
            int outputHeight = exactCanvas ? maxHeight : Math.Max(1, (int)Math.Round(height * scale));
            // Keep the viewBox and fit the complete artwork into a centered viewport.
            // Override source "none"/"slice" alignment so this mode never stretches or crops.
            if (exactCanvas) root.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");
            root.SetAttributeValue("width", outputWidth.ToString(CultureInfo.InvariantCulture));
            root.SetAttributeValue("height", outputHeight.ToString(CultureInfo.InvariantCulture));
            return new(document.ToString(SaveOptions.DisableFormatting), outputWidth, outputHeight, width, height);
        }
        catch (XmlException) { throw new SvgInputException("Invalid or unsafe SVG XML."); }
    }
    private static double Number(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || Math.Abs(number) > 1e9)
            throw new SvgInputException("Invalid SVG number.");
        return number;
    }
    private static double Length(string? value) => value is null ? throw new SvgInputException("SVG needs a viewBox or pixel dimensions.") : Number(value.EndsWith("px", StringComparison.Ordinal) ? value[..^2] : value);
    private static void ValidateValue(string name, string value)
    {
        if (value.Length > 65536 || value.Any(c => c is '\\' or '@' or '<' or '>' || c < 32 && !char.IsWhiteSpace(c)) || value.Contains("://", StringComparison.Ordinal) || value.Contains("data:", StringComparison.OrdinalIgnoreCase) || value.Contains("file:", StringComparison.OrdinalIgnoreCase))
            throw new SvgInputException("External or unsupported SVG value.");
        if (value.Contains("url", StringComparison.OrdinalIgnoreCase) && !Fragment().IsMatch(value.Trim())) throw new SvgInputException("Only local gradient references are supported.");
        if (name == "font-family" && !FontFamily().IsMatch(value)) throw new SvgInputException("Unsupported font family.");
    }
    [GeneratedRegex(@"^url\(#[A-Za-z_][A-Za-z0-9_.-]*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Fragment();
    [GeneratedRegex("^[A-Za-z0-9 ,\"'-]+$", RegexOptions.CultureInvariant)] private static partial Regex FontFamily();
}
