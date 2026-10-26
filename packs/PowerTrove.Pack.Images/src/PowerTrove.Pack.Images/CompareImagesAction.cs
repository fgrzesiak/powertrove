using FileKit.Imaging.Raster;
using FileKit.Imaging;

namespace PowerTrove.Pack.Images;

public sealed class CompareImagesAction : CombineFilesAction<EmptyParameters>
{
    private readonly IImageRasterCodec _codec;

    public CompareImagesAction(FileActionServices services, IImageRasterCodec codec) : base(services)
    {
        _codec = codec;
        Descriptor = new ActionDescriptor(
            "contextbar.image.compare",
            SR.GetString("ImagesPack_Title_Compare"),
            SR.GetString("ImagesPack_Desc_Compare"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "image", "compare", "diff", "difference" },
            RequiresPreview: true,
            ChangesFiles: true,
            Selection: new SelectionRequirements(new HashSet<string> { "image" }, MinItems: 2, MaxItems: 2, IdealMinItems: 2, IdealMaxItems: 2, AllowOtherItems: false, FamilyLimits: new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase) { ["image"] = new(2, 2) }));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override int MinimumInputs => 2;
    protected override int MaximumInputs => 2;
    protected override FileInputFilter Input => RasterSupport.Filter;

    protected override EmptyParameters ReadOptions(ActionParameterValues v) => new EmptyParameters();

    protected override string DescribeOperation(int n, EmptyParameters o) => SR.GetString("ImagesPack_Desc_Compare");

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, EmptyParameters o) { yield break; }

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> f, EmptyParameters o) =>
        new($"{Path.GetFileNameWithoutExtension(f[0].Name)} vs {Path.GetFileNameWithoutExtension(f[1].Name)} (comparison)", ".png");

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;

        try
        {
            var files = OrderInputs(((FileSelection)context.Content).Files.Where(Input.Accepts));
            if (files.Count != 2) return preview with { IsBlocked = true };

            var info1 = await RasterSupport.ProbeAsync(Services, files[0], ct).ConfigureAwait(false);
            var info2 = await RasterSupport.ProbeAsync(Services, files[1], ct).ConfigureAwait(false);

            if (info1.DisplaySize.Width != info2.DisplaySize.Width || info1.DisplaySize.Height != info2.DisplaySize.Height)
            {
                var warnings = preview.Warnings.ToList();
                warnings.Add(SR.GetString("ImagesPack_Warn_CompareSizeMismatch"));
                preview = preview with { Warnings = warnings };
            }

            return preview;
        }
        catch (Exception ex) when (ex is RasterException or FileProcessingException or IOException or UnauthorizedAccessException)
        {
            return preview with { IsBlocked = true, RequiresConfirmation = false, PreviewText = ex is FileProcessingException ? ex.Message : ex is RasterException ? SR.GetString("Raster_TooManyPixels") : RasterMessages.DecodeFailed };
        }
    }


    protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, EmptyParameters o, FileOutputWriter output, ExecutionStatistics statistics, IProgress<double>? progress, CancellationToken ct)
    {
        if (items.Count != 2) throw new FileProcessingException(RasterMessages.DecodeFailed);

        var first = await RasterSupport.DecodeAsync(Services, _codec, items[0], ct).ConfigureAwait(false);
        var second = await RasterSupport.DecodeAsync(Services, _codec, items[1], ct).ConfigureAwait(false);

        var firstPixels = RasterSupport.Pixels(first);
        var secondPixels = RasterSupport.Pixels(second);

        int changedPixelsCount = 0;
        RasterImage diffPixels = null!;

        await Task.Run(() =>
        {
            if (firstPixels.Width != secondPixels.Width || firstPixels.Height != secondPixels.Height)
            {
                secondPixels = RasterOperations.ResizeBilinear(secondPixels, firstPixels.Width, firstPixels.Height, ct);
            }

            diffPixels = RasterOperations.Compare(firstPixels, secondPixels, out changedPixelsCount, ct);
        }, ct).ConfigureAwait(false);

        var sizes = new[] { new ImageSize(firstPixels.Width, firstPixels.Height), new ImageSize(secondPixels.Width, secondPixels.Height), new ImageSize(diffPixels!.Width, diffPixels.Height) };
        var layout = ImageLayouts.Stitch(sizes, horizontal: true, gap: 10, align: "center");

        var layers = new List<(RasterImage Image, int X, int Y)>
        {
            (firstPixels, layout.Images[0].X, layout.Images[0].Y),
            (secondPixels, layout.Images[1].X, layout.Images[1].Y),
            (diffPixels!, layout.Images[2].X, layout.Images[2].Y)
        };

        var canvas = await Task.Run(() => RasterOperations.Compose(layers, layout.Width, layout.Height, new BgraColor(255, 255, 255, 0), ct), ct).ConfigureAwait(false);

        string target = FileFormats.Png;
        if (!_codec.CanEncode(target)) throw new FileProcessingException(SR.GetString("Raster_NoCodec"));

        double percentage = (double)changedPixelsCount / (firstPixels.Width * firstPixels.Height) * 100.0;
        statistics.ResultMessage = string.Format(SR.GetString("ImagesPack_CompareReceipt"), percentage);

        await output.WriteFileAsync(items[0].Directory, $"{items[0].BaseName} vs {items[1].BaseName} (comparison)", ImageExportAction.ExtensionFor(target),
            async (stream, token) => await _codec.EncodeAsync(RasterSupport.Map(canvas), stream, target, 90, token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, EmptyParameters o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

}
