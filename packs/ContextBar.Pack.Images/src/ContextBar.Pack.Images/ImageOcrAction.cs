using System.Text;
using ContextBar.Abstractions.Ocr;

namespace ContextBar.Pack.Images;

public sealed record ImageOcrOptions(bool ToFile, string? Language);

/// <summary>Chooses two SDK file pipelines; clipboard payload is owned by the individual invocation.</summary>
public sealed class ImageOcrAction : IAction
{
    private readonly FileActionServices _services;
    private readonly IImageRasterCodec? _codec;
    private readonly ITextRecognizer? _recognizer;
    public ImageOcrAction(FileActionServices services, IImageRasterCodec? codec, ITextRecognizer? recognizer)
    {
        _services = services; _codec = codec; _recognizer = recognizer;
        var languages = new List<ActionParameterChoice> { new("auto", ExtraImageSupport.Text("Choice_auto")) };
        languages.AddRange((recognizer?.AvailableLanguages ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Select(l => new ActionParameterChoice(l, l)));
        Descriptor = ExtraImageSupport.Descriptor("to-text", "Ocr", new[] {
            ExtraImageSupport.Choice("Output", "clipboard", "clipboard", "file"),
            ActionParameterDefinition.Choice("Language", ExtraImageSupport.Text("Language"), "auto", languages) });
    }
    public ActionDescriptor Descriptor { get; }
    private ImageOcrOptions Options(ActionParameters p)
    {
        var v = ActionParameterValues.From(p, Descriptor.Parameters); var language = v.GetChoice("Language");
        return new(v.GetChoice("Output") == "file", language == "auto" ? null : language);
    }
    private string? Unavailable => _codec is null || _recognizer is null || _recognizer.AvailableLanguages.Count == 0 ? ExtraImageSupport.Text("OcrUnavailable") : null;
    public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken ct) => new OcrFiles(this).MatchAsync(context, ct);
    public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters p, CancellationToken ct) => Options(p).ToFile
        ? new OcrFiles(this).PreviewAsync(context, p, ct) : new OcrClipboard(this, _ => { }).PreviewAsync(context, p, ct);
    public async Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters p, IProgress<ActionProgress>? progress, CancellationToken ct)
    {
        if (Options(p).ToFile) return await new OcrFiles(this).ExecuteAsync(context, p, progress, ct).ConfigureAwait(false);
        string? content = null;
        var receipt = await new OcrClipboard(this, text => content = text).ExecuteAsync(context, p, progress, ct).ConfigureAwait(false);
        return receipt.IsSuccess && !receipt.IsCanceled ? receipt with { ClipboardText = content } : receipt;
    }
    private async Task<string> RecognizeAsync(FileWorkItem item, ImageOcrOptions o, CancellationToken ct)
    {
        var raster = await RasterSupport.DecodeAsync(_services, _codec!, item, ct).ConfigureAwait(false);
        var lines = await _recognizer!.RecognizeAsync(raster.Width, raster.Height, raster.Bgra32, o.Language, ct).ConfigureAwait(false);
        if (lines.Count > 50000 || lines.Sum(l => (long)l.Text.Length) + Math.Max(0, lines.Count - 1) * Environment.NewLine.Length > 2_000_000)
            throw new FileProcessingException(ExtraImageSupport.Text("OcrTooLarge"));
        return string.Join(Environment.NewLine, lines.Select(l => l.Text));
    }
    private sealed class OcrFiles(ImageOcrAction owner) : PerFileAction<ImageOcrOptions>(owner._services)
    {
        public override ActionDescriptor Descriptor => owner.Descriptor;
        protected override FileInputFilter Input => RasterSupport.Filter;
        protected override string? UnavailableReason => owner.Unavailable;
        protected override ImageOcrOptions ReadOptions(ActionParameterValues v) { string language = v.GetChoice("Language"); return new(true, language == "auto" ? null : language); }
        protected override string DescribeOperation(int n, ImageOcrOptions o) => ExtraImageSupport.Count("OcrFileCount", n);
        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile f, ImageOcrOptions o) { yield return new(Path.GetFileNameWithoutExtension(f.Name), ".txt"); }
        protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, ImageOcrOptions o) => ExtraImageSupport.AnimationWarning(f);
        protected override async Task ProcessAsync(FileWorkItem item, ImageOcrOptions o, FileOutputWriter output, CancellationToken ct)
        {
            string text = await owner.RecognizeAsync(item, o, ct).ConfigureAwait(false);
            await output.WriteFileAsync(item.Directory, item.BaseName, ".txt", async (stream, token) =>
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }, ct, allowEmpty: true).ConfigureAwait(false);
        }
    }
    private sealed class OcrClipboard(ImageOcrAction owner, Action<string> receive) : CombineFilesAction<ImageOcrOptions>(owner._services)
    {
        public override ActionDescriptor Descriptor => owner.Descriptor with { ChangesFiles = false, RequiresPreview = false };
        protected override FileInputFilter Input => RasterSupport.Filter;
        protected override int MinimumInputs => 1;
        protected override string? UnavailableReason => owner.Unavailable;
        protected override ImageOcrOptions ReadOptions(ActionParameterValues v) { string language = v.GetChoice("Language"); return new(false, language == "auto" ? null : language); }
        protected override string DescribeOperation(int n, ImageOcrOptions o) => ExtraImageSupport.Count("OcrClipboardCount", n);
        protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> f, ImageOcrOptions o) => ExtraImageSupport.AnimationWarning(f);
        protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> f, ImageOcrOptions o) => throw new InvalidOperationException();
        protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> f, ImageOcrOptions o) => Array.Empty<string>();
        private async Task ReadAsync(IReadOnlyList<FileWorkItem> items, ImageOcrOptions o, IProgress<double>? progress, CancellationToken ct)
        {
            var result = new StringBuilder();
            foreach (var item in items)
            {
                string text = await owner.RecognizeAsync(item, o, ct).ConfigureAwait(false);
                if (result.Length + text.Length + Environment.NewLine.Length > 2_000_000) throw new FileProcessingException(ExtraImageSupport.Text("OcrTooLarge"));
                if (result.Length > 0) result.AppendLine(); result.Append(text); progress?.Report((double)(item.Index + 1) / items.Count);
            }
            ct.ThrowIfCancellationRequested();
            if (result.Length == 0) throw new FileSkippedException(ExtraImageSupport.Text("NoText"));
            receive(result.ToString());
        }
        protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, ImageOcrOptions o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct) => ReadAsync(items, o, progress, ct);
        protected override async Task ProcessWithStatisticsAsync(IReadOnlyList<FileWorkItem> items, ImageOcrOptions o, FileOutputWriter output, ExecutionStatistics stats, IProgress<double>? progress, CancellationToken ct)
        {
            await ReadAsync(items, o, progress, ct).ConfigureAwait(false);
            stats.CompletedWithoutOutput = true; stats.ResultMessage = ExtraImageSupport.Count("OcrResult", items.Count);
        }
    }
}
