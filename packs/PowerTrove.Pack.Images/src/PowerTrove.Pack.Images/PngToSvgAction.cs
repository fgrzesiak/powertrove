using System.Text;
using FileKit.Imaging;
using FileKit.Imaging.Raster;
using FileKit.Imaging.Vector;

namespace PowerTrove.Pack.Images;

public sealed class PngToSvgAction(FileActionServices services, IImageRasterCodec? codec) : PerFileAction<VectorOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.image.png-to-svg", SR.GetString("VectorTitle"), SR.GetString("VectorDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["png", "svg", "vector", "trace", "logo", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Choice("Mode", SR.GetString("VectorMode"), "color", [new("color", SR.GetString("VectorColor")), new("mono", SR.GetString("VectorMono"))]),
            ActionParameterDefinition.Choice("Detail", SR.GetString("VectorDetail"), "medium", [new("low", SR.GetString("VectorLow")), new("medium", SR.GetString("VectorMedium")), new("high", SR.GetString("VectorHigh"))]),
            ActionParameterDefinition.Integer("Threshold", SR.GetString("VectorThreshold"), 128, 0, 255).VisibleWhen("Mode", "mono")], Selection: SelectionRequirements.Any("image"));
    protected override FileInputFilter Input => new(SR.GetString("VectorFiles"), [".png"], [FileFormats.Png]);
    protected override string? UnavailableReason => codec is not null && codec.CanDecode(FileFormats.Png) ? null : SR.GetString("Raster_NoCodec");
    protected override VectorOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Mode") == "mono", values.GetChoice("Detail"), values.GetInt32("Threshold"));
    protected override string DescribeOperation(int count, VectorOptions options) => SR.GetString("VectorDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, VectorOptions options) => [SR.GetString("VectorLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, VectorOptions options) { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".svg"); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts)) await RasterSupport.ProbeAsync(Services, file, ct, RasterVectorizer.MaxSourcePixels).ConfigureAwait(false);
            return preview;
        }
        catch (FileProcessingException ex) { return preview with { IsBlocked = true, PreviewText = ex.Message }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, VectorOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await RasterSupport.ProbeAsync(Services, item.File, ct, RasterVectorizer.MaxSourcePixels).ConfigureAwait(false);
            await using var input = Services.FileSystem.OpenRead(item.Path);
            var image = await codec!.DecodeAsync(input, FileFormats.Png, RasterVectorizer.MaxSourcePixels, ct).ConfigureAwait(false);
            var vector = await Task.Run(() => RasterVectorizer.Trace(RasterSupport.Pixels(image), options, ct), ct).ConfigureAwait(false);
            await output.WriteFileAsync(item.Directory, item.BaseName, ".svg", (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(vector.Svg), token).AsTask(), ct).ConfigureAwait(false);
        }
        catch (RasterException ex) { throw new FileProcessingException(SR.GetString("VectorFailed"), ex); }
    }
}
