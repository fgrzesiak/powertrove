using FileKit.Pdf;
using FileKit.Tabular;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PowerTrove.Pack.Pdf.Layout;

public sealed class PdfPositionedReader : IPositionedDocumentReader
{
    public const int MaxFileBytes = 25 * 1024 * 1024;
    public async Task<IReadOnlyList<PositionedPage>> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(); byte[] buffer = new byte[81920]; int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (input.Length + count > MaxFileBytes) throw new TableLayoutException("PDF exceeds 25 MiB.");
            await input.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return await Task.Run(() => Read(input.ToArray(), cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    private static IReadOnlyList<PositionedPage> Read(byte[] input, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var checkedPdf = FileKit.Pdf.PdfDocument.Load(input, new PdfReadLimits { MaxFileBytes = MaxFileBytes, MaxObjects = 100000, MaxPages = 200, MaxNestingDepth = 64, MaxDecodedStreamBytes = 16 * 1024 * 1024, MaxXrefSections = 100 });
            if (checkedPdf.Trailer["Encrypt"] is not null) throw new TableLayoutException("Encrypted PDFs are unsupported for table extraction.");
            using var document = UglyToad.PdfPig.PdfDocument.Open(input, new ParsingOptions { UseLenientParsing = false, SkipMissingFonts = false, MaxStackDepth = 64, FilterProvider = new BoundedPdfFilters(ct) });
            if (document.NumberOfPages > 200) throw new TableLayoutException("PDF page limit exceeded.");
            var pages = new List<PositionedPage>(); int totalWords = 0, totalLetters = 0;
            for (int number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested(); var page = document.GetPage(number);
                if (page.Letters.Count > 100000 || (totalLetters += page.Letters.Count) > 500000) throw new TableLayoutException("PDF text complexity limit exceeded.");
                var words = new List<PositionedWord>();
                foreach (var word in page.GetWords())
                {
                    ct.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    if (word.TextOrientation != TextOrientation.Horizontal) throw new TableLayoutException("Rotated or vertical PDF table text is unsupported.");
                    var box = word.BoundingBox; words.Add(new(word.Text, box.Left, box.Bottom, box.Right, box.Top));
                    if (words.Count > 50000 || ++totalWords > 200000) throw new TableLayoutException("PDF word limit exceeded.");
                }
                var rules = new List<TableRule>(); int commands = 0;
                foreach (var path in page.Paths.Where(p => p.IsStroked && !p.IsClipping)) foreach (var subpath in path)
                {
                    PdfPoint? start = null, last = null;
                    foreach (var command in subpath.Commands)
                    {
                        ct.ThrowIfCancellationRequested(); if (++commands > 10000) throw new TableLayoutException("PDF path complexity limit exceeded.");
                        if (command is PdfSubpath.Move move) { start = last = move.Location; }
                        else if (command is PdfSubpath.Line line) { Add(line.From, line.To); last = line.To; }
                        else if (command is PdfSubpath.Close && last.HasValue && start.HasValue) Add(last.Value, start.Value);
                        else last = null;
                    }
                }
                pages.Add(new(number, words.AsReadOnly(), rules.AsReadOnly()));
                void Add(PdfPoint from, PdfPoint to)
                { if (Math.Abs(from.X - to.X) <= 1.5 || Math.Abs(from.Y - to.Y) <= 1.5) rules.Add(new(from.X, from.Y, to.X, to.Y)); }
            }
            return pages.AsReadOnly();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TableLayoutException) && ex is not OutOfMemoryException)
        { throw new TableLayoutException("The PDF is malformed, encrypted, exceeds its limits or uses unsupported text/stream features."); }
    }
}
