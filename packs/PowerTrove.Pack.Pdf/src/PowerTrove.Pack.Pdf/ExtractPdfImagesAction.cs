using System.Globalization;
using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record ExtractPdfImagesOptions(int MinSize)
{
    internal ImageExtractionReport Report { get; init; } = new();
}

internal sealed class ImageExtractionReport
{
    public int Extracted { get; set; }
    public int Unsupported { get; set; }
    public int TooSmall { get; set; }

    public string Describe()
    {
        if (Extracted + Unsupported + TooSmall == 0) return SR.GetString("ExtractPdfImages_NoImages");
        var parts = new List<string> { Count("Extracted", Extracted) };
        if (Unsupported > 0) parts.Add(Count("Unsupported", Unsupported));
        if (TooSmall > 0) parts.Add(Count("TooSmall", TooSmall));
        return string.Join(" ", parts);
    }

    private static string Count(string key, int count) => string.Format(CultureInfo.CurrentCulture,
        SR.GetString("ExtractPdfImages_" + key + (count == 1 ? "_One" : "_Other")), count);
}

/// <summary>Extracts embedded image XObjects into one transactional folder per input PDF.</summary>
public sealed class ExtractPdfImagesAction : PerFileAction<ExtractPdfImagesOptions>
{
    private const long MaxOutputBytes = 512L * 1024 * 1024;

    public ExtractPdfImagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.extract-images", SR.GetString("ExtractPdfImages_Title"), SR.GetString("ExtractPdfImages_Description"),
            new HashSet<ContentKind> { ContentKind.Files }, new[] { "pdf", "extract", "image", "jpeg", "png", "pictures" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[] { ActionParameterDefinition.Integer("MinSize", SR.GetString("ExtractPdfImages_MinSize"), 32, 1, 30_000) },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override ExtractPdfImagesOptions ReadOptions(ActionParameterValues values) => new(values.GetInt32("MinSize"));

    protected override string DescribeOperation(int inputCount, ExtractPdfImagesOptions options) => string.Format(
        CultureInfo.CurrentCulture, SR.GetString(inputCount == 1 ? "ExtractPdfImages_Preview_One" : "ExtractPdfImages_Preview_Other"),
        inputCount, options.MinSize);

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, ExtractPdfImagesOptions options)
    {
        yield return SR.GetString("ExtractPdfImages_Warning");
    }

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ExtractPdfImagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (images)", string.Empty, IsDirectory: true);
    }

    protected override async Task RunAsync(IReadOnlyList<SelectedFile> inputs, ExtractPdfImagesOptions options,
        FileOutputWriter output, List<ActionFailure> failures, ExecutionStatistics statistics,
        IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
    {
        // Reports belong to this invocation, never to the shared action instance.
        var runOptions = options with { Report = new ImageExtractionReport() };
        try
        {
            await base.RunAsync(inputs, runOptions, output, failures, statistics, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            statistics.ResultMessage = runOptions.Report.Describe();
        }
    }

    protected override string? DescribeResult(IReadOnlyList<string> outputs, ExecutionStatistics statistics) =>
        statistics.ResultMessage ?? base.DescribeResult(outputs, statistics);

    protected override async Task ProcessAsync(FileWorkItem item, ExtractPdfImagesOptions options,
        FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PdfImageInfo> images = Array.Empty<PdfImageInfo>();
        await PdfSupport.RunAsync(() =>
        {
            images = PdfImages.Enumerate(document, PdfImageExtractor.MaxImages + 1, cancellationToken);
            if (images.Count > PdfImageExtractor.MaxImages) throw new PdfLimitExceededException("Too many images to extract.");
        }, cancellationToken).ConfigureAwait(false);
        if (images.Count == 0) throw new FileSkippedException(SR.GetString("ExtractPdfImages_NoImages"));

        int extracted = 0, unsupported = 0, tooSmall = 0;
        long bytes = 0;
        try
        {
            await output.WriteDirectoryAsync(item.Directory, item.BaseName + " (images)", (directory, ct) => PdfSupport.RunAsync(() =>
            {
                foreach (var image in images)
                {
                    ct.ThrowIfCancellationRequested();
                    if (image.Width < options.MinSize || image.Height < options.MinSize) { tooSmall++; continue; }
                    var result = PdfImageExtractor.TryExtract(document, image, ct);
                    if (result is null) { unsupported++; continue; }
                    if (result.Data.LongLength > MaxOutputBytes - bytes)
                        throw new PdfLimitExceededException("Extracted images exceed the output limit.");
                    string name = item.BaseName + " - image " + (extracted + 1).ToString("D3", CultureInfo.InvariantCulture) + result.Extension;
                    using (var stream = Services.FileSystem.CreateFile(Path.Combine(directory, name)))
                    {
                        for (int offset = 0; offset < result.Data.Length; offset += 65_536)
                        {
                            ct.ThrowIfCancellationRequested();
                            stream.Write(result.Data, offset, Math.Min(65_536, result.Data.Length - offset));
                        }
                    }
                    bytes += result.Data.LongLength;
                    extracted++;
                }
                if (extracted == 0) throw new FileSkippedException(SR.GetString("ExtractPdfImages_NoEligibleImages"));
            }, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (FileSkippedException)
        {
            AddReport();
            throw;
        }
        AddReport();

        void AddReport()
        {
            options.Report.Extracted += extracted;
            options.Report.Unsupported += unsupported;
            options.Report.TooSmall += tooSmall;
        }
    }
}
