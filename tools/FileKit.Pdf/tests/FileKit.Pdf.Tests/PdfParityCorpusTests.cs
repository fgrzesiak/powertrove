using System.Text;
using System.Text.Json;

namespace FileKit.Pdf.Tests;

/// <summary>
/// Opt-in acceptance against a local, externally prepared PdfPig reading-order corpus.
/// Baselines are produced from a pinned PdfPig release before replacing the adapter.
/// This test has no PdfPig dependency and performs no network access.
/// </summary>
public sealed class PdfParityCorpusTests
{
    [Fact]
    public void LocallySuppliedPdfPigCorpusMeetsWordAndReadingOrderThreshold()
    {
        string? root = Environment.GetEnvironmentVariable("POWERTROVE_PDF_PARITY_CORPUS");
        if (string.IsNullOrWhiteSpace(root))
            return; // Optional local corpus: no fixtures or reference dependency in ordinary builds.

        Assert.True(Directory.Exists(root), "Configured PDF parity corpus is missing.");
        string[] pdfs = Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories);
        Assert.True(pdfs.Length >= 5, "Require at least five representative research-paper PDFs.");

        var categories = pdfs.Select(Path.GetFileNameWithoutExtension).ToArray();
        foreach (string category in new[] { "one-column", "two-column", "no-tounicode", "images", "tables" })
            Assert.Contains(categories, name => name.Contains(category, StringComparison.OrdinalIgnoreCase));

        foreach (string file in pdfs)
        {
            string baseline = file + ".pdfpig.words.json";
            Assert.True(File.Exists(baseline), "Missing pinned PdfPig word baseline: " + baseline);
            string[][] expected = JsonSerializer.Deserialize<string[][]>(File.ReadAllText(baseline))
                ?? throw new InvalidDataException("Invalid word baseline: " + baseline);
            byte[] bytes = File.ReadAllBytes(file);
            var pdf = PdfDocument.Load(bytes, new PdfReadLimits
            {
                MaxFileBytes = 100 * 1024 * 1024,
                MaxPages = 1000,
                MaxObjects = 500_000,
                MaxNestingDepth = 64,
                MaxDecodedStreamBytes = 32 * 1024 * 1024,
                MaxXrefSections = 200
            });
            var actual = PdfLayoutReader.Read(pdf, new PdfLayoutOptions
            {
                MaxPages = 1000,
                MaxLetters = 2_000_000,
                MaxLettersPerPage = 2_000_000,
                MaxWords = 2_000_000,
                MaxWordsPerPage = 2_000_000,
                IncludeImages = true,
                MaxDecodedStreamBytes = 32 * 1024 * 1024,
                MaxTotalDecodedBytes = 128L * 1024 * 1024
            });
            Assert.Equal(expected.Length, actual.Pages.Count);
            for (int page = 0; page < expected.Length; page++)
            {
                string[] reference = expected[page].Select(Normalize).Where(w => w.Length > 0).ToArray();
                string[] extracted = actual.Pages[page].Blocks.SelectMany(b => b.Lines)
                    .SelectMany(l => l.Words).Select(w => Normalize(w.Text))
                    .Where(w => w.Length > 0).ToArray();
                var positions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
                for (int i = 0; i < reference.Length; i++)
                {
                    if (!positions.TryGetValue(reference[i], out var queue))
                        positions[reference[i]] = queue = new Queue<int>();
                    queue.Enqueue(i);
                }
                var matches = new List<int>();
                foreach (string word in extracted)
                    if (positions.TryGetValue(word, out var queue) && queue.Count > 0)
                        matches.Add(queue.Dequeue());

                double recall = reference.Length == 0 ? extracted.Length == 0 ? 1 : 0
                    : (double)matches.Count / reference.Length;
                double precision = extracted.Length == 0 ? reference.Length == 0 ? 1 : 0
                    : (double)matches.Count / extracted.Length;
                double order = matches.Count == 0 ? 1 : (double)LongestIncreasing(matches) / matches.Count;
                Assert.True(recall >= .98 && precision >= .98 && order >= .98,
                    $"{Path.GetFileName(file)} page {page + 1}: " +
                    $"recall={recall:P2} precision={precision:P2} order={order:P2}; " +
                    $"reference={reference.Length} output={extracted.Length}");
            }
        }
    }

    private static string Normalize(string word) =>
        word.Normalize(NormalizationForm.FormKC).Replace("\u00AD", "", StringComparison.Ordinal)
            .Trim().ToLowerInvariant();

    private static int LongestIncreasing(IReadOnlyList<int> indices)
    {
        var tails = new List<int>();
        foreach (int index in indices)
        {
            int at = tails.BinarySearch(index);
            if (at < 0) at = ~at;
            if (at == tails.Count) tails.Add(index);
            else tails[at] = index;
        }
        return tails.Count;
    }
}
