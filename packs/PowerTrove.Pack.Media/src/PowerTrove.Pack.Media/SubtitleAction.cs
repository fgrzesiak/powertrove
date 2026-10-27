using System.Text;
using FileKit.Text;

namespace PowerTrove.Pack.Media;

public sealed class SubtitleAction(FileActionServices services, string kind) : PerFileAction<int>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.subtitle." + kind, SR.GetString("SubtitleTitle_" + kind), SR.GetString("SubtitleDesc_" + kind),
        new HashSet<ContentKind> { ContentKind.Files }, ["subtitle", "srt", "vtt", "captions", kind], true, true,
        Parameters: kind == "shift" ? [ActionParameterDefinition.Integer("OffsetMs", SR.GetString("SubtitleOffset"), 0, -86400000, 86400000, SR.GetString("SubtitleOffsetHelp"))] : [],
        Selection: SelectionRequirements.Any("subtitle"));
    protected override FileInputFilter Input => new(SR.GetString("SubtitleFiles"), kind == "srt-to-vtt" ? [".srt"] : kind == "vtt-to-srt" ? [".vtt"] : [".srt", ".vtt"], null);
    protected override int ReadOptions(ActionParameterValues values) => kind == "shift" ? values.GetInt32("OffsetMs") : 0;
    protected override string DescribeOperation(int count, int options) => SR.GetString("SubtitleDesc_" + kind);
    private SubtitleFormat Source(string extension) => string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase) ? SubtitleFormat.Srt : SubtitleFormat.Vtt;
    private SubtitleFormat Target(string extension) => kind == "srt-to-vtt" ? SubtitleFormat.Vtt : kind == "vtt-to-srt" ? SubtitleFormat.Srt : Source(extension);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, int options)
    { yield return new(Path.GetFileNameWithoutExtension(input.Name) + (kind == "shift" ? ".shifted" : ""), Target(input.Extension) == SubtitleFormat.Srt ? ".srt" : ".vtt"); }
    private async Task<(string Text, int Cues)> Transform(Stream input, string extension, int offset, CancellationToken ct)
    {
        var document = await Subtitles.ReadAsync(input, Source(extension), ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            if (kind == "shift") document = Subtitles.Shift(document, TimeSpan.FromMilliseconds(offset), ct);
            return (Subtitles.Write(document, Target(extension), kind != "shift", ct), document.Cues.Count);
        }, ct).ConfigureAwait(false);
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;
        int offset = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        try
        {
            long cues = 0;
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                await using var input = Services.FileSystem.OpenRead(file.Path!);
                cues += (await Transform(input, file.Extension, offset, ct).ConfigureAwait(false)).Cues;
            }
            return preview with { PreviewText = string.Format(SR.GetString("SubtitlePreview"), cues) };
        }
        catch (SubtitleException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("SubtitleInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.GetString("SubtitleReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, int offset, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            string result = (await Transform(input, item.File.Extension, offset, ct).ConfigureAwait(false)).Text;
            var planned = PlanOutputs(item.File, offset).Single();
            await output.WriteFileAsync(item.Directory, planned.BaseName, planned.Extension, async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(result.AsMemory(), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (SubtitleException ex) { throw new FileProcessingException(SR.GetString("SubtitleInvalid"), ex); }
    }
}
