using FileKit.Pdf;
using FileKit.Tabular;

namespace PowerTrove.Pack.Pdf.Layout;

/// <summary>Words and straight stroked lines of every page, read by FileKit.Pdf within the table extraction limits.</summary>
public sealed class PdfPositionedReader : IPositionedDocumentReader
{
    public const int MaxFileBytes = 25 * 1024 * 1024;

    public async Task<IReadOnlyList<PositionedPage>> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (input.Length + count > MaxFileBytes) throw new TableLayoutException("PDF exceeds 25 MiB.");
            await input.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return await Task.Run(() => Read(input.ToArray(), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<PositionedPage> Read(byte[] bytes, CancellationToken ct)
    {
        try
        {
            var pdf = PdfDocument.Load(bytes, new PdfReadLimits { MaxFileBytes = MaxFileBytes, MaxObjects = 100_000, MaxPages = 200, MaxNestingDepth = 64, MaxDecodedStreamBytes = 16 * 1024 * 1024, MaxXrefSections = 100 });
            if (pdf.Trailer["Encrypt"] is not null) throw new TableLayoutException("Encrypted PDFs are unsupported for table extraction.");
            var layout = PdfLayoutReader.Read(pdf, new PdfLayoutOptions
            {
                MaxPages = 200,
                MaxLetters = 500_000,
                MaxLettersPerPage = 100_000,
                MaxWords = 200_000,
                MaxWordsPerPage = 50_000,
                MaxPathCommands = 10_000,
                MaxDecodedStreamBytes = 16 * 1024 * 1024,
                MaxTotalDecodedBytes = 128L * 1024 * 1024,
                SkipRotatedText = true
            }, ct);
            if (layout.RotatedText > 0) throw new TableLayoutException("Rotated or vertical PDF table text is unsupported.");
            return layout.Pages.Select(page => new PositionedPage(page.Number,
                page.Blocks.SelectMany(block => block.Lines).SelectMany(line => line.Words)
                    .Select(w => new PositionedWord(w.Text, w.Left, w.Bottom, w.Right, w.Top)).ToArray(),
                page.Rules.Select(r => new TableRule(r.X1, r.Y1, r.X2, r.Y2)).ToArray())).ToArray();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TableLayoutException or OutOfMemoryException))
        {
            throw new TableLayoutException("The PDF is malformed, encrypted, exceeds its limits or uses unsupported text/stream features.");
        }
    }
}
