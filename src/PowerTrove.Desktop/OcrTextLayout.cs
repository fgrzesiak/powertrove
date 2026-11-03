using PowerTrove.Abstractions.Ocr;

namespace PowerTrove.Desktop;

/// <summary>A word box in image pixels; every word of a line shares the line's top and height.</summary>
internal readonly record struct OcrBox(double X, double Y, double Width, double Height);

/// <summary>
/// Reading-order geometry of recognized text, so the reader can select it like text in an editor:
/// words are numbered across lines, a drag selects the range between two words.
/// </summary>
internal sealed class OcrTextLayout
{
    private readonly record struct Word(string Text, int Line, OcrBox Box);
    private readonly record struct Band(double Top, double Bottom, int First, int Last);

    private readonly List<Word> _words = [];
    private readonly List<Band> _lines = [];
    private readonly Dictionary<int, int> _bandOfLine = [];

    public OcrTextLayout(IReadOnlyList<RecognizedLine> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            var words = lines[index].Words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0 && w.Height > 0)
                .OrderBy(w => w.X).ToArray();
            if (words.Length == 0) continue;
            _bandOfLine[index] = _lines.Count;
            double top = words.Min(w => w.Y), bottom = words.Max(w => w.Y + w.Height);
            // A little air around the glyphs, scaled to the line so marks look alike at any size.
            double pad = (bottom - top) * .15;
            int first = _words.Count;
            foreach (var word in words)
                _words.Add(new Word(word.Text, _lines.Count,
                    new OcrBox(word.X - pad / 2, top - pad, word.Width + pad, bottom - top + 2 * pad)));
            _lines.Add(new Band(top - pad, bottom + pad, first, _words.Count - 1));
        }
    }

    public int Count => _words.Count;

    public OcrBox Box(int index) => _words[index].Box;

    /// <summary>The word under or nearest to a point: first the closest line, then the closest word in it.</summary>
    public int HitTest(double x, double y)
    {
        if (_lines.Count == 0) return -1;
        var line = _lines.MinBy(band => y < band.Top ? band.Top - y : y > band.Bottom ? y - band.Bottom : 0);
        int best = line.First;
        double distance = double.MaxValue;
        for (int i = line.First; i <= line.Last; i++)
        {
            var box = _words[i].Box;
            double d = x < box.X ? box.X - x : x > box.X + box.Width ? x - box.X - box.Width : 0;
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    /// <summary>All words of the line that contains the word.</summary>
    public (int First, int Last) LineOf(int index)
    {
        var band = _lines[_words[index].Line];
        return (band.First, band.Last);
    }

    /// <summary>The words of a recognized line by its position in the OCR result; null when it has no word boxes.</summary>
    public (int First, int Last)? LineRange(int line) =>
        _bandOfLine.TryGetValue(line, out int band) ? (_lines[band].First, _lines[band].Last) : null;

    /// <summary>One box per line that covers the selected words of that line.</summary>
    public IEnumerable<OcrBox> SelectionBoxes(int from, int to)
    {
        (from, to) = Order(from, to);
        for (int i = from; i <= to;)
        {
            int line = _words[i].Line, last = Math.Min(to, _lines[line].Last);
            var first = _words[i].Box;
            var end = _words[last].Box;
            yield return new OcrBox(first.X, first.Y, end.X + end.Width - first.X, first.Height);
            i = last + 1;
        }
    }

    /// <summary>The selected words as text: spaces inside a line, line breaks between lines.</summary>
    public string Text(int from, int to)
    {
        (from, to) = Order(from, to);
        var text = new System.Text.StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (i > from)
            {
                if (_words[i].Line != _words[i - 1].Line) text.Append(Environment.NewLine);
                else if (NeedsSpace(_words[i - 1].Text, _words[i].Text)) text.Append(' ');
            }
            text.Append(_words[i].Text);
        }
        return text.ToString();
    }

    private (int, int) Order(int from, int to)
    {
        if (from > to) (from, to) = (to, from);
        if (from < 0 || to >= _words.Count) throw new ArgumentOutOfRangeException(nameof(to));
        return (from, to);
    }

    // Windows OCR returns Chinese and Japanese characters as separate words that are written without spaces.
    private static bool NeedsSpace(string before, string after) => !IsCjk(before[^1]) && !IsCjk(after[0]);

    private static bool IsCjk(char c) =>
        c is >= '　' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}
