using FileKit.Pdf;
using FileKit.Tabular;

namespace PowerTrove.Pack.Markdown.Pdf;

/// <summary>
/// Reads the text layer of a PDF with FileKit.Pdf: words with font size and style, grouped into blocks in reading
/// order (columns resolved), straight stroked lines as table borders and, when asked, the placed pictures. FileKit
/// checks the structure and limits first. Text that is not horizontal is counted and skipped, and so are pictures
/// that cannot be decoded. A page with more paths than the limit keeps its text and only stops collecting borders.
/// </summary>
public sealed class PdfLayoutTextReader : IPdfTextReader
{
    public const int MaximumFileBytes = 100 * 1024 * 1024;

    public async Task<PdfTextDocument> ReadAsync(Stream source, bool images, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (input.Length + count > MaximumFileBytes) throw new PdfTextException(PdfTextFailure.TooComplex, "PDF exceeds the size limit.");
            await input.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return await Task.Run(() => Read(input.ToArray(), images, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tags of a tagged PDF; a damaged structure tree only means an untagged document.</summary>
    private static PdfStructureTags Structure(PdfDocument pdf, CancellationToken ct)
    {
        try { return PdfStructureTags.Build(PdfStructure.Read(pdf, ct), ct); }
        catch (PdfException) { return PdfStructureTags.Build([], ct); }
    }

    /// <summary>Bookmarks with a target page, flattened in order; a broken outline only means no bookmarks.</summary>
    private static IReadOnlyList<PdfTextBookmark> Bookmarks(PdfDocument pdf, CancellationToken ct)
    {
        IReadOnlyList<PdfOutlineItem> outline;
        try { outline = PdfOutline.Read(pdf, ct); }
        catch (PdfException) { return []; }
        var result = new List<PdfTextBookmark>();
        void Add(IReadOnlyList<PdfOutlineItem> items, int level)
        {
            foreach (var item in items)
            {
                if (item.PageIndex is int page && !string.IsNullOrWhiteSpace(item.Title))
                    result.Add(new PdfTextBookmark(item.Title.Trim(), level, page + 1, item.Top));
                Add(item.Children, level + 1);
            }
        }
        Add(outline, 1);
        return result;
    }

    private static PdfTextDocument Read(byte[] bytes, bool images, CancellationToken ct)
    {
        try
        {
            var pdf = PdfDocument.Load(bytes, new PdfReadLimits { MaxFileBytes = MaximumFileBytes, MaxObjects = 500_000, MaxPages = 1000, MaxNestingDepth = 64, MaxDecodedStreamBytes = 32 * 1024 * 1024, MaxXrefSections = 200 });
            if (pdf.Trailer["Encrypt"] is not null) throw new PdfTextException(PdfTextFailure.Encrypted, "Encrypted PDF.");
            var layout = PdfLayoutReader.Read(pdf, new PdfLayoutOptions
            {
                MaxPages = 1000,
                MaxLetters = 2_000_000,
                MaxLettersPerPage = 2_000_000,
                MaxWords = 2_000_000,
                MaxWordsPerPage = 2_000_000,
                MaxPathCommands = 20_000,
                IgnoreExcessPaths = true,
                MaxDecodedStreamBytes = 32 * 1024 * 1024,
                MaxTotalDecodedBytes = 512L * 1024 * 1024,
                SkipRotatedText = true,
                IncludeImages = images,
                MaxImages = 500,
                MaxImageBytes = 100L * 1024 * 1024,
                MaxImageSamples = 40_000_000,
                MinimumImageSize = 24
            }, ct);
            var structure = Structure(pdf, ct);
            int? Tag(int page, int? mcid) => mcid is int id && structure.Content.TryGetValue((page - 1, id), out int tag) ? tag : null;
            var pages = layout.Pages.Select(page => new PdfTextPage(page.Number, page.Width, page.Height,
                page.Blocks.Select(block => new PdfTextBlock(block.Lines.Select(line => new PdfTextLine(line.Words
                    .Select(w => new PdfTextWord(w.Text, w.Left, w.Bottom, w.Right, w.Top, w.FontSize, w.Bold, w.Italic, w.Monospace, Tag(page.Number, w.Mcid), w.Artifact, w.RaisedFrom))
                    .ToArray())).ToArray())).ToArray(),
                page.Rules.Select(r => new TableRule(r.X1, r.Y1, r.X2, r.Y2)).ToArray(),
                images ? page.Images.Where(i => !i.Artifact).Select(i => new PdfPageImage(i.Left, i.Bottom, i.Right, i.Top, i.Bytes, i.Extension,
                    Tag(page.Number, i.Mcid) is int tag ? structure.Tags[tag].Alt : null)).ToArray() : null,
                (page.Links ?? []).Select(l => new PdfTextLink(l.Left, l.Bottom, l.Right, l.Top, l.Uri, l.Target?.PageIndex + 1, l.Target?.Top)).ToArray())).ToArray();
            return new PdfTextDocument(pages, layout.SkippedText, layout.SkippedImages, Bookmarks(pdf, ct), structure.Tags.Count > 0 ? structure.Tags : null);
        }
        catch (PdfEncryptedException)
        {
            throw new PdfTextException(PdfTextFailure.Encrypted, "Encrypted PDF.");
        }
        catch (PdfLimitExceededException)
        {
            throw new PdfTextException(PdfTextFailure.TooComplex, "The PDF exceeds the reading limits.");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PdfTextException or OutOfMemoryException))
        {
            throw new PdfTextException(PdfTextFailure.Invalid, "The PDF is malformed, exceeds its limits or uses unsupported features.");
        }
    }
}
