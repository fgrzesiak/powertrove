using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

/// <summary>
/// Interprets PDF text, path and XObject painting operators without external libraries, and groups the glyphs into
/// words, lines and blocks in reading order. Columns are found by recursive XY cuts over the blocks.
/// </summary>
public sealed class PdfLayoutReader
{
    private readonly PdfDocument _pdf;
    private readonly PdfLayoutOptions _limits;
    private readonly CancellationToken _ct;
    private long _decoded;
    private int _letters, _words, _images, _skippedImages, _skippedText, _rotatedText;
    private long _imageBytes;
    private readonly Dictionary<PdfDictionary, LayoutFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfStream> _openForms = new(ReferenceEqualityComparer.Instance);

    private PdfLayoutReader(PdfDocument pdf, PdfLayoutOptions options, CancellationToken token)
    {
        _pdf = pdf;
        _limits = options;
        _ct = token;
    }

    public static PdfLayoutResult Read(PdfDocument pdf, PdfLayoutOptions? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new PdfLayoutReader(pdf, limits ?? new PdfLayoutOptions(), cancellationToken).ReadAll();
    }

    private PdfLayoutResult ReadAll()
    {
        if (_pdf.PageCount > _limits.MaxPages) throw new PdfLimitExceededException("Page count exceeds the layout limit.");
        var pages = new List<PdfLayoutPage>(_pdf.PageCount);
        var destinations = new PdfDestinations(_pdf);
        foreach (var page in _pdf.Pages)
        {
            _ct.ThrowIfCancellationRequested();
            var resources = _pdf.ResolveDictionary(page.Resources) ?? new PdfDictionary();
            var canvas = new Canvas();
            int beforeImages = _skippedImages;
            Interpret(PageContent(page.Dictionary["Contents"]), resources, new State(), canvas, 0);
            var words = GroupWords(canvas.Glyphs, canvas.SpaceBefore, _ct);
            _words += words.Count;
            if (_words > _limits.MaxWords || words.Count > _limits.MaxWordsPerPage)
                throw new PdfLimitExceededException("PDF word limit exceeded.");
            double[] rect = Rectangle(_pdf, page.CropBox ?? page.MediaBox);
            pages.Add(new PdfLayoutPage(page.Index + 1, rect[2] - rect[0], rect[3] - rect[1],
                canvas.Glyphs, PdfReadingOrder.Blocks(words, _ct), canvas.Rules, canvas.Images, _skippedImages - beforeImages,
                PdfLinks.Read(_pdf, page, destinations, _ct)));
        }
        return new PdfLayoutResult(pages, _skippedText, _skippedImages, _rotatedText);
    }

    private static double[] Rectangle(PdfDocument pdf, PdfObject? value)
    {
        var array = pdf.Resolve(value) as PdfArray;
        return array is { Count: 4 } ? array.Items.Select(x => Number(pdf.Resolve(x))).ToArray() : [0, 0, 612, 792];
    }

    /// <summary>A content array is one stream split in parts; a token may continue in the next part.</summary>
    private byte[] PageContent(PdfObject? content)
    {
        var resolved = _pdf.Resolve(content);
        if (resolved is PdfStream stream) return DecodeCounted(stream);
        if (resolved is not PdfArray list) return [];
        var joined = new List<byte>();
        foreach (var value in list.Items)
        {
            if (_pdf.Resolve(value) is not PdfStream part) continue;
            joined.AddRange(DecodeCounted(part));
            joined.Add((byte)'\n');
        }
        return joined.ToArray();
    }

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity, Tm = PdfMatrix.Identity, Tlm = PdfMatrix.Identity;
        public LayoutFont? Font;
        public double FontSize = 1, CharacterSpacing, WordSpacing, HorizontalScaling = 1, Leading, Rise;
        public int RenderMode;
        public State Copy() => (State)MemberwiseClone();
    }

    /// <summary>What a page paints, in content order. <see cref="SpaceBefore"/> marks glyphs after a shown space.</summary>
    private sealed class Canvas
    {
        public readonly List<PdfLayoutGlyph> Glyphs = [];
        public readonly List<bool> SpaceBefore = [];
        public readonly List<PdfLayoutRule> Rules = [];
        public readonly List<PdfLayoutImage> Images = [];
        public bool PendingSpace;
        /// <summary>Open marked-content sequences (BMC/BDC to EMC), innermost last; forms continue the page's.</summary>
        public readonly List<(int? Mcid, bool Artifact)> Marked = [];
        public int? Mcid => Marked.Count == 0 ? null : Marked.FindLast(m => m.Mcid is not null).Mcid;
        public bool Artifact => Marked.Exists(m => m.Artifact);
    }

    private void Interpret(byte[] bytes, PdfDictionary resources, State initial, Canvas canvas, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        if (depth > _limits.MaxFormDepth) throw new PdfLimitExceededException("Form XObject nesting limit exceeded.");
        var operands = new List<PdfObject>(32);
        var stack = new Stack<State>();
        var state = initial.Copy();
        var path = new List<(double X, double Y, double XX, double YY)>();
        (double X, double Y)? current = null, subStart = null;
        int ops = 0, paths = 0;
        foreach (var item in Tokens(bytes, _ct))
        {
            _ct.ThrowIfCancellationRequested();
            if (item is PdfObject operand)
            {
                if (operands.Count >= 100_000) throw new PdfLimitExceededException("Too many content operands.");
                operands.Add(operand);
                continue;
            }
            if (item is not string op) continue;
            if (++ops > 5_000_000) throw new PdfLimitExceededException("Content operator count exceeded.");
            int n = operands.Count;
            double At(int fromEnd) => n >= fromEnd ? Number(operands[^fromEnd]) : 0;
            switch (op)
            {
                case "q":
                    if (stack.Count >= 64) throw new PdfLimitExceededException("Graphics state depth exceeded.");
                    stack.Push(state.Copy());
                    break;
                case "Q":
                    if (stack.Count > 0) state = stack.Pop();
                    break;
                case "cm":
                    if (n >= 6) state.Ctm = state.Ctm.Multiply(PdfMatrix.From(operands));
                    break;
                case "BT":
                    state.Tm = state.Tlm = PdfMatrix.Identity;
                    break;
                case "Tf":
                    if (n >= 2 && operands[^2] is PdfName fontName)
                    {
                        state.Font = Font(resources, fontName.Value);
                        state.FontSize = At(1);
                    }
                    break;
                case "Tc": state.CharacterSpacing = At(1); break;
                case "Tw": state.WordSpacing = At(1); break;
                case "Tz": state.HorizontalScaling = At(1) / 100.0; break;
                case "TL": state.Leading = At(1); break;
                case "Ts": state.Rise = At(1); break;
                case "Tr": state.RenderMode = (int)At(1); break;
                case "Tm":
                    if (n >= 6) state.Tm = state.Tlm = PdfMatrix.From(operands);
                    break;
                case "Td":
                case "TD":
                    if (n >= 2)
                    {
                        if (op == "TD") state.Leading = -At(1);
                        state.Tlm = state.Tlm.Multiply(PdfMatrix.Translation(At(2), At(1)));
                        state.Tm = state.Tlm;
                    }
                    break;
                case "T*":
                    NewLine(state);
                    break;
                case "Tj":
                    if (n >= 1 && operands[^1] is PdfString single) Show(single.Value, state, canvas);
                    break;
                case "TJ":
                    if (n >= 1 && operands[^1] is PdfArray array)
                        foreach (var token in array.Items)
                        {
                            if (token is PdfString text) Show(text.Value, state, canvas);
                            else state.Tm = state.Tm.Multiply(PdfMatrix.Translation(-Number(token) / 1000.0 * state.FontSize * state.HorizontalScaling, 0));
                        }
                    break;
                case "'":
                    NewLine(state);
                    if (n >= 1 && operands[^1] is PdfString quote) Show(quote.Value, state, canvas);
                    break;
                case "\"":
                    if (n >= 3)
                    {
                        state.WordSpacing = At(3);
                        state.CharacterSpacing = At(2);
                        NewLine(state);
                        if (operands[^1] is PdfString quoted) Show(quoted.Value, state, canvas);
                    }
                    break;
                case "Do":
                    if (n >= 1 && operands[^1] is PdfName xobject) InvokeXObject(resources, xobject.Value, state, canvas, depth);
                    break;
                case "BMC":
                case "BDC":
                    if (canvas.Marked.Count >= 256) throw new PdfLimitExceededException("Marked-content nesting limit exceeded.");
                    canvas.Marked.Add(MarkedContent(op, operands, resources));
                    break;
                case "EMC":
                    if (canvas.Marked.Count > 0) canvas.Marked.RemoveAt(canvas.Marked.Count - 1);
                    break;
                case "m":
                    if (n >= 2) current = subStart = state.Ctm.Point(At(2), At(1));
                    break;
                case "l":
                    if (n >= 2 && current.HasValue)
                    {
                        var end = state.Ctm.Point(At(2), At(1));
                        path.Add((current.Value.X, current.Value.Y, end.X, end.Y));
                        current = end;
                    }
                    break;
                case "re":
                    if (n >= 4)
                    {
                        var a = state.Ctm.Point(At(4), At(3));
                        var b = state.Ctm.Point(At(4) + At(2), At(3));
                        var c = state.Ctm.Point(At(4) + At(2), At(3) + At(1));
                        var d = state.Ctm.Point(At(4), At(3) + At(1));
                        path.Add((a.X, a.Y, b.X, b.Y));
                        path.Add((b.X, b.Y, c.X, c.Y));
                        path.Add((c.X, c.Y, d.X, d.Y));
                        path.Add((d.X, d.Y, a.X, a.Y));
                        current = subStart = a;
                    }
                    break;
                case "h":
                    if (current.HasValue && subStart.HasValue)
                    {
                        path.Add((current.Value.X, current.Value.Y, subStart.Value.X, subStart.Value.Y));
                        current = subStart;
                    }
                    break;
                case "s":
                case "b":
                case "b*":
                    if (current.HasValue && subStart.HasValue)
                        path.Add((current.Value.X, current.Value.Y, subStart.Value.X, subStart.Value.Y));
                    goto case "S";
                case "S":
                case "B":
                case "B*":
                    foreach (var (x, y, xx, yy) in path)
                        if (Math.Abs(x - xx) <= 1.5 || Math.Abs(y - yy) <= 1.5) canvas.Rules.Add(new PdfLayoutRule(x, y, xx, yy));
                    path.Clear();
                    current = subStart = null;
                    break;
                case "n":
                case "f":
                case "f*":
                case "F":
                    path.Clear();
                    current = subStart = null;
                    break;
                // Curves shape a path but are never orthogonal table borders.
                case "c":
                case "v":
                case "y":
                    current = null;
                    break;
            }
            if (op is "m" or "l" or "re" or "h" or "c" or "v" or "y" && ++paths > _limits.MaxPathCommands)
            {
                if (!_limits.IgnoreExcessPaths) throw new PdfLimitExceededException("PDF path complexity limit exceeded.");
                path.Clear();
                current = subStart = null;
            }
            operands.Clear();
            if (canvas.Glyphs.Count > _limits.MaxLettersPerPage || _letters > _limits.MaxLetters)
                throw new PdfLimitExceededException("PDF text complexity limit exceeded.");
        }
    }

    /// <summary>The MCID and artifact flag of a BMC or BDC; BDC properties may be inline or named in /Properties.</summary>
    private (int? Mcid, bool Artifact) MarkedContent(string op, List<PdfObject> operands, PdfDictionary resources)
    {
        int n = operands.Count;
        string? tag = op == "BMC" ? (n >= 1 ? (operands[^1] as PdfName)?.Value : null) : (n >= 2 ? (operands[^2] as PdfName)?.Value : null);
        PdfDictionary? properties = op != "BDC" || n < 1 ? null : operands[^1] switch
        {
            PdfDictionary inline => inline,
            PdfName name => _pdf.ResolveDictionary(_pdf.ResolveDictionary(resources["Properties"])?[name.Value]),
            _ => null
        };
        int? mcid = _pdf.Resolve(properties?["MCID"]) is PdfInteger number && number.Value is >= 0 and <= int.MaxValue ? (int)number.Value : null;
        return (mcid, tag == "Artifact");
    }

    private void InvokeXObject(PdfDictionary resources, string key, State state, Canvas canvas, int depth)
    {
        var xobjects = _pdf.ResolveDictionary(resources["XObject"]);
        if (xobjects is null || _pdf.Resolve(xobjects[key]) is not PdfStream stream) return;
        switch (stream.Dictionary.GetName("Subtype"))
        {
            case "Form":
            {
                if (!_openForms.Add(stream)) throw new PdfFormatException("Cyclic Form XObject references.");
                try
                {
                    var copy = state.Copy();
                    if (_pdf.Resolve(stream.Dictionary["Matrix"]) is PdfArray { Count: 6 } matrix)
                        copy.Ctm = copy.Ctm.Multiply(PdfMatrix.From(matrix.Items));
                    var next = _pdf.ResolveDictionary(stream.Dictionary["Resources"]) ?? resources;
                    Interpret(DecodeCounted(stream), next, copy, canvas, depth + 1);
                }
                finally { _openForms.Remove(stream); }
                break;
            }
            case "Image":
            {
                if (!_limits.IncludeImages) return;
                if (_images >= _limits.MaxImages)
                {
                    _skippedImages++;
                    return;
                }
                var a = state.Ctm.Point(0, 0);
                var b = state.Ctm.Point(1, 0);
                var c = state.Ctm.Point(0, 1);
                var d = state.Ctm.Point(1, 1);
                double left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
                double right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
                double bottom = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
                double top = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
                if (right - left < _limits.MinimumImageSize || top - bottom < _limits.MinimumImageSize) return;
                try
                {
                    var result = PdfLayoutRaster.TryDecode(_pdf, stream, _limits, _ct, ChargeDecoded);
                    if (result is null || _imageBytes + result.Bytes.Length > _limits.MaxImageBytes)
                    {
                        _skippedImages++;
                        return;
                    }
                    canvas.Images.Add(new PdfLayoutImage(left, bottom, right, top, result.Bytes, result.Extension, canvas.Mcid, canvas.Artifact));
                    _imageBytes += result.Bytes.Length;
                    _images++;
                }
                catch (Exception ex) when (ex is PdfLimitExceededException or PdfFormatException)
                {
                    _skippedImages++;
                }
                break;
            }
        }
    }

    private LayoutFont? Font(PdfDictionary resources, string name)
    {
        var fonts = _pdf.ResolveDictionary(resources["Font"]);
        var dictionary = fonts is null ? null : _pdf.ResolveDictionary(fonts[name]);
        if (dictionary is null) return null;
        if (!_fonts.TryGetValue(dictionary, out var font))
            _fonts.Add(dictionary, font = new LayoutFont(_pdf, dictionary, _ct, DecodeCounted));
        return font;
    }

    private static void NewLine(State state)
    {
        state.Tlm = state.Tlm.Multiply(PdfMatrix.Translation(0, -state.Leading));
        state.Tm = state.Tlm;
    }

    private void Show(byte[] bytes, State state, Canvas canvas)
    {
        if (state.Font is null)
        {
            // A missing font cannot be measured or decoded; its text is only counted.
            _skippedText += bytes.Length;
            return;
        }
        foreach (var (text, width, wordSpace) in state.Font.Decode(bytes))
        {
            _ct.ThrowIfCancellationRequested();
            double advance = (width * state.FontSize + state.CharacterSpacing + (wordSpace ? state.WordSpacing : 0)) * state.HorizontalScaling;
            var transform = state.Ctm.Multiply(state.Tm);
            var a = transform.Point(0, state.Rise);
            var b = transform.Point(advance, state.Rise);
            var h = transform.Point(0, state.FontSize + state.Rise);
            state.Tm = state.Tm.Multiply(PdfMatrix.Translation(advance, 0));
            if (string.IsNullOrWhiteSpace(text))
            {
                if (text is null) _skippedText++;
                else canvas.PendingSpace = true;
                continue;
            }
            double size = Math.Sqrt((h.X - a.X) * (h.X - a.X) + (h.Y - a.Y) * (h.Y - a.Y));
            bool horizontal = Math.Abs(b.Y - a.Y) < Math.Max(.5, size * .1) && Math.Abs(h.X - a.X) < Math.Max(.5, size * .1) && h.Y > a.Y;
            if (!horizontal)
            {
                _skippedText++;
                _rotatedText++;
                if (_limits.SkipRotatedText) continue;
            }
            double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, h.Y), top = Math.Max(a.Y, h.Y);
            if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(bottom) || !double.IsFinite(top)) continue;
            canvas.Glyphs.Add(new PdfLayoutGlyph(text, left, bottom, right, top, Math.Round(size, 2),
                state.Font.Bold, state.Font.Italic, state.Font.Mono, horizontal, canvas.Mcid, canvas.Artifact));
            canvas.SpaceBefore.Add(canvas.PendingSpace);
            canvas.PendingSpace = false;
            if (++_letters > _limits.MaxLetters) throw new PdfLimitExceededException("PDF letter count exceeded.");
        }
    }

    internal static double Number(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => 0
    };

    internal static byte[] Decode(PdfDocument doc, PdfStream stream, PdfLayoutOptions limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stream.Data.LongLength > limits.MaxDecodedStreamBytes)
            throw new PdfLimitExceededException("PDF stream exceeds per-stream size limit.");
        byte[] content = PdfFilters.Decode(stream, doc.Resolve, limits.MaxDecodedStreamBytes);
        ct.ThrowIfCancellationRequested();
        return content;
    }

    private void ChargeDecoded(long count)
    {
        _ct.ThrowIfCancellationRequested();
        _decoded = checked(_decoded + count);
        if (_decoded > _limits.MaxTotalDecodedBytes)
            throw new PdfLimitExceededException("PDF total decoded stream budget exceeded.");
    }

    private byte[] DecodeCounted(PdfStream stream)
    {
        byte[] bytes = Decode(_pdf, stream, _limits, _ct);
        ChargeDecoded(bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Operands and operators of a content stream. Inline image data is skipped, and a malformed tail ends the
    /// stream instead of failing the page, as viewers do.
    /// </summary>
    internal static List<object> Tokens(byte[] bytes, CancellationToken ct)
    {
        var parser = new PdfParser(bytes, new PdfReadLimits { MaxNestingDepth = 64, MaxDecodedStreamBytes = 32 * 1024 * 1024 });
        var list = new List<object>();
        int count = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 5_000_000) throw new PdfLimitExceededException("PDF content token count exceeded.");
                parser.SkipWhitespaceAndComments();
                if (parser.AtEnd) break;
                if (!PdfParser.IsRegular(bytes[parser.Position]))
                {
                    list.Add(parser.ParseObject(0));
                    continue;
                }
                string token = parser.ReadKeyword();
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
                    list.Add(new PdfReal(numeric));
                else if (token == "ID")
                {
                    parser.Position = InlineImageEnd(bytes, parser.Position + 1);
                    list.Add("EI");
                }
                else list.Add(token);
            }
        }
        catch (PdfFormatException)
        {
        }
        return list;
    }

    /// <summary>Inline image data ends at "EI" between whitespace (or at the end of the stream).</summary>
    private static int InlineImageEnd(byte[] bytes, int start)
    {
        for (int i = Math.Max(start, 1); i + 1 < bytes.Length; i++)
            if (bytes[i] == 'E' && bytes[i + 1] == 'I' && PdfParser.IsWhitespace(bytes[i - 1]) &&
                (i + 2 == bytes.Length || PdfParser.IsWhitespace(bytes[i + 2]) || bytes[i + 2] == '%'))
                return i + 2;
        return bytes.Length;
    }

    /// <summary>
    /// Words from glyphs in content order: a word ends at a shown space that really leaves a gap, at a gap wider than
    /// a tenth of the font size after a space (or 15% without one), or where the text jumps to another line or back.
    /// </summary>
    private static List<PdfLayoutWord> GroupWords(List<PdfLayoutGlyph> glyphs, List<bool> spaceBefore, CancellationToken ct)
    {
        var output = new List<PdfLayoutWord>();
        var word = new List<PdfLayoutGlyph>();
        for (int i = 0; i < glyphs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var glyph = glyphs[i];
            if (!glyph.Horizontal) continue;
            if (word.Count > 0)
            {
                var prior = word[^1];
                double size = Math.Max(1, Math.Max(prior.FontSize, glyph.FontSize));
                double gap = glyph.Left - prior.Right;
                bool sameLine = Math.Abs(glyph.Bottom - prior.Bottom) < size * .3 ||
                    glyph.Bottom < prior.Top && glyph.Top > prior.Bottom && Math.Min(glyph.FontSize, prior.FontSize) < size * .8;
                // A word never spans two marked-content sequences, so each word belongs to one structure element.
                if (!sameLine || gap > size * (spaceBefore[i] ? .1 : .15) || gap < -size * .5 || glyph.Mcid != prior.Mcid || glyph.Artifact != prior.Artifact)
                    Flush();
            }
            word.Add(glyph);
        }
        Flush();
        return output;

        void Flush()
        {
            if (word.Count == 0) return;
            // Trailing glyphs set smaller and above the first glyph's baseline are a superscript (a footnote mark).
            var first = word[0];
            int raised = word.Count;
            while (raised > 1 && word[raised - 1].FontSize <= first.FontSize * .85 && word[raised - 1].Bottom > first.Bottom + first.FontSize * .2)
                raised--;
            var builder = new StringBuilder();
            int raisedFrom = -1;
            for (int g = 0; g < word.Count; g++)
            {
                if (g == raised) raisedFrom = builder.Length;
                builder.Append(word[g].Text);
            }
            var sizes = word.Select(g => g.FontSize).Order().ToArray();
            output.Add(new PdfLayoutWord(builder.ToString(), word.Min(g => g.Left), word.Min(g => g.Bottom),
                word.Max(g => g.Right), word.Max(g => g.Top), Math.Round(sizes[sizes.Length / 2], 1),
                first.Bold, first.Italic, first.Monospace, first.Mcid, first.Artifact, raisedFrom));
            word.Clear();
        }
    }
}
