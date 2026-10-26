using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ContextBar.Abstractions.Ocr;

public sealed record RecognizedWord(string Text, double X, double Y, double Width, double Height);
public sealed record RecognizedLine(string Text, IReadOnlyList<RecognizedWord> Words);

/// <summary>Local OCR capability. Recognized content is returned only to the explicit caller.</summary>
public interface ITextRecognizer
{
    IReadOnlyList<string> AvailableLanguages { get; }
    Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(int width, int height, byte[] bgra32, string? language, CancellationToken ct);
}

public static class OcrCoordinateMapping
{
    public static RecognizedWord ToOriginal(RecognizedWord word, int originalWidth, int originalHeight, int scaledWidth, int scaledHeight)
    {
        if (originalWidth < 1 || originalHeight < 1 || scaledWidth < 1 || scaledHeight < 1 ||
            !double.IsFinite(word.X) || !double.IsFinite(word.Y) || !double.IsFinite(word.Width) || !double.IsFinite(word.Height) || word.Width < 0 || word.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(word));
        double sx = (double)originalWidth / scaledWidth, sy = (double)originalHeight / scaledHeight;
        double x = Math.Clamp(word.X * sx, 0, originalWidth), y = Math.Clamp(word.Y * sy, 0, originalHeight);
        return word with { X = x, Y = y, Width = Math.Clamp((word.X + word.Width) * sx, x, originalWidth) - x, Height = Math.Clamp((word.Y + word.Height) * sy, y, originalHeight) - y };
    }
}
