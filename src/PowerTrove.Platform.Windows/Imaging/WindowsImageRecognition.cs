using System.Runtime.InteropServices.WindowsRuntime;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Ocr;
using FileKit.Imaging.Raster;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace PowerTrove.Platform.Windows.Imaging;

internal static class RecognitionBitmap
{
    public static async Task<(SoftwareBitmap Bitmap, int Width, int Height)> CreateAsync(int inputWidth, int inputHeight, byte[] inputPixels, int maxSide, CancellationToken ct)
    {
        var source = new RasterImage(inputWidth, inputHeight, inputPixels);
        if (maxSide < 1) throw new InvalidDataException();
        double scale = Math.Min(1, (double)maxSide / Math.Max(inputWidth, inputHeight));
        int width = Math.Max(1, (int)Math.Round(inputWidth * scale)), height = Math.Max(1, (int)Math.Round(inputHeight * scale));
        var opaque = await Task.Run(() =>
        {
            var resized = RasterOperations.ResizeBilinear(source, width, height, ct);
            return RasterOperations.Compose(new[] { (resized, 0, 0) }, width, height, new BgraColor(255, 255, 255), ct);
        }, ct).ConfigureAwait(false);
        return (SoftwareBitmap.CreateCopyFromBuffer(opaque.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore), width, height);
    }
}

public sealed class WindowsTextRecognizer : ITextRecognizer
{
    public IReadOnlyList<string> AvailableLanguages
    {
        get { try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToArray(); } catch { return Array.Empty<string>(); } }
    }

    // Existing pack and PDF calls retain Windows' user-profile language, avoiding a
    // potentially expensive multi-language pass over every page of a PDF or batch.
    public async Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(int width, int height, byte[] bgra32, string? language, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var engine = language is null ? OcrEngine.TryCreateFromUserProfileLanguages()
                : OcrEngine.TryCreateFromLanguage(new Language(language));
            if (engine is null) throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrUnavailable);
            var frame = await RecognitionBitmap.CreateAsync(width, height, bgra32,
                (int)Math.Min(30000u, OcrEngine.MaxImageDimension), ct).ConfigureAwait(false);
            using var bitmap = frame.Bitmap;
            return await ReadAsync(engine, bitmap, frame.Width, frame.Height, width, height, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrFailed, ex); }
    }

    public async Task<RecognizedPage> RecognizePageAsync(int width, int height, byte[] bgra32,
        string? language, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var frame = await RecognitionBitmap.CreateAsync(width, height, bgra32,
                (int)Math.Min(30000u, OcrEngine.MaxImageDimension), ct).ConfigureAwait(false);
            using var bitmap = frame.Bitmap;
            if (!string.IsNullOrWhiteSpace(language))
            {
                var selected = OcrEngine.TryCreateFromLanguage(new Language(language))
                    ?? throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrUnavailable);
                return new(await ReadAsync(selected, bitmap, frame.Width, frame.Height, width, height, ct).ConfigureAwait(false),
                    selected.RecognizerLanguage.LanguageTag);
            }

            // Unlike TryCreateFromUserProfileLanguages, Windows OCR cannot detect a
            // language by itself. Compare outputs of installed engines locally instead.
            var candidates = OcrLanguageHeuristics.CandidateLanguages(AvailableLanguages,
                ApplicationLanguages.Languages);
            if (candidates.Length == 0)
                throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrUnavailable);
            RecognizedPage? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (string candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var engine = OcrEngine.TryCreateFromLanguage(new Language(candidate));
                if (engine is null) continue;
                IReadOnlyList<RecognizedLine> lines;
                try
                {
                    lines = await ReadAsync(engine, bitmap, frame.Width, frame.Height, width, height, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException &&
                    ex is not OutOfMemoryException) { continue; }

                var score = OcrLanguageHeuristics.Score(candidate, lines);
                if (best is null || score > bestScore)
                {
                    best = new RecognizedPage(lines, candidate);
                    bestScore = score;
                }
            }
            if (best is null)
                throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrUnavailable);
            return double.IsNegativeInfinity(bestScore) ? new RecognizedPage([], null) : best;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileProcessingException)
        { throw new FileProcessingException(PowerTrove.Platform.Windows.Resources.Strings.OcrFailed, ex); }
    }

    private static async Task<IReadOnlyList<RecognizedLine>> ReadAsync(OcrEngine engine, SoftwareBitmap bitmap,
        int scaledWidth, int scaledHeight, int width, int height, CancellationToken ct)
    {
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        if (result.Lines.Count > 50000 || result.Lines.Sum(l => (long)l.Words.Count) > 50000)
            throw new InvalidDataException();
        var lines = result.Lines.Select(line => new RecognizedLine(line.Text,
            line.Words.Select(word =>
            {
                var box = word.BoundingRect;
                return OcrCoordinateMapping.ToOriginal(new RecognizedWord(
                    word.Text, box.X, box.Y, box.Width, box.Height), width, height, scaledWidth, scaledHeight);
            }).ToArray())).ToArray();
        if (lines.Sum(l => (long)l.Text.Length) > 2_000_000) throw new InvalidDataException();
        return lines;
    }
}
