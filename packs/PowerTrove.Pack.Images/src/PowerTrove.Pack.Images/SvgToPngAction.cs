using FileKit.Imaging;

namespace PowerTrove.Pack.Images;

public interface ISvgRenderer
{
    Task RenderAsync(SvgRasterSource source, Stream destination, CancellationToken cancellationToken);
}

public sealed record SvgOptions(int Width, int Height);

public sealed class SvgToPngAction(FileActionServices services, ISvgRenderer? renderer) : PerFileAction<SvgOptions>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.image.svg-to-png", SR.GetString("SvgTitle"), SR.GetString("SvgDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["svg", "png", "render", "vector", "convert"], true, true,
        Parameters: [ActionParameterDefinition.Integer("Width", SR.GetString("SvgWidth"), 1024, 1, 10000), ActionParameterDefinition.Integer("Height", SR.GetString("SvgHeight"), 1024, 1, 10000)],
        Selection: SelectionRequirements.Any("image"));
    protected override FileInputFilter Input => new(SR.GetString("SvgFiles"), [".svg"], null);
    protected override string? UnavailableReason => renderer is null ? SR.GetString("SvgUnavailable") : null;
    protected override SvgOptions ReadOptions(ActionParameterValues values) => new(values.GetInt32("Width"), values.GetInt32("Height"));
    protected override string? ValidateOptions(SvgOptions options) => (long)options.Width * options.Height > 40000000 ? SR.GetString("SvgInvalid") : null;
    protected override string DescribeOperation(int count, SvgOptions options) => SR.GetString("SvgDescription");
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, SvgOptions options) => [SR.GetString("SvgLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, SvgOptions options) { yield return new(Path.GetFileNameWithoutExtension(file.Name), ".png"); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        try
        {
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            { await using var input = Services.FileSystem.OpenRead(file.Path!); await SvgRasterSource.ReadAsync(input, options.Width, options.Height, ct).ConfigureAwait(false); }
            return preview;
        }
        catch (SvgInputException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("SvgInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("SvgReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, SvgOptions options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            var source = await SvgRasterSource.ReadAsync(input, options.Width, options.Height, ct).ConfigureAwait(false);
            await output.WriteFileAsync(item.Directory, item.BaseName, ".png", (stream, token) => renderer!.RenderAsync(source, stream, token), ct).ConfigureAwait(false);
        }
        catch (SvgInputException ex) { throw new FileProcessingException(SR.GetString("SvgInvalid"), ex); }
    }
}
