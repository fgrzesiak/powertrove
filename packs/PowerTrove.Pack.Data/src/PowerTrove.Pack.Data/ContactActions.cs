using System.Text;
using T = FileKit.Tabular;
using X = FileKit.Xlsx;

namespace PowerTrove.Pack.Data;

internal static class ContactSupport
{
    internal static async Task<IReadOnlyList<T.ContactCard>> Read(Stream input, string kind, CancellationToken ct)
    {
        if (kind.StartsWith("vcf", StringComparison.Ordinal) || kind == "merge-vcf") return await T.VCard.ReadAsync(input, ct).ConfigureAwait(false);
        T.Table table;
        if (kind == "xlsx-to-vcf")
        {
            var book = await X.XlsxReader.ReadAsync(input, new X.XlsxLimits(MaxRows: 100001, MaxCells: 4000000), ct).ConfigureAwait(false);
            if (book.Sheets.Count != 1 || book.Sheets[0].Rows.Count < 2) throw new T.TabularException("Select a single-sheet contact workbook with headers.");
            var rows = book.Sheets[0].Rows;
            var headers = rows[0].Select(c => c is X.TextCell t ? t.Value : throw new T.TabularException("Contact headers must be text.")).ToArray();
            table = new(headers, rows.Skip(1).Select(row => (IReadOnlyList<T.Cell>)headers.Select((_, index) => index >= row.Count ? new T.EmptyCell() : row[index] switch
            {
                X.TextCell text => (T.Cell)new T.TextCell(text.Value), X.EmptyCell => new T.EmptyCell(),
                _ => throw new T.TabularException("Contact values must be text to avoid losing identifiers.")
            }).ToArray()).ToArray());
            if (rows.Any(r => r.Count > headers.Length)) throw new T.TabularException("Contact row exceeds header width.");
        }
        else table = await T.CsvReader.ReadAsync(input, limits: new T.CsvReadLimits(MaxRows: 100001, MaxColumns: 1024, MaxBytes: T.VCard.MaxBytes), cancellationToken: ct).ConfigureAwait(false);
        return await Task.Run(() => T.VCard.FromTable(table, ct), ct).ConfigureAwait(false);
    }
    internal static async Task Write(FileOutputWriter output, string directory, string stem, string kind, IReadOnlyList<T.ContactCard> cards, CancellationToken ct)
    {
        if (kind.EndsWith("vcf", StringComparison.Ordinal))
        {
            string text = await Task.Run(() => T.VCard.Write(cards, ct), ct).ConfigureAwait(false);
            await DataSupport.WriteText(output, directory, stem, ".vcf", text, ct).ConfigureAwait(false); return;
        }
        var table = await Task.Run(() => T.VCard.ToTable(cards, ct), ct).ConfigureAwait(false);
        if (kind.EndsWith("xlsx", StringComparison.Ordinal))
        {
            var rows = new List<IReadOnlyList<X.Cell>> { table.Headers!.Select(h => (X.Cell)new X.TextCell(h)).ToArray() };
            rows.AddRange(table.Rows.Select(r => (IReadOnlyList<X.Cell>)r.Select(ExcelAction.ToXlsx).ToArray()));
            await output.WriteFileAsync(directory, stem, ".xlsx", (stream, token) => X.XlsxWriter.WriteAsync(stream, new([new("Contacts", rows, HasHeader: true)]), token), ct).ConfigureAwait(false);
        }
        else await output.WriteFileAsync(directory, stem, ".csv", async (stream, token) =>
        { await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true); T.CsvWriter.Write(writer, table, cancellationToken: token); }, ct).ConfigureAwait(false);
    }
}

public sealed class ContactAction(FileActionServices services, string kind) : PerFileAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.data." + kind, SR.Get(kind), SR.Get("ContactDesc_" + kind),
        new HashSet<ContentKind> { ContentKind.Files }, ["contacts", "vcf", "vcard", "csv", "excel", kind], true, true,
        Selection: SelectionRequirements.Any(kind.StartsWith("vcf", StringComparison.Ordinal) ? "other" : "spreadsheet"));
    protected override FileInputFilter Input => new(SR.Get("Files"), kind.StartsWith("vcf", StringComparison.Ordinal) ? [".vcf"] : kind == "xlsx-to-vcf" ? [".xlsx"] : [".csv", ".tsv"], null);
    protected override bool ReadOptions(ActionParameterValues values) => false;
    protected override string DescribeOperation(int count, bool options) => string.Format(SR.Get("Operation"), count);
    protected override IEnumerable<string> GetWarnings(IReadOnlyList<SelectedFile> inputs, bool options) => [SR.Get("ContactLimits")];
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, bool options)
    { yield return new(Path.GetFileNameWithoutExtension(file.Name), kind.EndsWith("xlsx", StringComparison.Ordinal) ? ".xlsx" : kind.EndsWith("csv", StringComparison.Ordinal) ? ".csv" : ".vcf"); }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try
        {
            long count = 0;
            foreach (var file in ((FileSelection)context.Content).Files.Where(Input.Accepts))
            {
                await using var input = Services.FileSystem.OpenRead(file.Path!); var cards = await ContactSupport.Read(input, kind, ct).ConfigureAwait(false);
                if (!kind.EndsWith("vcf", StringComparison.Ordinal)) await Task.Run(() => T.VCard.ToTable(cards, ct), ct).ConfigureAwait(false);
                count += cards.Count;
            }
            return preview with { PreviewText = string.Format(SR.Get("ContactPreview"), count) };
        }
        catch (Exception ex) when (ex is T.TabularException or X.XlsxException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ContactInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }
    protected override async Task ProcessAsync(FileWorkItem item, bool options, FileOutputWriter output, CancellationToken ct)
    {
        try
        {
            await using var input = Services.FileSystem.OpenRead(item.Path);
            var cards = await ContactSupport.Read(input, kind, ct).ConfigureAwait(false);
            await ContactSupport.Write(output, item.Directory, item.BaseName, kind, cards, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is T.TabularException or X.XlsxException) { throw new FileProcessingException(SR.Get("ContactInvalid"), ex); }
    }
}

public sealed class MergeContactsAction(FileActionServices services) : CombineFilesAction<bool>(services)
{
    public override ActionDescriptor Descriptor => new("powertrove.data.merge-vcf", SR.Get("merge-vcf"), SR.Get("MergeContactsDescription"),
        new HashSet<ContentKind> { ContentKind.Files }, ["contacts", "vcf", "vcard", "merge", "combine"], true, true,
        Selection: SelectionRequirements.Multiple("other"));
    protected override FileInputFilter Input => new(SR.Get("Files"), [".vcf"], null);
    protected override bool ReadOptions(ActionParameterValues values) => false;
    protected override string DescribeOperation(int count, bool options) => string.Format(SR.Get("Operation"), count);
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> files, bool options) => new(Path.GetFileNameWithoutExtension(files[0].Name) + " (merged)", ".vcf");
    private async Task<IReadOnlyList<T.ContactCard>> Read(IEnumerable<SelectedFile> files, CancellationToken ct)
    {
        var cards = new List<T.ContactCard>(); long bytes = 0;
        foreach (var file in files)
        {
            bytes += Services.FileSystem.GetFileLength(file.Path!); if (bytes > T.VCard.MaxBytes) throw new T.TabularException("Merged vCard size limit exceeded.");
            await using var input = Services.FileSystem.OpenRead(file.Path!); cards.AddRange(await T.VCard.ReadAsync(input, ct).ConfigureAwait(false));
            if (cards.Count > 100000) throw new T.TabularException("Contact count limit exceeded.");
        }
        return cards;
    }
    public override async ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken ct)
    {
        var preview = await base.PreviewAsync(context, parameters, ct).ConfigureAwait(false); if (preview.IsBlocked) return preview;
        try { var cards = await Read(((FileSelection)context.Content).Files.Where(Input.Accepts), ct).ConfigureAwait(false); return preview with { PreviewText = string.Format(SR.Get("ContactPreview"), cards.Count) }; }
        catch (T.TabularException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ContactInvalid") }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return preview with { IsBlocked = true, PreviewText = SR.Get("ReadError") }; }
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool options, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var cards = await Read(items.Select(i => i.File), ct).ConfigureAwait(false);
            await ContactSupport.Write(output, items[0].Directory, items[0].BaseName + " (merged)", "merge-vcf", cards, ct).ConfigureAwait(false);
        }
        catch (T.TabularException ex) { throw new FileProcessingException(SR.Get("ContactInvalid"), ex); }
    }
}
