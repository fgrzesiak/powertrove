using FileKit.Pdf;

namespace PowerTrove.Pack.Pdf;

public sealed record NoOptions;

public sealed record InterleavePdfOptions(bool ReverseSecond);

public sealed class InterleavePdfAction : CombineFilesAction<InterleavePdfOptions>
{
    private readonly IPdfPageRenderer? _renderer;

    public InterleavePdfAction(FileActionServices services, IPdfPageRenderer? renderer = null) : base(services)
    {
        _renderer = renderer;
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.interleave", SR.GetString("InterleavePdfAction_Title"),
            SR.GetString("InterleavePdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "combine", "front", "back", "scans", "interleave" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Boolean("ReverseSecond", SR.GetString("InterleavePdfAction_ParamTitle_ReverseSecond"), true)
            },
            Selection: SelectionRequirements.Multiple("pdf") with { MaxItems = 2 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override int MinimumInputs => 2;
    protected override int MaximumInputs => 2;

    protected override InterleavePdfOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetBoolean("ReverseSecond"));

    protected override string DescribeOperation(int inputCount, InterleavePdfOptions options) =>
        string.Format(SR.GetString("InterleavePdfAction_Operation_Description"), "first", "second");

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked || context.Content is not FileSelection selection) return preview;

        var inputs = OrderInputs(selection.Files.Where(Input.Accepts)).ToList();
        if (inputs.Count != 2) return preview;

        int count1 = 0, count2 = 0;
        bool hadRestrictions = false;

        if (_renderer != null && _renderer.IsAvailable)
        {
            try
            {
                count1 = await _renderer.GetPageCountAsync(inputs[0].Path!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            try
            {
                count2 = await _renderer.GetPageCountAsync(inputs[1].Path!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        else
        {
            try
            {
                var doc1 = await Task.Run(() => PdfDocument.Open(inputs[0].Path!), cancellationToken).ConfigureAwait(false);
                count1 = doc1.PageCount;
                if (doc1.OpenedWithEmptyPassword) hadRestrictions = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            try
            {
                var doc2 = await Task.Run(() => PdfDocument.Open(inputs[1].Path!), cancellationToken).ConfigureAwait(false);
                count2 = doc2.PageCount;
                if (doc2.OpenedWithEmptyPassword) hadRestrictions = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var newWarnings = new List<string>(preview.Warnings);
        if (hadRestrictions)
            newWarnings.Add("This PDF had usage restrictions; the new file will not have them.");

        if (count1 > 0 && count2 > 0 && count1 != count2)
            newWarnings.Add(SR.GetString("InterleavePdfAction_Warning_UnequalPages"));

        return preview with { Warnings = newWarnings };
    }

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, InterleavePdfOptions options) =>
        new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (combined)", ".pdf");

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, InterleavePdfOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var first = await PdfSupport.LoadAsync(items[0].Path, cancellationToken).ConfigureAwait(false);
        var second = await PdfSupport.LoadAsync(items[1].Path, cancellationToken).ConfigureAwait(false);

        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        await output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Interleave(first, second, options.ReverseSecond, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record InsertPdfOptions(string Position, int AfterPage, bool Swap);

public sealed class InsertPdfAction : CombineFilesAction<InsertPdfOptions>
{
    public InsertPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.insert", SR.GetString("InsertPdfAction_Title"),
            SR.GetString("InsertPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "insert", "add" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Position", SR.GetString("InsertPdfAction_ParamTitle_Position"), "after", new[]
                {
                    new ActionParameterChoice("start", SR.GetString("InsertPdfAction_ParamChoice_Start")),
                    new ActionParameterChoice("end", SR.GetString("InsertPdfAction_ParamChoice_End")),
                    new ActionParameterChoice("after", SR.GetString("InsertPdfAction_ParamChoice_After"))
                }),
                ActionParameterDefinition.Integer("AfterPage", SR.GetString("InsertPdfAction_ParamTitle_AfterPage"), 1, 1, 100000)
                    .VisibleWhen("Position", "after"),
                ActionParameterDefinition.Boolean("Swap", SR.GetString("InsertPdfAction_ParamTitle_Swap"), false)
            },
            Selection: SelectionRequirements.Multiple("pdf") with { MaxItems = 2 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override int MinimumInputs => 2;
    protected override int MaximumInputs => 2;

    protected override InsertPdfOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Position"), values.GetInt32("AfterPage"), values.GetBoolean("Swap"));

    protected override string DescribeOperation(int inputCount, InsertPdfOptions options) =>
        string.Format(SR.GetString("InsertPdfAction_Operation_Description"), "first", "second");

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, InsertPdfOptions options)
    {
        var targetFile = options.Swap ? inputs[1] : inputs[0];
        return new(Path.GetFileNameWithoutExtension(targetFile.Name) + " (inserted)", ".pdf");
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, InsertPdfOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var first = await PdfSupport.LoadAsync(items[0].Path, cancellationToken).ConfigureAwait(false);
        var second = await PdfSupport.LoadAsync(items[1].Path, cancellationToken).ConfigureAwait(false);

        var source = options.Swap ? first : second;
        var target = options.Swap ? second : first;

        int positionIndex = options.Position switch
        {
            "start" => -1,
            "end" => target.PageCount - 1,
            _ => options.AfterPage - 1
        };

        if (options.Position == "after" && options.AfterPage > target.PageCount)
        {
            throw new FileProcessingException(SR.GetString("InsertPdfAction_Error_AfterPageOutOfBounds"));
        }

        var targetFile = options.Swap ? items[1].File : items[0].File;
        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        var targetDir = options.Swap ? items[1].Directory : items[0].Directory;

        await output.WriteFileAsync(targetDir, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Insert(source, target, positionIndex, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Merges the selected PDFs (in file-name order) into one new PDF.</summary>
public sealed record MergePdfOptions(bool AddBookmarks);

public sealed class MergePdfAction : CombineFilesAction<MergePdfOptions>
{
    public MergePdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.merge", SR.GetString("MergePdfAction_Title"),
            SR.GetString("MergePdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "merge", "combine", "join" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Boolean("AddBookmarks", SR.GetString("MergePdfAction_ParamTitle_AddBookmarks"), true)
            },
            Selection: SelectionRequirements.Multiple("pdf"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override MergePdfOptions ReadOptions(ActionParameterValues values) => new(values.GetBoolean("AddBookmarks"));

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, MergePdfOptions options)
    {
        bool hadRestrictions = false;
        foreach (var input in inputs)
        {
            try
            {
                using var stream = File.OpenRead(input.Path!);
                var doc = PdfDocument.Open(input.Path!);
                if (doc.OpenedWithEmptyPassword) { hadRestrictions = true; break; }
            }
            catch { }
        }
        if (hadRestrictions)
            yield return SR.GetString("PdfSupport_Warning_UsageRestrictionsRemoved");
    }

    protected override string DescribeOperation(int inputCount, MergePdfOptions options) =>
        string.Format(SR.GetString("MergePdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, MergePdfOptions options) =>
        new(Path.GetFileNameWithoutExtension(inputs[0].Name) + " (merged)", ".pdf");

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, MergePdfOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var documents = new List<PdfDocument>();
        for (int i = 0; i < items.Count; i++)
        {
            documents.Add(await PdfSupport.LoadAsync(items[i].Path, cancellationToken).ConfigureAwait(false));
            progress?.Report(0.8 * (i + 1) / items.Count);
        }

        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        var fileTitles = options.AddBookmarks ? items.Select(i => Path.GetFileNameWithoutExtension(i.File.Name)).ToList() : null;
        await output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Merge(documents, stream, cancellationToken: ct, fileBookmarkTitles: fileTitles), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Splits each PDF into single-page PDFs inside a new folder.</summary>
public sealed record SplitPdfOptions(string Mode, string Ranges, int Every);

/// <summary>Splits each PDF into single-page PDFs, by ranges, or other criteria, inside a new folder.</summary>
public sealed class SplitPdfAction : PerFileAction<SplitPdfOptions>
{
    public SplitPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.split", SR.GetString("SplitPdfAction_Title"),
            SR.GetString("SplitPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "split", "pages", "separate" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Mode", SR.GetString("SplitPdfAction_ParamTitle_Mode"), "single", new[]
                {
                    new ActionParameterChoice("single", SR.GetString("SplitPdfAction_ParamChoice_Single")),
                    new ActionParameterChoice("ranges", SR.GetString("SplitPdfAction_ParamChoice_Ranges")),
                    new ActionParameterChoice("every", SR.GetString("SplitPdfAction_ParamChoice_Every")),
                    new ActionParameterChoice("odd-even", SR.GetString("SplitPdfAction_ParamChoice_OddEven"))
                }),
                ActionParameterDefinition.Text("Ranges", SR.GetString("SplitPdfAction_ParamTitle_Ranges"), "", SR.GetString("SplitPdfAction_ParamDesc_Ranges")).VisibleWhen("Mode", "ranges"),
                ActionParameterDefinition.Integer("Every", SR.GetString("SplitPdfAction_ParamTitle_Every"), 2, 2, 1000).VisibleWhen("Mode", "every")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override string ProgressVerb => "Splitting PDF";
    protected override SplitPdfOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Mode"), values.GetText("Ranges"), values.GetInt32("Every"));

    protected override string? ValidateOptions(SplitPdfOptions options)
    {
        if (options.Mode == "ranges")
        {
            if (string.IsNullOrWhiteSpace(options.Ranges)) return SR.GetString("PdfSupport_Error_InvalidPageSyntax");
            var parts = options.Ranges.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return SR.GetString("ReorderPdfPagesAction_Error_NoValidRanges");
            foreach (var part in parts)
            {
                var p = part.Trim();
                var err = PdfSupport.ValidatePageSyntax(p, allowEmpty: false, allowDuplicates: true);
                if (err != null) return err;
            }
        }
        return null;
    }

    protected override string DescribeOperation(int inputCount, SplitPdfOptions options) =>
        string.Format(SR.GetString("SplitPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, SplitPdfOptions options)
    {
        string suffix = options.Mode == "single" ? " (pages)" : " (split)";
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + suffix, string.Empty, IsDirectory: true);
    }

    protected override async Task ProcessAsync(FileWorkItem item, SplitPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        string suffix = options.Mode == "single" ? " (pages)" : " (split)";
        string baseName = OutputNames.Sanitize(item.BaseName);
        int pageCount = document.PageCount;

        if (options.Mode == "ranges")
        {
            var parts = options.Ranges.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var pages = PdfSupport.ParsePages(part.Trim(), pageCount, allowAll: false, allowDuplicates: true);
                if (pages != null && pages.Count == 0)
                {
                    throw new FileProcessingException(string.Format(SR.GetString("SplitPdfAction_Error_NoValidPages"), part.Trim()));
                }
            }
        }

        await output.WriteDirectoryAsync(item.Directory, item.BaseName + suffix, (directory, ct) => PdfSupport.RunAsync(() =>
        {
            if (options.Mode == "single")
            {
                int digits = Math.Max(2, pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
                for (int i = 0; i < pageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = $"{baseName} - page {(i + 1).ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture)}.pdf";
                    using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    PdfTools.ExtractPages(document, new[] { i }, stream, cancellationToken: ct);
                }
            }
            else if (options.Mode == "ranges")
            {
                var parts = options.Ranges.Split(';', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = parts[i].Trim();
                    var pages = PdfSupport.ParsePages(p, pageCount, allowAll: false, allowDuplicates: true, cancellationToken: ct)!;

                    if (pages.Count > 0)
                    {
                        string name = $"{baseName} - part {i + 1}.pdf";
                        using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        PdfTools.ExtractPages(document, pages, stream, cancellationToken: ct);
                    }
                }
            }
            else if (options.Mode == "every")
            {
                int n = options.Every;
                int parts = (pageCount + n - 1) / n;
                for (int i = 0; i < parts; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int start = i * n;
                    int count = Math.Min(n, pageCount - start);
                    var pages = Enumerable.Range(start, count).ToList();
                    string name = $"{baseName} - part {i + 1}.pdf";
                    using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    PdfTools.ExtractPages(document, pages, stream, cancellationToken: ct);
                }
            }
            else if (options.Mode == "odd-even")
            {
                var odds = Enumerable.Range(0, pageCount).Where(i => i % 2 == 0).ToList();
                var evens = Enumerable.Range(0, pageCount).Where(i => i % 2 != 0).ToList();

                ct.ThrowIfCancellationRequested();
                if (odds.Count > 0)
                {
                    using var stream = new FileStream(Path.Combine(directory, $"{baseName} - odd pages.pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    PdfTools.ExtractPages(document, odds, stream, cancellationToken: ct);
                }

                ct.ThrowIfCancellationRequested();
                if (evens.Count > 0)
                {
                    using var stream = new FileStream(Path.Combine(directory, $"{baseName} - even pages.pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    PdfTools.ExtractPages(document, evens, stream, cancellationToken: ct);
                }
            }
        }, ct), cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PageSelectionOptions(string Pages);

/// <summary>Creates a new PDF with only the selected pages, in the given order.</summary>
public sealed class ExtractPdfPagesAction : PerFileAction<PageSelectionOptions>
{
    public ExtractPdfPagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.extract-pages", SR.GetString("ExtractPdfPagesAction_Title"),
            SR.GetString("ExtractPdfPagesAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "extract", "pages", "select", "reorder" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[] { PdfSupport.PagesParameter },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override PageSelectionOptions ReadOptions(ActionParameterValues values) => new(values.GetText("Pages"));
    protected override string? ValidateOptions(PageSelectionOptions options) => PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: false);

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, PageSelectionOptions options)
    {
        bool hadRestrictions = false;
        foreach (var input in inputs)
        {
            try
            {
                var doc = PdfDocument.Open(input.Path!);
                if (doc.OpenedWithEmptyPassword) { hadRestrictions = true; break; }
            }
            catch { }
        }
        if (hadRestrictions)
            yield return SR.GetString("PdfSupport_Warning_UsageRestrictionsRemoved");
    }

    protected override string DescribeOperation(int inputCount, PageSelectionOptions options) =>
        string.Format(SR.GetString("ExtractPdfPagesAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")), options.Pages);

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, PageSelectionOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (selected pages)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, PageSelectionOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var pages = PdfSupport.ParsePages(options.Pages, document.PageCount, allowAll: false)!;
        await output.WriteFileAsync(item.Directory, item.BaseName + " (selected pages)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.ExtractPages(document, pages, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Creates a copy of the PDF without the selected pages.</summary>
public sealed class RemovePdfPagesAction : PerFileAction<PageSelectionOptions>
{
    public RemovePdfPagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.remove-pages", SR.GetString("RemovePdfPagesAction_Title"),
            SR.GetString("RemovePdfPagesAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "remove", "delete", "pages" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[] { PdfSupport.PagesParameter },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override PageSelectionOptions ReadOptions(ActionParameterValues values) => new(values.GetText("Pages"));
    protected override string? ValidateOptions(PageSelectionOptions options) => PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: false);

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, PageSelectionOptions options)
    {
        bool hadRestrictions = false;
        foreach (var input in inputs)
        {
            try
            {
                var doc = PdfDocument.Open(input.Path!);
                if (doc.OpenedWithEmptyPassword) { hadRestrictions = true; break; }
            }
            catch { }
        }
        if (hadRestrictions)
            yield return SR.GetString("PdfSupport_Warning_UsageRestrictionsRemoved");
    }

    protected override string DescribeOperation(int inputCount, PageSelectionOptions options) =>
        string.Format(SR.GetString("RemovePdfPagesAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")), options.Pages);

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, PageSelectionOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (edited)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, PageSelectionOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var pages = PdfSupport.ParsePages(options.Pages, document.PageCount, allowAll: false)!;
        await output.WriteFileAsync(item.Directory, item.BaseName + " (edited)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.RemovePages(document, pages.ToList(), stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record RotateOptions(int Degrees, string Pages);

/// <summary>Creates a rotated copy of the PDF (all or selected pages).</summary>
public sealed class RotatePdfAction : PerFileAction<RotateOptions>
{
    public RotatePdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.rotate", SR.GetString("RotatePdfAction_Title"),
            SR.GetString("RotatePdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "rotate", "turn", "orientation" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Angle", SR.GetString("RotatePdfAction_ParamTitle_Angle"), "90", new[]
                {
                    new ActionParameterChoice("90", SR.GetString("RotatePdfAction_ParamChoice_90")),
                    new ActionParameterChoice("180", SR.GetString("RotatePdfAction_ParamChoice_180")),
                    new ActionParameterChoice("270", SR.GetString("RotatePdfAction_ParamChoice_270"))
                }),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "all", SR.GetString("PdfToImagesAction_ParamDesc_Pages"), "all")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override RotateOptions ReadOptions(ActionParameterValues values) =>
        new(int.Parse(values.GetChoice("Angle"), System.Globalization.CultureInfo.InvariantCulture), values.GetText("Pages"));
    protected override string? ValidateOptions(RotateOptions options) => PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: true);

    protected override string DescribeOperation(int inputCount, RotateOptions options) =>
        string.Format(SR.GetString("RotatePdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")), options.Degrees, (string.IsNullOrWhiteSpace(options.Pages) ? "all" : options.Pages));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, RotateOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (rotated)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, RotateOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var pages = PdfSupport.ParsePages(options.Pages, document.PageCount, allowAll: true);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (rotated)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Rotate(document, options.Degrees, pages?.ToList(), stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Creates a copy without document information, XMP metadata and page metadata.</summary>
public sealed class RemovePdfMetadataAction : PerFileAction<NoOptions>
{
    public RemovePdfMetadataAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.remove-metadata", SR.GetString("RemovePdfMetadataAction_Title"),
            SR.GetString("RemovePdfMetadataAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "metadata", "privacy", "author", "strip" },
            RequiresPreview: true, ChangesFiles: true,
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override NoOptions ReadOptions(ActionParameterValues values) => new();

    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, NoOptions options)
    {
        bool hadRestrictions = false;
        foreach (var input in inputs)
        {
            try
            {
                using var stream = File.OpenRead(input.Path!);
                var doc = PdfDocument.Open(input.Path!);
                if (doc.OpenedWithEmptyPassword) { hadRestrictions = true; break; }
            }
            catch { }
        }
        if (hadRestrictions)
            yield return SR.GetString("PdfSupport_Warning_UsageRestrictionsRemoved");
    }

    protected override string DescribeOperation(int inputCount, NoOptions options) =>
        string.Format(SR.GetString("RemovePdfMetadataAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_Copy"), SR.GetString("Pdf_Copies")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, NoOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (no metadata)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, NoOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (no metadata)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.RemoveMetadata(document, stream, ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ImagesToPdfOptions(PdfPageSize PageSize);

/// <summary>Combines JPEG and PNG images into one PDF, one image per page, without re-encoding JPEGs.</summary>
public sealed class ImagesToPdfAction : CombineFilesAction<ImagesToPdfOptions>
{
    private static readonly FileInputFilter Filter = new("JPEG or PNG images", new[] { ".jpg", ".jpeg", ".png" }, new[] { FileFormats.Jpeg, FileFormats.Png });

    public ImagesToPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.images-to-pdf", SR.GetString("ImagesToPdfAction_Title"),
            SR.GetString("ImagesToPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "images", "photos", "scan", "create", "convert" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("PageSize", SR.GetString("ImagesToPdfAction_ParamTitle_PageSize"), "a4", new[]
                {
                    new ActionParameterChoice("a4", SR.GetString("ImagesToPdfAction_ParamChoice_A4")),
                    new ActionParameterChoice("letter", SR.GetString("ImagesToPdfAction_ParamChoice_Letter")),
                    new ActionParameterChoice("fit", SR.GetString("ImagesToPdfAction_ParamChoice_Fit"))
                })
            },
            Selection: SelectionRequirements.Any("image") with { IdealMinItems = 2 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => Filter;
    protected override int MinimumInputs => 1;
    protected override int MaximumInputs => 500;

    protected override ImagesToPdfOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("PageSize") switch
    {
        "letter" => PdfPageSize.Letter,
        "fit" => PdfPageSize.FitImage,
        _ => PdfPageSize.A4
    });

    protected override string DescribeOperation(int inputCount, ImagesToPdfOptions options) =>
        string.Format(SR.GetString("ImagesToPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_Page"), SR.GetString("Pdf_Pages")));

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, ImagesToPdfOptions options)
    {
        if (inputs.Count == 1) return new PlannedOutput(Path.GetFileNameWithoutExtension(inputs[0].Name), ".pdf");
        string? folder = Path.GetFileName(Path.GetDirectoryName(inputs[0].Path!));
        return new PlannedOutput(string.IsNullOrEmpty(folder) ? SR.GetString("ImagesToPdfAction_DefaultOutput") : folder, ".pdf");
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, ImagesToPdfOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var images = new List<PdfImage>();
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = items[i].Path;
            images.Add(await PdfSupport.LoadImageAsync(() => PdfImage.Load(path), cancellationToken).ConfigureAwait(false));
            progress?.Report(0.7 * (i + 1) / items.Count);
        }

        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        var layout = new PdfImagePageLayout(options.PageSize);
        await output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension,
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.ImagesToPdf(images, stream, layout, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PageNumbersOptions(string Position, string Format, int StartAt, bool SkipFirst, int FontSize);


public sealed record HeaderFooterOptions(string Text, string Placement, string Alignment, int FontSize);

public sealed class HeaderFooterPdfAction : PerFileAction<HeaderFooterOptions>
{
    private readonly TimeProvider _timeProvider;

    public HeaderFooterPdfAction(FileActionServices services, TimeProvider timeProvider = null) : base(services)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.header-footer", SR.GetString("HeaderFooterPdfAction_Title"),
            SR.GetString("HeaderFooterPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "header", "footer", "text" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Text("Text", SR.GetString("HeaderFooterPdfAction_ParamTitle_Text"), "", SR.GetString("HeaderFooterPdfAction_ParamDesc_Text")),
                ActionParameterDefinition.Choice("Placement", SR.GetString("HeaderFooterPdfAction_ParamTitle_Placement"), "header", new[]
                {
                    new ActionParameterChoice("header", SR.GetString("HeaderFooterPdfAction_ParamChoice_Header")),
                    new ActionParameterChoice("footer", SR.GetString("HeaderFooterPdfAction_ParamChoice_Footer"))
                }),
                ActionParameterDefinition.Choice("Alignment", SR.GetString("HeaderFooterPdfAction_ParamTitle_Alignment"), "center", new[]
                {
                    new ActionParameterChoice("left", SR.GetString("HeaderFooterPdfAction_ParamChoice_Left")),
                    new ActionParameterChoice("center", SR.GetString("HeaderFooterPdfAction_ParamChoice_Center")),
                    new ActionParameterChoice("right", SR.GetString("HeaderFooterPdfAction_ParamChoice_Right"))
                }),
                ActionParameterDefinition.Integer("FontSize", SR.GetString("HeaderFooterPdfAction_ParamTitle_FontSize"), 10, 6, 72)
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override HeaderFooterOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetText("Text"), values.GetChoice("Placement"), values.GetChoice("Alignment"), values.GetInt32("FontSize"));

    protected override string DescribeOperation(int inputCount, HeaderFooterOptions options) =>
        string.Format(SR.GetString("HeaderFooterPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, HeaderFooterOptions options)
    {
        string suffix = options.Placement == "header" ? " (header)" : " (footer)";
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + suffix, ".pdf");
    }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        if (options.Text.Length > 200)
            return new ActionPreview(false, SR.GetString("HeaderFooterPdfAction_Error_TooLong"), Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true);

        string sample = options.Text.Replace("{page}", "1").Replace("{pages}", "99").Replace("{date}", _timeProvider.GetLocalNow().ToString("yyyy-MM-dd"));
        try
        {
            foreach (char c in sample)
                FileKit.Pdf.PdfTools.ValidateWinAnsi(c);
        }
        catch (FileKit.Pdf.PdfOperationException)
        {
            return new ActionPreview(false, SR.GetString("HeaderFooterPdfAction_Error_InvalidChars"), Array.Empty<string>(), Array.Empty<string>(), IsBlocked: true);
        }

        return await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ProcessAsync(FileWorkItem item, HeaderFooterOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var anchor = (options.Placement, options.Alignment) switch
        {
            ("header", "left") => PdfAnchor.TopLeft,
            ("header", "center") => PdfAnchor.TopCenter,
            ("header", "right") => PdfAnchor.TopRight,
            ("footer", "left") => PdfAnchor.BottomLeft,
            ("footer", "center") => PdfAnchor.BottomCenter,
            ("footer", "right") => PdfAnchor.BottomRight,
            _ => PdfAnchor.BottomCenter
        };

        string suffix = options.Placement == "header" ? " (header)" : " (footer)";
        string dateStr = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd");

        await output.WriteFileAsync(item.Directory, item.BaseName + suffix, ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.AddHeaderFooter(document, stream, options.Text, anchor, options.FontSize, dateStr, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AddPageNumbersPdfAction : PerFileAction<PageNumbersOptions>
{
    public AddPageNumbersPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.page-numbers", SR.GetString("AddPageNumbersPdfAction_Title"),
            SR.GetString("AddPageNumbersPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "page", "number", "numbers", "pagination" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Position", SR.GetString("AddPageNumbersPdfAction_ParamTitle_Position"), "bottom-center", new[]
                {
                    new ActionParameterChoice("top-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopLeft")),
                    new ActionParameterChoice("top-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopCenter")),
                    new ActionParameterChoice("top-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopRight")),
                    new ActionParameterChoice("middle-left", SR.GetString("StampPdfImageAction_ParamChoice_MiddleLeft")),
                    new ActionParameterChoice("center", SR.GetString("StampPdfImageAction_ParamChoice_Center")),
                    new ActionParameterChoice("middle-right", SR.GetString("StampPdfImageAction_ParamChoice_MiddleRight")),
                    new ActionParameterChoice("bottom-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomLeft")),
                    new ActionParameterChoice("bottom-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomCenter")),
                    new ActionParameterChoice("bottom-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomRight"))
                }),
                ActionParameterDefinition.Choice("Format", SR.GetString("AddPageNumbersPdfAction_ParamTitle_Format"), "n", new[]
                {
                    new ActionParameterChoice("n", "n"),
                    new ActionParameterChoice("n-of-total", SR.GetString("AddPageNumbersPdfAction_ParamChoice_NOfTotal")),
                    new ActionParameterChoice("n-slash-total", "n / N")
                }),
                ActionParameterDefinition.Integer("StartAt", SR.GetString("AddPageNumbersPdfAction_ParamTitle_Start"), 1, 0, 100000),
                ActionParameterDefinition.Boolean("SkipFirst", SR.GetString("AddPageNumbersPdfAction_ParamTitle_SkipFirst"), false),
                ActionParameterDefinition.Integer("FontSize", SR.GetString("WatermarkPdfAction_ParamTitle_FontSize"), 10, 6, 48)
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override PageNumbersOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Position"), values.GetChoice("Format"), values.GetInt32("StartAt"), values.GetBoolean("SkipFirst"), values.GetInt32("FontSize"));

    protected override string DescribeOperation(int inputCount, PageNumbersOptions options) =>
        string.Format(SR.GetString("AddPageNumbersPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, PageNumbersOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (numbered)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, PageNumbersOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        var anchor = options.Position switch
        {
            "top-left" => PdfAnchor.TopLeft,
            "top-center" => PdfAnchor.TopCenter,
            "top-right" => PdfAnchor.TopRight,
            "middle-left" => PdfAnchor.MiddleLeft,
            "center" => PdfAnchor.Center,
            "middle-right" => PdfAnchor.MiddleRight,
            "bottom-left" => PdfAnchor.BottomLeft,
            "bottom-right" => PdfAnchor.BottomRight,
            _ => PdfAnchor.BottomCenter
        };

        var format = options.Format switch
        {
            "n-of-total" => PdfPageNumberFormat.PageNumberOfTotal,
            "n-slash-total" => PdfPageNumberFormat.NumberSlashTotal,
            _ => PdfPageNumberFormat.Number
        };

        await output.WriteFileAsync(item.Directory, item.BaseName + " (numbered)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.AddPageNumbers(document, stream, anchor, format, options.StartAt, options.SkipFirst, options.FontSize, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PdfToImagesOptions(string Format, int Dpi, string Pages);

/// <summary>Renders PDF pages to PNG or JPEG images in a new folder (Windows PDF renderer).</summary>
public sealed class PdfToImagesAction : PerFileAction<PdfToImagesOptions>
{
    private readonly IPdfPageRenderer? _renderer;

    public PdfToImagesAction(FileActionServices services, IPdfPageRenderer? renderer) : base(services)
    {
        _renderer = renderer;
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.to-images", SR.GetString("PdfToImagesAction_Title"),
            SR.GetString("PdfToImagesAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "image", "png", "jpeg", "render", "export", "pages" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Format", SR.GetString("PdfToImagesAction_ParamTitle_Format"), "png", new[]
                {
                    new ActionParameterChoice("png", SR.GetString("PdfToImagesAction_ParamChoice_Png")),
                    new ActionParameterChoice("jpeg", SR.GetString("PdfToImagesAction_ParamChoice_Jpeg"))
                }),
                ActionParameterDefinition.Integer("Dpi", SR.GetString("PdfToImagesAction_ParamTitle_Dpi"), 150, 72, 600),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "all", SR.GetString("PdfToImagesAction_ParamDesc_Pages"), "all")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override string ProgressVerb => SR.GetString("PdfToImagesAction_ProgressVerb");

    protected override string? UnavailableReason =>
        _renderer is { IsAvailable: true } ? null : SR.GetString("PdfToImagesAction_Error_NoRenderer");

    protected override PdfToImagesOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Format"), values.GetInt32("Dpi"), values.GetText("Pages"));

    protected override string? ValidateOptions(PdfToImagesOptions options) => PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: true);

    protected override string DescribeOperation(int inputCount, PdfToImagesOptions options) =>
        string.Format(SR.GetString("PdfToImagesAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")), options.Format.ToUpperInvariant(), options.Dpi);

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, PdfToImagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (images)", string.Empty, IsDirectory: true);
    }

    protected override async Task ProcessAsync(FileWorkItem item, PdfToImagesOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var renderer = _renderer ?? throw new FileProcessingException(SR.GetString("PdfToImagesAction_Error_NoRenderer"));
        int pageCount = await renderer.GetPageCountAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var pages = PdfSupport.ParsePages(options.Pages, pageCount, allowAll: true);
        await output.WriteDirectoryAsync(item.Directory, item.BaseName + " (images)",
            (directory, ct) => renderer.RenderToDirectoryAsync(item.Path, directory, OutputNames.Sanitize(item.BaseName), pages,
                new PdfRenderOptions(options.Format, options.Dpi), null, ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ReorderPdfPagesOptions(string Mode, string Order);

public sealed class ReorderPdfPagesAction : PerFileAction<ReorderPdfPagesOptions>
{
    public ReorderPdfPagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.reorder", SR.GetString("ReorderPdfPagesAction_Title"),
            SR.GetString("ReorderPdfPagesAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "reorder", "sort" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Mode", SR.GetString("ReorderPdfPagesAction_ParamTitle_Mode"), "custom", new[]
                {
                    new ActionParameterChoice("custom", SR.GetString("ReorderPdfPagesAction_ParamChoice_Custom")),
                    new ActionParameterChoice("reverse", SR.GetString("ReorderPdfPagesAction_ParamChoice_Reverse")),
                    new ActionParameterChoice("odd-even", SR.GetString("ReorderPdfPagesAction_ParamChoice_OddFirst")),
                    new ActionParameterChoice("even-odd", SR.GetString("ReorderPdfPagesAction_ParamChoice_EvenFirst"))
                }),
                ActionParameterDefinition.Text("Order", SR.GetString("ReorderPdfPagesAction_ParamTitle_Order"), "", SR.GetString("ReorderPdfPagesAction_ParamDesc_Order"))
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;
    protected override ReorderPdfPagesOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Mode"), values.GetText("Order"));

    protected override string? ValidateOptions(ReorderPdfPagesOptions options)
    {
        if (options.Mode == "custom")
        {
            return PdfSupport.ValidatePageSyntax(options.Order, allowEmpty: false, allowDuplicates: true);
        }
        return null;
    }

    protected override string DescribeOperation(int inputCount, ReorderPdfPagesOptions options) =>
        string.Format(SR.GetString("ReorderPdfPagesAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ReorderPdfPagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (reordered)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, ReorderPdfPagesOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<int> order;
        int n = document.PageCount;

        if (options.Mode == "custom")
        {
            order = PdfSupport.ParsePages(options.Order, n, allowAll: false, allowDuplicates: true)!;
            PdfSupport.ValidatePermutation(order, n);
        }
        else if (options.Mode == "reverse")
        {
            order = Enumerable.Range(0, n).Reverse().ToList();
        }
        else if (options.Mode == "odd-even")
        {
            var odds = Enumerable.Range(0, n).Where(i => i % 2 == 0);
            var evens = Enumerable.Range(0, n).Where(i => i % 2 != 0);
            order = odds.Concat(evens).ToList();
        }
        else if (options.Mode == "even-odd")
        {
            var evens = Enumerable.Range(0, n).Where(i => i % 2 != 0);
            var odds = Enumerable.Range(0, n).Where(i => i % 2 == 0);
            order = evens.Concat(odds).ToList();
        }
        else
        {
            throw new FileProcessingException(string.Format(SR.GetString("Pdf_Error_UnknownMode"), options.Mode));
        }

        await output.WriteFileAsync(item.Directory, item.BaseName + " (reordered)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Reorder(document, order, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record InsertBlankPagesOptions(string Mode, string Pages);

public sealed class InsertBlankPagesAction : PerFileAction<InsertBlankPagesOptions>
{
    public InsertBlankPagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.insert-blank", SR.GetString("InsertBlankPdfAction_Title"),
            SR.GetString("InsertBlankPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "insert", "blank", "empty", "page" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Mode", SR.GetString("InsertBlankPdfAction_ParamTitle_Mode"), "after-each", new[]
                {
                    new ActionParameterChoice("after-each", SR.GetString("InsertBlankPdfAction_ParamChoice_AfterEach")),
                    new ActionParameterChoice("at-positions", SR.GetString("InsertBlankPdfAction_ParamChoice_AtPositions")),
                    new ActionParameterChoice("make-even", SR.GetString("InsertBlankPdfAction_ParamChoice_MakeEven"))
                }),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "1", SR.GetString("Pdf_ParamDesc_Pages"), "1")
                    .VisibleWhen("Mode", "at-positions")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override InsertBlankPagesOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Mode"), values.GetText("Pages"));

    protected override string? ValidateOptions(InsertBlankPagesOptions options)
    {
        if (options.Mode == "at-positions")
        {
            if (string.IsNullOrWhiteSpace(options.Pages)) return SR.GetString("InsertBlankPdfAction_Error_NoPositions");
            var parts = options.Pages.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return SR.GetString("InsertBlankPdfAction_Error_NoValidPositions");

            // Allow 0 for 'at-positions'
            foreach (var part in parts)
            {
                var p = part.Trim();
                int dash = p.IndexOf('-');
                if (dash < 0)
                {
                    if (string.Equals(p, "last", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page) || page < 0)
                        return SR.GetString("InsertBlankPdfAction_Error_PositionsMustBeNonNegative");
                }
                else
                {
                    // For ranges, validate normally but allow 0?
                    string left = p[..dash].Trim();
                    string right = p[(dash + 1)..].Trim();

                    if (left.Length > 0 && (!int.TryParse(left, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int first) || first < 0))
                        return SR.GetString("InsertBlankPdfAction_Error_PositionsMustBeNonNegative");
                    if (right.Length > 0 && (!int.TryParse(right, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int last) || last < 0))
                        return SR.GetString("InsertBlankPdfAction_Error_PositionsMustBeNonNegative");
                }
            }
        }
        return null;
    }

    protected override string DescribeOperation(int inputCount, InsertBlankPagesOptions options) =>
        string.Format(SR.GetString("InsertBlankPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, InsertBlankPagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (blanks)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, InsertBlankPagesOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        int pageCount = document.PageCount;

        if (options.Mode == "make-even" && pageCount % 2 == 0)
        {
            throw new FileSkippedException("The PDF already has an even number of pages.");
        }

        await output.WriteFileAsync(item.Directory, item.BaseName + " (blanks)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() =>
            {
                var builder = new PdfDocumentBuilder();

                if (options.Mode == "after-each")
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        builder.AddPage(document, i);
                        var (w, h) = PdfTools.GetPageSize(document, i);
                        builder.AddBlankPage(w, h);
                    }
                }
                else if (options.Mode == "make-even")
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        builder.AddPage(document, i);
                    }
                    if (pageCount % 2 != 0 && pageCount > 0)
                    {
                        var (w, h) = PdfTools.GetPageSize(document, pageCount - 1);
                        builder.AddBlankPage(w, h);
                    }
                }
                else if (options.Mode == "at-positions")
                {
                    var positions = ParsePositions(options.Pages, pageCount, ct);
                    for (int i = 0; i < pageCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (i == 0 && positions.Contains(0))
                        {
                            var (w, h) = PdfTools.GetPageSize(document, 0);
                            int times = positions.Count(p => p == 0);
                            for (int t = 0; t < times; t++) builder.AddBlankPage(w, h);
                        }

                        builder.AddPage(document, i);

                        if (positions.Contains(i + 1))
                        {
                            var (w, h) = PdfTools.GetPageSize(document, i);
                            int times = positions.Count(p => p == i + 1);
                            for (int t = 0; t < times; t++) builder.AddBlankPage(w, h);
                        }
                    }
                    // Handle positions > pageCount if any? Specs: '0' means before first. '2' means after page 2.
                    // If positions contains values > pageCount, append them at the end with last page size
                    int extra = positions.Count(p => p > pageCount);
                    if (extra > 0 && pageCount > 0)
                    {
                        var (w, h) = PdfTools.GetPageSize(document, pageCount - 1);
                        for (int t = 0; t < extra; t++) builder.AddBlankPage(w, h);
                    }
                }

                builder.Save(stream, cancellationToken: ct);
            }, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<int> ParsePositions(string text, int pageCount, CancellationToken cancellationToken)
    {
        var result = new List<int>();
        var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        int limit = Math.Min(pageCount, 100000);

        foreach (var rawPart in parts)
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            int first, last;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (string.Equals(part, "last", StringComparison.OrdinalIgnoreCase)) first = limit;
                else if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first)) continue;
                last = first;
            }
            else
            {
                string left = part[..dash].Trim();
                string right = part[(dash + 1)..].Trim();

                if (left.Length == 0) first = 0;
                else if (string.Equals(left, "last", StringComparison.OrdinalIgnoreCase)) first = limit;
                else if (!int.TryParse(left, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first)) continue;

                if (right.Length == 0) last = limit;
                else if (string.Equals(right, "last", StringComparison.OrdinalIgnoreCase)) last = limit;
                else if (!int.TryParse(right, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out last)) continue;
            }

            if (last < first)
                throw new FileProcessingException(SR.GetString("SplitPdfAction_Error_InvalidRangeOrder"));
            if (first > limit || last > limit)
                throw new FileProcessingException(string.Format(SR.GetString("InsertBlankPdfAction_Error_PagePositions"), limit));
            if (first < 0 || last < 0)
                throw new FileProcessingException(string.Format(SR.GetString("InsertBlankPdfAction_Error_PagePositions"), limit));

            for (int p = first; p <= last; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(p);
            }
        }
        return result;
    }
}

public sealed record WatermarkPdfOptions(string Text, PdfWatermarkStyle Style, int Opacity, int FontSize, PdfColor Color);

public sealed class WatermarkPdfAction : PerFileAction<WatermarkPdfOptions>
{
    public WatermarkPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.watermark", SR.GetString("WatermarkPdfAction_Title"),
            SR.GetString("WatermarkPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "watermark", "text", "stamp", "draft" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Text("Text", SR.GetString("HeaderFooterPdfAction_ParamTitle_Text"), "DRAFT", SR.GetString("WatermarkPdfAction_ParamDesc_Text")),
                ActionParameterDefinition.Choice("Style", SR.GetString("WatermarkPdfAction_ParamTitle_Style"), "diagonal", new[]
                {
                    new ActionParameterChoice("diagonal", SR.GetString("WatermarkPdfAction_ParamChoice_Diagonal")),
                    new ActionParameterChoice("horizontal", SR.GetString("WatermarkPdfAction_ParamChoice_Horizontal")),
                    new ActionParameterChoice("tiled", SR.GetString("WatermarkPdfAction_ParamChoice_Tiled"))
                }),
                ActionParameterDefinition.Integer("Opacity", SR.GetString("WatermarkPdfAction_ParamTitle_Opacity"), 20, 5, 100),
                ActionParameterDefinition.Integer("FontSize", SR.GetString("WatermarkPdfAction_ParamTitle_FontSize"), 0, 0, 200, SR.GetString("WatermarkPdfAction_ParamDesc_FontSize")),
                ActionParameterDefinition.Color("Color", SR.GetString("WatermarkPdfAction_ParamTitle_Color"), "#808080")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override WatermarkPdfOptions ReadOptions(ActionParameterValues values)
    {
        if (!values.TryRaw("Text", out _)) return new WatermarkPdfOptions("DRAFT", PdfWatermarkStyle.Diagonal, 20, 0, new PdfColor(0.5, 0.5, 0.5));
        string text = values.GetText("Text");
        if (text.Length > 100) text = text.Substring(0, 100);

        string styleStr = values.GetChoice("Style");
        PdfWatermarkStyle style = styleStr switch
        {
            "horizontal" => PdfWatermarkStyle.Horizontal,
            "tiled" => PdfWatermarkStyle.Tiled,
            _ => PdfWatermarkStyle.Diagonal
        };

        int opacity = values.GetInt32("Opacity");
        int fontSize = values.GetInt32("FontSize");
        var cl = values.GetColor("Color");
        var color = new PdfColor(cl.R / 255.0, cl.G / 255.0, cl.B / 255.0);

        return new WatermarkPdfOptions(text, style, opacity, fontSize, color);
    }

    protected override string? ValidateOptions(WatermarkPdfOptions options) => string.IsNullOrWhiteSpace(options.Text) ? "Watermark text cannot be empty." : null;

    protected override string DescribeOperation(int inputCount, WatermarkPdfOptions options) =>
        string.Format(SR.GetString("WatermarkPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, WatermarkPdfOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (watermarked)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, WatermarkPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (watermarked)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.AddWatermark(document, stream, options.Text, options.Style, options.Opacity / 100.0, options.FontSize, options.Color, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record CropPdfOptions(int Margin, string Pages);

public sealed class CropPdfAction : PerFileAction<CropPdfOptions>
{
    public CropPdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.crop", SR.GetString("CropPdfAction_Title"),
            SR.GetString("CropPdfAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "crop", "margins", "white", "space" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Integer("Margin", SR.GetString("CropPdfAction_ParamTitle_Margin"), 10, 0, 100, SR.GetString("CropPdfAction_ParamDesc_Margin")),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "all", SR.GetString("Pdf_ParamDesc_Pages"), "all")
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override CropPdfOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetInt32("Margin"), values.GetText("Pages"));

    protected override string? ValidateOptions(CropPdfOptions options)
    {
        if (options.Margin < 0 || options.Margin > 100) return SR.GetString("CropPdfAction_Error_MarginBounds");
        return null;
    }

    protected override string DescribeOperation(int inputCount, CropPdfOptions options) =>
        string.Format(SR.GetString("CropPdfAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, CropPdfOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (cropped)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, CropPdfOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        var pages = PdfSupport.ParsePages(options.Pages, document.PageCount, allowAll: true);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (cropped)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.Crop(document, options.Margin, pages?.ToList(), stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ResizePdfPagesOptions(string Size, string Fit);

public sealed class ResizePdfPagesAction : PerFileAction<ResizePdfPagesOptions>
{
    public ResizePdfPagesAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.resize-pages", SR.GetString("ResizePdfPagesAction_Title"),
            SR.GetString("ResizePdfPagesAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "resize", "scale", "a4", "letter", "a5", "legal" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Size", SR.GetString("ResizePdfPagesAction_ParamTitle_Size"), "a4", new[]
                {
                    new ActionParameterChoice("a4", "A4"),
                    new ActionParameterChoice("letter", "US Letter"),
                    new ActionParameterChoice("a5", "A5"),
                    new ActionParameterChoice("legal", "US Legal")
                }),
                ActionParameterDefinition.Choice("Fit", SR.GetString("ResizePdfPagesAction_ParamTitle_Fit"), "fit", new[]
                {
                    new ActionParameterChoice("fit", SR.GetString("ResizePdfPagesAction_ParamChoice_Fit")),
                    new ActionParameterChoice("fit-shrink", SR.GetString("ResizePdfPagesAction_ParamChoice_FitShrink"))
                })
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override ResizePdfPagesOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Size"), values.GetChoice("Fit"));

    protected override string? ValidateOptions(ResizePdfPagesOptions options) => null;

    protected override string DescribeOperation(int inputCount, ResizePdfPagesOptions options) =>
        string.Format(SR.GetString("ResizePdfPagesAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ResizePdfPagesOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (resized)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, ResizePdfPagesOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);
        await output.WriteFileAsync(item.Directory, item.BaseName + " (resized)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.ResizePages(document, options.Size, options.Fit, stream, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}


internal static class StampImageHelper
{
    public static async Task ProcessAsync(PdfDocument document, PdfImage image, FileWorkItem item, string position, int width, string pages, int opacityPercent, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var anchor = position switch
        {
            "top-left" => PdfAnchor.TopLeft,
            "top-center" => PdfAnchor.TopCenter,
            "top-right" => PdfAnchor.TopRight,
            "middle-left" => PdfAnchor.MiddleLeft,
            "center" => PdfAnchor.Center,
            "middle-right" => PdfAnchor.MiddleRight,
            "bottom-left" => PdfAnchor.BottomLeft,
            "bottom-right" => PdfAnchor.BottomRight,
            _ => PdfAnchor.BottomCenter
        };

        double opacity = opacityPercent / 100.0;

        await output.WriteFileAsync(item.Directory, item.BaseName + " (stamped)", ".pdf",
            (stream, ct) => PdfSupport.RunAsync(() => PdfTools.StampImage(document, image, stream, anchor, width, opacity, pages, cancellationToken: ct), ct),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record StampPdfImageOptions(string Image, string Position, int Width, string Pages, int Opacity);

public sealed class StampPdfImageAction : PerFileAction<StampPdfImageOptions>
{
    public StampPdfImageAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.stamp-image", SR.GetString("StampPdfImageAction_Title"),
            SR.GetString("StampPdfImageAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "image", "stamp", "signature", "watermark" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.FilePath("Image", SR.GetString("StampPdfImageAction_ParamTitle_Image"), new[] { ".png", ".jpg", ".jpeg" }),
                ActionParameterDefinition.Choice("Position", SR.GetString("AddPageNumbersPdfAction_ParamTitle_Position"), "bottom-center", new[]
                {
                    new ActionParameterChoice("top-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopLeft")),
                    new ActionParameterChoice("top-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopCenter")),
                    new ActionParameterChoice("top-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopRight")),
                    new ActionParameterChoice("middle-left", SR.GetString("StampPdfImageAction_ParamChoice_MiddleLeft")),
                    new ActionParameterChoice("center", SR.GetString("StampPdfImageAction_ParamChoice_Center")),
                    new ActionParameterChoice("middle-right", SR.GetString("StampPdfImageAction_ParamChoice_MiddleRight")),
                    new ActionParameterChoice("bottom-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomLeft")),
                    new ActionParameterChoice("bottom-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomCenter")),
                    new ActionParameterChoice("bottom-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomRight"))
                }),
                ActionParameterDefinition.Integer("Width", SR.GetString("StampPdfImageAction_ParamTitle_Width"), 40, 5, 300),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "all", SR.GetString("StampPdfImageAction_ParamDesc_Pages"), "all"),
                ActionParameterDefinition.Integer("Opacity", SR.GetString("StampPdfImageAction_ParamTitle_Opacity"), 100, 1, 100)
            },
            Selection: SelectionRequirements.Any("pdf") with { IdealMinItems = 1, IdealMaxItems = 1 });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => PdfSupport.PdfInput;

    protected override StampPdfImageOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetFilePath("Image"), values.GetChoice("Position"), values.GetInt32("Width"), values.GetText("Pages"), values.GetInt32("Opacity"));

    protected override string? ValidateOptions(StampPdfImageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Image) || !File.Exists(options.Image))
            return SR.GetString("StampPdfImageAction_Error_InvalidImage");

        if (!string.Equals(options.Pages, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Pages, "first", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Pages, "last", StringComparison.OrdinalIgnoreCase))
        {
            return PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: false);
        }
        return null;
    }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;

        var options = ReadOptions(ActionParameterValues.From(parameters, Descriptor.Parameters));
        try
        {
            await PdfSupport.LoadImageAsync(() => PdfImage.Load(options.Image), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileProcessingException or IOException or UnauthorizedAccessException)
        {
            return preview with
            {
                IsBlocked = true,
                PreviewText = ex is FileProcessingException ? ex.Message : SR.GetString("StampPdfImageAction_Error_UnreadableImage")
            };
        }

        return preview;
    }

    protected override string DescribeOperation(int inputCount, StampPdfImageOptions options) =>
        string.Format(SR.GetString("StampPdfImageAction_Operation_Description"), Plural(inputCount, SR.GetString("Pdf_File"), SR.GetString("Pdf_Files")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, StampPdfImageOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + " (stamped)", ".pdf");
    }

    protected override async Task ProcessAsync(FileWorkItem item, StampPdfImageOptions options, FileOutputWriter output, CancellationToken cancellationToken)
    {
        var document = await PdfSupport.LoadAsync(item.Path, cancellationToken).ConfigureAwait(false);

        // Run off thread to avoid blocking if reading large images
        var image = await PdfSupport.LoadImageAsync(() => PdfImage.Load(options.Image), cancellationToken).ConfigureAwait(false);

        await StampImageHelper.ProcessAsync(document, image, item, options.Position, options.Width, options.Pages, options.Opacity, output, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record StampSelectedImageOptions(string Position, int Width, string Pages, int Opacity);

public sealed class StampSelectedImagePdfAction : CombineFilesAction<StampSelectedImageOptions>
{
    public StampSelectedImagePdfAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            "powertrove.pdf.stamp-selected-image", SR.GetString("StampSelectedImagePdfAction_Title"),
            SR.GetString("StampPdfImageAction_Description"),
            new HashSet<ContentKind> { ContentKind.Files },
            new[] { "pdf", "image", "stamp", "signature", "watermark" },
            RequiresPreview: true, ChangesFiles: true,
            Parameters: new[]
            {
                ActionParameterDefinition.Choice("Position", SR.GetString("AddPageNumbersPdfAction_ParamTitle_Position"), "bottom-center", new[]
                {
                    new ActionParameterChoice("top-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopLeft")),
                    new ActionParameterChoice("top-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopCenter")),
                    new ActionParameterChoice("top-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_TopRight")),
                    new ActionParameterChoice("middle-left", SR.GetString("StampPdfImageAction_ParamChoice_MiddleLeft")),
                    new ActionParameterChoice("center", SR.GetString("StampPdfImageAction_ParamChoice_Center")),
                    new ActionParameterChoice("middle-right", SR.GetString("StampPdfImageAction_ParamChoice_MiddleRight")),
                    new ActionParameterChoice("bottom-left", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomLeft")),
                    new ActionParameterChoice("bottom-center", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomCenter")),
                    new ActionParameterChoice("bottom-right", SR.GetString("AddPageNumbersPdfAction_ParamChoice_BottomRight"))
                }),
                ActionParameterDefinition.Integer("Width", SR.GetString("StampPdfImageAction_ParamTitle_Width"), 40, 5, 300),
                ActionParameterDefinition.Text("Pages", SR.GetString("Pdf_ParamTitle_Pages"), "all", SR.GetString("StampPdfImageAction_ParamDesc_Pages"), "all"),
                ActionParameterDefinition.Integer("Opacity", SR.GetString("StampPdfImageAction_ParamTitle_Opacity"), 100, 1, 100)
            },
            Selection: SelectionRequirements.Multiple("pdf", "image") with
            {
                MaxItems = 2,
                AllowOtherItems = false,
                FamilyLimits = new Dictionary<string, FamilyLimit>(StringComparer.OrdinalIgnoreCase)
                {
                    { "pdf", new FamilyLimit(1, 1) },
                    { "image", new FamilyLimit(1, 1) }
                }
            });
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => new("PDF and Image", new[] { ".pdf", ".png", ".jpg", ".jpeg" }, new[] { FileFormats.Pdf, FileFormats.Png, FileFormats.Jpeg });

    protected override StampSelectedImageOptions ReadOptions(ActionParameterValues values) =>
        new(values.GetChoice("Position"), values.GetInt32("Width"), values.GetText("Pages"), values.GetInt32("Opacity"));

    protected override string? ValidateOptions(StampSelectedImageOptions options)
    {
        if (!string.Equals(options.Pages, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Pages, "first", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Pages, "last", StringComparison.OrdinalIgnoreCase))
        {
            return PdfSupport.ValidatePageSyntax(options.Pages, allowEmpty: false);
        }
        return null;
    }

    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
    {
        var preview = await base.PreviewAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (preview.IsBlocked) return preview;

        if (context.Content is not FileSelection selection) return preview;

        var imageFile = selection.Files.FirstOrDefault(f => SelectionFamilies.Of(f) == "image");
        if (imageFile == null || imageFile.Path == null) return preview;

        try
        {
            await Task.Run(() => PdfImage.Load(imageFile.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            return preview with
            {
                IsBlocked = true,
                PreviewText = ex is PdfException ? PdfSupport.Wrap((PdfException)ex).Message : SR.GetString("StampPdfImageAction_Error_UnreadableImage")
            };
        }

        return preview;
    }

    protected override string DescribeOperation(int inputCount, StampSelectedImageOptions options) =>
        string.Format(SR.GetString("StampPdfImageAction_Operation_Description"), SR.GetString("Pdf_File"));

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, StampSelectedImageOptions options)
    {
        var pdfInput = inputs.FirstOrDefault(f => SelectionFamilies.Of(f) == "pdf") ?? inputs[0];
        return new PlannedOutput(Path.GetFileNameWithoutExtension(pdfInput.Name) + " (stamped)", ".pdf");
    }

    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, StampSelectedImageOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pdfItem = items.FirstOrDefault(i => SelectionFamilies.Of(i.File) == "pdf");
        var imageItem = items.FirstOrDefault(i => SelectionFamilies.Of(i.File) == "image");

        if (pdfItem == null || imageItem == null) return;

        var document = await PdfSupport.LoadAsync(pdfItem.Path, cancellationToken).ConfigureAwait(false);
        var image = await Task.Run(() => PdfImage.Load(imageItem.Path), cancellationToken).ConfigureAwait(false);

        await StampImageHelper.ProcessAsync(document, image, pdfItem, options.Position, options.Width, options.Pages, options.Opacity, output, cancellationToken).ConfigureAwait(false);
    }
}
