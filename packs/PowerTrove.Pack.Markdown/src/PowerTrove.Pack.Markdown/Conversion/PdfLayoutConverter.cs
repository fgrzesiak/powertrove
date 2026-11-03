using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FileKit.Tabular;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// Rebuilds document structure from a PDF's positioned text. Bookmarks, when they match the text, decide which lines
/// are headings and at which level; otherwise headings come from font sizes above the body text (largest first) or
/// from short bold lines. A table of contents (entries with dot leaders or page numbers) becomes a nested list linked
/// to the headings. Link areas become Markdown links: web and mail addresses directly, places in the document as
/// links to the heading there. Running headers, footers and page numbers that repeat across pages are dropped;
/// bullets and numbers start list items, nested by indentation; lines are joined into paragraphs with hyphenation
/// undone, also across page breaks; monospace lines become code; ruled tables become Markdown tables. Pictures are
/// placed before the first text block that starts below them; a picture repeated on most pages is dropped.
/// The conversion runs twice: the first pass finds the headings and their anchors, the second links to them.
/// </summary>
internal static partial class PdfLayoutConverter
{
    private sealed class Line
    {
        public required int Index { get; init; }
        public required PdfTextPage Page { get; init; }
        public required IReadOnlyList<PdfTextWord> Words { get; init; }
        public string Text => string.Join(' ', Words.Select(w => w.Text));
        public double Left => Words.Min(w => w.Left);
        public double Right => Words.Max(w => w.Right);
        public double Top => Words.Max(w => w.Top);
        public double Bottom => Words.Min(w => w.Bottom);
        public double Size { get; init; }
        public bool Bold => Words.All(w => w.Bold);
        public bool Mono => Words.All(w => w.Monospace);
        public bool Removed { get; set; }
        public string? Table { get; set; }
        /// <summary>The heading level a matched bookmark gives this line, and the heading text when it spans lines.</summary>
        public int? Bookmark { get; set; }
        public string? HeadingText { get; set; }
        /// <summary>Set on table of contents entries; the first entry of each contents list carries the whole list.</summary>
        public TocEntry? Toc { get; set; }
        public List<TocEntry>? TocList { get; set; }
        /// <summary>The structure tag most of the line's words carry in a tagged PDF.</summary>
        public PdfTextTag? Tag { get; init; }
        /// <summary>The footnote numbers of the words in this line that are footnote marks.</summary>
        public Dictionary<PdfTextWord, int>? Marks { get; set; }
    }

    /// <summary>A footnote: its number in the Markdown, its lines (taken out of the text) and the mark its first line starts with.</summary>
    private sealed record Footnote(int Number, List<Line> Lines, string Mark);

    private sealed record TocEntry(Line Line, string Title, int Depth);

    /// <summary>A heading written in the first pass: where it is and the anchor Markdig gives it.</summary>
    private sealed record Anchor(int Line, int Page, double Top, string Slug, string Key, string Bare);

    public static ConvertedMarkdown Convert(PdfTextDocument document, CancellationToken ct)
    {
        // The layout is analysed once and written twice, the first time only to learn the anchors of the headings.
        var layout = Analyze(document, ct);
        var anchors = Render(document, layout, null, ct).Anchors;
        return Render(document, layout, anchors, ct).Markdown;
    }

    /// <summary>The analysed lines by block (marked as removed, table, contents or heading) and what writing them needs.</summary>
    private sealed record Layout(List<List<Line>> Blocks, bool Tagged, bool TaggedHeadings, double Body, (int Deepest, double Largest, int First)? Outline,
        List<double> Levels, Dictionary<int, List<PdfPageImage>> Before, Dictionary<int, List<PdfPageImage>> After, List<PdfPageImage> Leading,
        List<Footnote> Footnotes);

    private static Layout Analyze(PdfTextDocument document, CancellationToken ct)
    {
        var blocks = new List<List<Line>>();
        int count = 0;
        foreach (var page in document.Pages)
            foreach (var block in page.Blocks)
            {
                var lines = block.Lines.Where(l => l.Words.Count > 0)
                    .Select(l => new Line { Index = count++, Page = page, Words = l.Words, Size = Size(l.Words), Tag = TagOf(document.Tags, l.Words) }).ToList();
                if (lines.Count > 0) blocks.Add(lines);
            }
        var all = blocks.SelectMany(b => b).ToList();
        if (all.Count == 0) throw new DocumentFormatException(SR.Get("ToMarkdownPdfNoText"));

        // Content a PDF declares as an artifact (running headers, footers, page numbers) is never text.
        foreach (var line in all.Where(l => l.Words.All(w => w.Artifact))) line.Removed = true;
        bool tagged = Tagged(all);
        bool taggedHeadings = tagged && all.Any(l => !l.Removed && l.Tag?.Kind == PdfTextTagKind.Heading);

        double body = BodySize(all);
        RemoveRunningText(all, document.Pages.Count);
        var footnotes = FindFootnotes(blocks, body, ct);
        if (tagged) FindTaggedTables(all, document.Tags!);
        FindTables(document, all, ct);
        FindContents(blocks, ct);
        var outline = taggedHeadings ? null : MatchBookmarks(document, all, ct);
        if (taggedHeadings) TaggedHeadings(all, body);
        MatchContents(all, ct);
        if (!tagged) FindUnruledTables(blocks, all, ct);
        var levels = all.Where(l => !l.Removed && l.Table is null && l.Toc is null && l.Bookmark is null && l.Size >= body * 1.15 && l.Words.Count <= 15)
            .Select(l => Math.Round(l.Size * 2) / 2).Distinct().OrderByDescending(s => s).Take(4).ToList();
        var (before, after, leading) = PlaceImages(document, blocks);
        return new Layout(blocks, tagged, taggedHeadings, body, outline, levels, before, after, leading, footnotes);
    }

    private static (ConvertedMarkdown Markdown, List<Anchor> Anchors) Render(PdfTextDocument document, Layout layout, IReadOnlyList<Anchor>? anchors, CancellationToken ct)
    {
        var (blocks, tagged, taggedHeadings, body, outline, levels, before, after, leading, footnotes) = layout;
        var written = new List<Anchor>();
        var slugs = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new MarkdownText { OmittedImages = document.SkippedImages };
        void Pictures(IEnumerable<PdfPageImage> images)
        {
            foreach (var image in images) output.Block(output.Image(image.Bytes, image.Extension, image.Alt));
        }
        var anchorsByPage = anchors?.ToLookup(a => a.Page);
        string? Target(PdfTextLink link) => link.Uri ?? (link.TargetPage is int page ? AnchorAt(anchorsByPage, page, link.TargetTop) : null);
        void Heading(Line line, int level, string text)
        {
            string clean = MarkdownText.Clean(text);
            output.Block(new string('#', Math.Clamp(level, 1, 6)) + " " + MarkdownText.Inline(clean));
            written.Add(new Anchor(line.Index, line.Page.Number, line.Top, Slug(clean, slugs), Key(clean), Key(Numbering().Replace(clean, ""))));
        }

        var paragraph = new StringBuilder();
        var code = new List<string>();
        string? listMarker = null;
        double listBase = 0;
        bool previousWasItem = false;
        // In a tagged PDF, the element the current paragraph or list item belongs to.
        PdfTextTag? element = null;

        void FlushParagraph()
        {
            element = null;
            if (paragraph.Length == 0) return;
            string text = paragraph.ToString().Trim();
            paragraph.Clear();
            if (listMarker is not null)
            {
                if (previousWasItem) output.Line(listMarker + text); else output.Block(listMarker + text);
                previousWasItem = true;
                listMarker = null;
                return;
            }
            output.Block(MarkdownText.LineStart(text));
            previousWasItem = false;
        }
        void FlushCode()
        {
            if (code.Count == 0) return;
            output.Block(MarkdownText.Fence(string.Join('\n', code)));
            code.Clear();
            previousWasItem = false;
        }

        Pictures(leading);
        for (int b = 0; b < blocks.Count; b++)
        {
            var lines = blocks[b];
            if (before.TryGetValue(b, out var above))
            {
                FlushParagraph(); FlushCode();
                Pictures(above);
                previousWasItem = false;
            }
            double blockRight = lines.Max(l => l.Right), blockLeft = lines.Min(l => l.Left);
            int visible = lines.Count(l => !l.Removed);
            for (int i = 0; i < lines.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[i];
                if (line.Removed) continue;
                if (line.Table is not null)
                {
                    FlushParagraph(); FlushCode();
                    if (line.Table.Length > 0) { output.Block(line.Table); previousWasItem = false; }
                    continue;
                }
                if (line.TocList is not null)
                {
                    FlushParagraph(); FlushCode();
                    output.Block(Contents(line.TocList, anchors, anchorsByPage));
                    previousWasItem = false;
                    continue;
                }
                if (line.Toc is not null) continue;
                string text = line.Text.Trim();
                if (text.Length == 0) continue;

                if (line.Bookmark is int bookmarked)
                {
                    FlushParagraph(); FlushCode();
                    Heading(line, bookmarked, line.HeadingText ?? text);
                    previousWasItem = false;
                    continue;
                }

                if (line.Mono || tagged && line.Tag?.Kind == PdfTextTagKind.Code) { FlushParagraph(); code.Add(text); continue; }
                FlushCode();

                double rounded = Math.Round(line.Size * 2) / 2;
                int level = levels.IndexOf(rounded) + 1;
                bool shortLine = line.Words.Count <= 15 && !MarkdownText.EndsWithAny(text, ".,;");
                bool heading = !taggedHeadings && (level > 0 && shortLine || level == 0 && line.Bold && shortLine && visible <= 3 && line.Words.Count <= 12
                    && line.Size >= body * 0.95 && !text.EndsWith(':') && !Bullet().IsMatch(text) && (i + 1 >= lines.Count || !lines[i + 1].Bold || lines[i + 1].Size == line.Size));
                if (level == 0) level = Math.Min(levels.Count + 1, 4);
                // Bookmarks that reach section level are the structure; other lines are headings only when larger and
                // before the first bookmarked heading (a title). Chapter-only bookmarks leave the sections to the font
                // sizes, one level below.
                if (heading && outline is var (deepest, largest, firstLine))
                {
                    if (line.Size >= largest + 0.5 && line.Index < firstLine) level = 1;
                    else if (deepest >= 2) heading = false;
                    else level = Math.Min(6, Math.Max(level, deepest + 1));
                }
                if (heading)
                {
                    FlushParagraph();
                    // Consecutive lines of one heading in the same block form one heading.
                    var title = new StringBuilder(text);
                    while (i + 1 < lines.Count && !lines[i + 1].Removed && lines[i + 1].Bookmark is null && Math.Abs(lines[i + 1].Size - line.Size) < 0.5
                           && lines[i + 1].Bold == line.Bold && lines[i + 1].Words.Count <= 15 && title.Length < 200)
                        title.Append(' ').Append(lines[++i].Text.Trim());
                    Heading(line, level, title.ToString());
                    previousWasItem = false;
                    continue;
                }

                // Tagged list items: the item element decides where an item starts and how deep it is nested. Numbered
                // headings that Word tags as list items were caught as headings above.
                if (tagged && line.Tag is { Kind: PdfTextTagKind.ListItem } item)
                {
                    if (ReferenceEquals(item, element))
                    {
                        Append(paragraph, Words(line, null, Target));
                        continue;
                    }
                    FlushParagraph();
                    var label = Bullet().Match(text);
                    if (!label.Success) label = Number().Match(text);
                    bool numbered = label.Success && label.Groups["n"].Success;
                    // A label drawn in a symbol font (a private-use or other non-letter glyph) is a bullet too.
                    string? marker = label.Success ? label.Value.Trim() : line.Words[0].Text.Length <= 2 && !line.Words[0].Text.Any(char.IsLetterOrDigit) && line.Words.Count > 1 ? line.Words[0].Text : null;
                    listMarker = new string(' ', (item.Level - 1) * 3) + (numbered ? label.Groups["n"].Value + ". " : "- ");
                    paragraph.Append(Words(line, marker, Target));
                    element = item;
                    continue;
                }
                // Tagged paragraphs: lines of one element form one paragraph, a new element starts the next one.
                if (tagged && line.Tag is { Kind: PdfTextTagKind.Paragraph } part && !ReferenceEquals(part, element) && paragraph.Length > 0)
                    FlushParagraph();

                var bullet = Bullet().Match(text);
                if (!bullet.Success) bullet = Number().Match(text);
                if (bullet.Success && !(tagged && line.Tag?.Kind == PdfTextTagKind.Paragraph))
                {
                    FlushParagraph();
                    bool ordered = bullet.Groups["n"].Success;
                    if (!previousWasItem) listBase = line.Left;
                    int depth = Math.Clamp((int)Math.Round((line.Left - listBase) / 18.0), 0, 3);
                    listMarker = new string(' ', depth * (ordered ? 3 : 2)) + (ordered ? bullet.Groups["n"].Value + ". " : "- ");
                    paragraph.Append(Words(line, bullet.Value.Trim(), Target));
                    continue;
                }

                if (paragraph.Length > 0 && !(tagged && line.Tag is { Kind: PdfTextTagKind.Paragraph or PdfTextTagKind.ListItem } && ReferenceEquals(line.Tag, element)))
                {
                    Line? previous = null;
                    for (int k = i - 1; k >= 0 && previous is null; k--) if (!lines[k].Removed && lines[k].Table is null) previous = lines[k];
                    bool gap = previous is not null && previous.Bottom - line.Top > (previous.Top - previous.Bottom) * 0.8;
                    bool ended = previous is not null && previous.Right < blockRight - (blockRight - blockLeft) * 0.15 && Terminal().IsMatch(paragraph.ToString().TrimEnd());
                    // An indented line under a list item continues the item; anything else after a gap starts a paragraph.
                    if (gap || ended || listMarker is not null && line.Left <= listBase + 2 && !Bullet().IsMatch(text)) FlushParagraph();
                }
                if (paragraph.Length == 0 || element is null) element = line.Tag;
                Append(paragraph, Words(line, null, Target));
            }

            // A paragraph runs on into the next block (often the next page) when its sentence is unfinished.
            var next = blocks.Skip(b + 1).Select(block => block.FirstOrDefault(l => !l.Removed)).FirstOrDefault(l => l is not null);
            if (next?.Table is not null || next?.Toc is not null || next?.Bookmark is not null) next = null;
            bool continues = paragraph.Length > 0 && next is not null && (tagged && element is { Kind: PdfTextTagKind.Paragraph or PdfTextTagKind.ListItem }
                ? ReferenceEquals(next.Tag, element)
                : !next.Mono && !Terminal().IsMatch(paragraph.ToString().TrimEnd()) && next.Text.Length > 0 && char.IsLower(next.Text[0]) && Math.Abs(next.Size - body) < 1);
            if (!continues || after.ContainsKey(b)) FlushParagraph();
            FlushCode();
            if (after.TryGetValue(b, out var below)) { Pictures(below); previousWasItem = false; }
        }
        FlushParagraph();
        FlushCode();
        foreach (var note in footnotes)
        {
            var text = new StringBuilder();
            for (int i = 0; i < note.Lines.Count; i++) Append(text, Words(note.Lines[i], i == 0 ? note.Mark : null, Target));
            output.Block("[^" + note.Number.ToString(CultureInfo.InvariantCulture) + "]: " + text);
        }
        if (output.IsEmpty) throw new DocumentFormatException(SR.Get("ToMarkdownPdfNoText"));
        output.OmittedContent += document.SkippedWords > 0 ? 1 : 0;
        return (output.Build(), written);
    }

    /// <summary>The tag most of the words carry (by characters), or null for untagged text.</summary>
    private static PdfTextTag? TagOf(IReadOnlyList<PdfTextTag>? tags, IReadOnlyList<PdfTextWord> words)
    {
        if (tags is null) return null;
        var best = words.Where(w => w.Tag is int t && t >= 0 && t < tags.Count).GroupBy(w => w.Tag!.Value)
            .OrderByDescending(g => g.Sum(w => w.Text.Length)).FirstOrDefault();
        return best is null || best.Sum(w => w.Text.Length) * 2 < words.Sum(w => w.Text.Length) ? null : tags[best.Key];
    }

    /// <summary>A PDF counts as tagged when at least 60% of its text (by characters) carries structure tags.</summary>
    private static bool Tagged(List<Line> all)
    {
        var words = all.Where(l => !l.Removed).SelectMany(l => l.Words).Where(w => !w.Artifact).ToList();
        int total = words.Sum(w => w.Text.Length);
        return total > 0 && words.Where(w => w.Tag is not null).Sum(w => w.Text.Length) >= total * 0.6;
    }

    /// <summary>
    /// Tagged headings: consecutive lines of one heading element form one heading at its level. Exporters sometimes tag
    /// body text as a heading, so a heading must also look like one: bold or larger than the body, at most 25 words, and
    /// its further lines in the same style. Lines that do not are left as text.
    /// </summary>
    private static void TaggedHeadings(List<Line> all, double body)
    {
        Line? first = null;
        foreach (var line in all.Where(l => !l.Removed && l.Table is null && l.Toc is null))
        {
            if (line.Tag is not { Kind: PdfTextTagKind.Heading } tag) { first = null; continue; }
            if (first is not null && ReferenceEquals(first.Tag, tag))
            {
                if (Math.Abs(line.Size - first.Size) < 0.5 && line.Bold == first.Bold && first.HeadingText!.Length + line.Text.Length < 250)
                {
                    first.HeadingText += " " + line.Text.Trim();
                    line.Removed = true;
                }
                continue;
            }
            first = null;
            if (!(line.Bold || line.Size >= body * 1.1) || line.Words.Count > 25) continue;
            first = line;
            line.Bookmark = tag.Level;
            line.HeadingText = line.Text.Trim();
        }
    }

    /// <summary>
    /// Tagged tables become Markdown tables from their cells, with or without drawn borders. Words go to the cell their
    /// own tag names (a line may run across several cells) and are joined in reading order; a cell spanning columns
    /// fills the first of them. Tables of one row or one column are layout boxes and stay text.
    /// </summary>
    private static void FindTaggedTables(List<Line> all, IReadOnlyList<PdfTextTag> tags)
    {
        PdfTextTag? CellOf(PdfTextWord word) => word.Tag is int t && t >= 0 && t < tags.Count && tags[t].Kind == PdfTextTagKind.TableCell ? tags[t] : null;
        var tables = all.Where(l => !l.Removed && l.Tag is { Kind: PdfTextTagKind.TableCell }).GroupBy(l => l.Tag!.Group);
        foreach (var table in tables)
        {
            var cells = new Dictionary<(int Row, int Column), List<string>>();
            int columns = 0;
            foreach (var line in table.OrderBy(l => l.Index))
                foreach (var word in line.Words)
                    if (CellOf(word) is { } cell && cell.Group == table.Key)
                    {
                        (cells.TryGetValue((cell.Row, cell.Column), out var list) ? list : cells[(cell.Row, cell.Column)] = []).Add(word.Text);
                        columns = Math.Max(columns, cell.Column + cell.ColumnSpan);
                    }
            int rows = cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Row) + 1;
            if (rows < 2 || columns < 2 || columns > 50) continue;
            var grid = Enumerable.Range(0, rows).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, columns)
                .Select(c => MarkdownText.Inline(MarkdownText.Clean(cells.TryGetValue((r, c), out var words) ? string.Join(' ', words) : ""))).ToArray()).ToArray();
            bool firstLine = true;
            foreach (var line in table.OrderBy(l => l.Index))
            {
                line.Table = firstLine ? MarkdownText.Table(grid) : string.Empty;
                firstLine = false;
            }
        }
    }

    /// <summary>The heading a place in the document lands on: the first on its page at or below the target top.</summary>
    private static string? AnchorAt(ILookup<int, Anchor>? anchors, int page, double? top)
    {
        if (anchors is null) return null;
        var onPage = anchors[page].ToList();
        var anchor = top is double y ? onPage.FirstOrDefault(a => a.Top <= y + 3) ?? onPage.LastOrDefault() : onPage.FirstOrDefault();
        return anchor is null ? null : "#" + anchor.Slug;
    }

    /// <summary>
    /// Headings from bookmarks: each bookmark is matched to the line (or up to three lines of one block) on its page
    /// whose letters and digits spell its title, nearest to its target. Bookmarks are used when at least two and half
    /// of them match; the result is the deepest bookmark level, the largest matched size and the first matched line, or null.
    /// </summary>
    private static (int Deepest, double Largest, int First)? MatchBookmarks(PdfTextDocument document, List<Line> all, CancellationToken ct)
    {
        var bookmarks = document.Outline ?? [];
        if (bookmarks.Count < 2) return null;
        var byPage = all.Where(l => !l.Removed && l.Table is null && l.Toc is null).GroupBy(l => l.Page.Number).ToDictionary(g => g.Key, g => g.ToList());
        var matches = new List<(Line Line, int Level, string Text, List<Line> Merged)>();
        var used = new HashSet<Line>();
        foreach (var bookmark in bookmarks.Take(2000))
        {
            ct.ThrowIfCancellationRequested();
            string key = Key(bookmark.Title), bare = Key(Numbering().Replace(bookmark.Title, ""));
            if (key.Length == 0 || !byPage.TryGetValue(bookmark.Page, out var lines)) continue;
            (Line Line, string Text, List<Line> Merged)? best = null;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < lines.Count; i++)
            {
                if (used.Contains(lines[i]) || lines[i].Words.Count > 30) continue;
                string text = lines[i].Text.Trim(), joined = Key(text);
                var merged = new List<Line>();
                for (int k = i + 1; joined.Length < key.Length && k < lines.Count && k <= i + 3 && SameBlock(lines[k - 1], lines[k]); k++)
                {
                    merged.Add(lines[k]);
                    text += " " + lines[k].Text.Trim();
                    joined += Key(lines[k].Text);
                }
                string joinedBare = Key(Numbering().Replace(text, ""));
                if (joined != key && joinedBare != bare && joinedBare != key) continue;
                double distance = bookmark.Top is double top ? Math.Abs(lines[i].Top - top) : i;
                if (distance < bestDistance) { best = (lines[i], text, merged); bestDistance = distance; }
            }
            if (best is not { } found) continue;
            used.Add(found.Line);
            matches.Add((found.Line, bookmark.Level, found.Text, found.Merged));
        }
        if (matches.Count < 2 || matches.Count * 2 < bookmarks.Count) return null;
        foreach (var (line, level, text, merged) in matches)
        {
            line.Bookmark = level;
            line.HeadingText = text;
            foreach (var part in merged) part.Removed = true;
        }
        return (matches.Max(m => m.Level), matches.Max(m => m.Line.Size), matches.Min(m => m.Line.Index));

        static bool SameBlock(Line a, Line b) => a.Page == b.Page && a.Bottom - b.Top < (a.Top - a.Bottom) * 1.5 && b.Top <= a.Top;
    }

    /// <summary>
    /// Contents entries name headings too: after the contents, the next standalone line that spells an entry's title
    /// becomes a heading at the entry's depth, aligned with the bookmark levels where entries already matched them.
    /// Lines are searched in order, so a title that repeats (References in every chapter) finds its own heading.
    /// </summary>
    private static void MatchContents(List<Line> all, CancellationToken ct)
    {
        var entries = all.Where(l => l.TocList is not null).SelectMany(l => l.TocList!).ToList();
        if (entries.Count == 0) return;
        int start = entries.Max(e => e.Line.Index) + 1;
        var candidates = all.Where(l => l.Index >= start && !l.Removed && l.Table is null && l.Toc is null && l.Words.Count <= 15)
            .Select(l => (Line: l, Text: l.HeadingText ?? l.Text)).Select(c => (c.Line, Key: Key(c.Text), Bare: Key(Numbering().Replace(c.Text, "")))).ToList();
        var found = new List<(TocEntry Entry, Line Line)>();
        int position = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string key = Key(entry.Title), bare = Key(Numbering().Replace(entry.Title, ""));
            if (key.Length == 0) continue;
            for (int k = position; k < candidates.Count; k++)
            {
                if (candidates[k].Key != key && (bare.Length == 0 || candidates[k].Bare != bare)) continue;
                found.Add((entry, candidates[k].Line));
                position = k + 1;
                break;
            }
        }
        // Entries whose heading is bookmarked tell how contents depth maps to heading levels.
        int offset = found.Where(f => f.Line.Bookmark is not null).Select(f => f.Line.Bookmark!.Value - f.Entry.Depth - 1)
            .GroupBy(o => o).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        foreach (var (entry, line) in found)
            line.Bookmark ??= Math.Clamp(entry.Depth + 1 + offset, 1, 6);
    }

    /// <summary>
    /// Table of contents entries. Pages are read as visual rows across the whole width, because contents pages often
    /// set numbers, titles and page numbers in separate columns. A page with at least three dot-leader rows, or with a
    /// contents title and at least three rows ending in a page number (and the pages right after such a page), is a
    /// contents page; each row ending in a page number there is an entry, and a title that wraps joins the row below.
    /// The entries of consecutive contents pages form one nested list, written where the first entry is.
    /// </summary>
    private static void FindContents(List<List<Line>> blocks, CancellationToken ct)
    {
        var pages = blocks.SelectMany(b => b).Where(l => !l.Removed && l.Table is null).GroupBy(l => l.Page.Number).OrderBy(g => g.Key).ToList();
        var lists = new List<List<(Line Line, string Title, List<Line> Parts)>>();
        int previousContents = int.MinValue;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var rows = Rows(page.ToList());
            int leaders = rows.Count(r => r.Leader.Success);
            int numbered = rows.Count(r => r.Entry.Success);
            bool titled = rows.Any(r => r.Title);
            bool continued = previousContents == page.Key - 1;
            if (!(leaders >= 3 || titled && numbered >= 3 || continued && numbered >= 3 && numbered * 2 >= rows.Count || AlignedPageNumbers(rows))) continue;
            if (!continued) lists.Add([]);
            previousContents = page.Key;
            // Rows of a title that wraps, waiting for the row with its page number (at most three).
            var pending = new List<Row>();
            foreach (var row in rows)
            {
                var match = row.Entry;
                if (match.Success)
                {
                    string title = match.Groups["title"].Value.Trim();
                    var parts = new List<Line>(row.Lines);
                    if (pending.Count > 0 && row.Lines.Min(l => l.Left) >= pending[0].Lines.Min(l => l.Left) - 2)
                    {
                        title = string.Join(' ', pending.Select(p => p.Text)) + " " + title;
                        parts.InsertRange(0, pending.SelectMany(p => p.Lines));
                    }
                    lists[^1].Add((parts[0], title, parts));
                    pending.Clear();
                }
                else if (!row.Title && row.Text.Length <= 120 && pending.Count < 3) pending.Add(row);
                else pending.Clear();
            }
        }
        foreach (var list in lists.Where(l => l.Count >= 2))
        {
            var nested = Nest(list.Select(e => (e.Line, e.Title)).ToList());
            for (int i = 0; i < list.Count; i++)
                foreach (var part in list[i].Parts) part.Toc = nested[i];
            // The list is written at the first of its lines in reading order.
            var first = list.SelectMany(e => e.Parts).MinBy(l => l.Index)!;
            first.TocList = nested;
        }
    }

    /// <summary>
    /// At least four rows ending in arabic page numbers that are right-aligned in one column and never decrease: a
    /// contents page without dot leaders or a contents title.
    /// </summary>
    private static bool AlignedPageNumbers(List<Row> rows)
    {
        var numbers = rows.Select(r => (Match: r.Trailing, Right: r.Lines.Max(l => l.Right)))
            .Where(x => x.Match.Success && int.TryParse(x.Match.Groups["page"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(x => (Page: int.Parse(x.Match.Groups["page"].Value, CultureInfo.InvariantCulture), x.Right)).ToList();
        if (numbers.Count < 4) return false;
        double right = numbers.Select(n => n.Right).Order().ElementAt(numbers.Count / 2);
        var column = numbers.Where(n => Math.Abs(n.Right - right) <= 3).ToList();
        return column.Count >= 4 && column.Count * 4 >= numbers.Count * 3 && column.Zip(column.Skip(1)).All(p => p.Second.Page >= p.First.Page);
    }

    /// <summary>Lines of one page grouped into visual rows (vertically overlapping), top to bottom, each read left to right.</summary>
    private static List<Row> Rows(List<Line> lines)
    {
        var rows = new List<List<Line>>();
        foreach (var line in lines.OrderByDescending(l => l.Top))
        {
            var row = rows.LastOrDefault(r => Math.Min(r[0].Top, line.Top) - Math.Max(r[0].Bottom, line.Bottom) > 0.5 * Math.Min(r[0].Top - r[0].Bottom, line.Top - line.Bottom));
            if (row is null) rows.Add([line]); else row.Add(line);
        }
        return rows.Select(r =>
        {
            r.Sort((a, b) => a.Left.CompareTo(b.Left));
            return new Row(string.Join(' ', r.Select(l => l.Text.Trim())), r);
        }).ToList();
    }

    /// <summary>A visual row of a page, matched once against the contents patterns.</summary>
    private sealed class Row
    {
        public Row(string text, List<Line> lines)
        {
            Text = text;
            Lines = lines;
            // A row ending in neither a digit nor a roman numeral letter cannot end in a page number.
            bool numbered = text.Length > 0 && (char.IsAsciiDigit(text[^1]) || "ivxlcdmIVXLCDM".Contains(text[^1]));
            Leader = numbered ? PdfLayoutConverter.Leader().Match(text) : Match.Empty;
            Trailing = numbered ? TrailingPage().Match(text) : Match.Empty;
            Title = ContentsTitle().IsMatch(text);
        }
        public string Text { get; }
        public List<Line> Lines { get; }
        public Match Leader { get; }
        public Match Trailing { get; }
        public Match Entry => Leader.Success ? Leader : Trailing;
        public bool Title { get; }
    }

    /// <summary>Depth from indentation, or from the numbering (1.2.3) when all entries start at the same position.</summary>
    private static List<TocEntry> Nest(List<(Line Line, string Title)> group)
    {
        var indents = new List<double>();
        foreach (double left in group.Select(e => e.Line.Left).Order())
            if (indents.Count == 0 || left > indents[^1] + 4) indents.Add(left);
        return group.Select(e =>
        {
            int depth = indents.Count > 1
                ? indents.FindLastIndex(x => e.Line.Left >= x - 4)
                : SectionNumber().Match(e.Title) is { Success: true } number ? number.Groups["n"].Value.Count(c => c == '.') : 0;
            return new TocEntry(e.Line, e.Title, Math.Clamp(depth, 0, 3));
        }).ToList();
    }

    private static string Contents(List<TocEntry> entries, IReadOnlyList<Anchor>? anchors, ILookup<int, Anchor>? anchorsByPage)
    {
        var builder = new StringBuilder();
        int last = -1;
        foreach (var entry in entries)
        {
            string title = MarkdownText.Inline(MarkdownText.Clean(entry.Title));
            string? target = null;
            if (anchors is not null)
            {
                // The title names its heading; a link area is only the fallback, as it often reaches into the row above.
                string key = Key(entry.Title), bare = Key(Numbering().Replace(entry.Title, ""));
                // Titles that repeat (References in every chapter) take the first heading after the previous entry's.
                int found = FindAnchor(a => a.Key == key);
                if (found < 0 && bare.Length > 0) found = FindAnchor(a => a.Bare == bare);
                if (found >= 0) last = found;
                target = found < 0 ? null : "#" + anchors[found].Slug;
                target ??= entry.Line.Page.Links?.Where(l => l.TargetPage is not null).Select(l => (Link: l, Overlap: Overlap(l, entry.Line)))
                    .Where(x => x.Overlap > 0.5).OrderByDescending(x => x.Overlap).Select(x => AnchorAt(anchorsByPage, x.Link.TargetPage!.Value, x.Link.TargetTop)).FirstOrDefault();
            }
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(new string(' ', entry.Depth * 2)).Append("- ").Append(target is null ? title : "[" + title + "](" + target + ")");
        }
        return builder.ToString();

        int FindAnchor(Func<Anchor, bool> match)
        {
            for (int k = last + 1; k < anchors!.Count; k++) if (match(anchors[k])) return k;
            for (int k = 0; k <= last && k < anchors.Count; k++) if (match(anchors[k])) return k;
            return -1;
        }
    }

    /// <summary>The share of the line's height a link area covers, when they also overlap horizontally.</summary>
    private static double Overlap(PdfTextLink link, Line line) =>
        Math.Min(link.Right, line.Right) - Math.Max(link.Left, line.Left) <= 0 ? 0
            : Math.Max(0, Math.Min(link.Top, line.Top) - Math.Max(link.Bottom, line.Bottom)) / Math.Max(1, line.Top - line.Bottom);

    /// <summary>The anchor Markdig's GitHub-style identifiers give a heading, numbered when it repeats.</summary>
    private static string Slug(string heading, Dictionary<string, int> used)
    {
        var builder = new StringBuilder();
        foreach (char c in heading)
            if (char.IsLetterOrDigit(c) || c is '-' or '_') builder.Append(char.ToLowerInvariant(c));
            else if (c == ' ') builder.Append('-');
        string slug = builder.Length == 0 ? "section" : builder.ToString();
        if (!used.TryGetValue(slug, out int seen)) { used[slug] = 0; return slug; }
        string candidate;
        do candidate = slug + "-" + ++seen; while (used.ContainsKey(candidate));
        used[slug] = seen;
        used[candidate] = 0;
        return candidate;
    }

    /// <summary>Letters and digits only, lowercase: how titles in bookmarks, contents and headings are compared.</summary>
    private static string Key(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    /// <summary>
    /// Where pictures go: before the first block of their page that starts below the picture's top, else after the
    /// page's last block (or the previous page's, for pages without text).
    /// </summary>
    private static (Dictionary<int, List<PdfPageImage>> Before, Dictionary<int, List<PdfPageImage>> After, List<PdfPageImage> Leading) PlaceImages(
        PdfTextDocument document, List<List<Line>> blocks)
    {
        var before = new Dictionary<int, List<PdfPageImage>>();
        var after = new Dictionary<int, List<PdfPageImage>>();
        var leading = new List<PdfPageImage>();
        var repeated = Repeated(document);
        int lastBlock = -1;
        foreach (var page in document.Pages)
        {
            var indices = Enumerable.Range(0, blocks.Count).Where(i => ReferenceEquals(blocks[i][0].Page, page)).ToArray();
            foreach (var image in (page.Images ?? []).Where(i => !repeated.Contains(Hash(i.Bytes))).OrderByDescending(i => i.Top))
            {
                int target = indices.Where(i => blocks[i].Where(l => !l.Removed).Select(l => l.Top).DefaultIfEmpty(double.MaxValue).Max() < image.Top - 1).DefaultIfEmpty(-1).First();
                var list = target >= 0 ? Get(before, target) : indices.Length > 0 ? Get(after, indices[^1]) : lastBlock >= 0 ? Get(after, lastBlock) : leading;
                list.Add(image);
            }
            if (indices.Length > 0) lastBlock = indices[^1];
        }
        return (before, after, leading);

        static List<PdfPageImage> Get(Dictionary<int, List<PdfPageImage>> map, int key) => map.TryGetValue(key, out var list) ? list : map[key] = [];
    }

    /// <summary>Pictures shown on at least half of the pages of a document with three or more pages.</summary>
    private static HashSet<string> Repeated(PdfTextDocument document)
    {
        int pages = document.Pages.Count;
        if (pages < 3) return [];
        return document.Pages.SelectMany(p => (p.Images ?? []).Select(i => (Hash(i.Bytes), p.Number))).Distinct()
            .GroupBy(x => x.Item1).Where(g => g.Count() >= Math.Max(2, pages / 2)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static string Hash(byte[] bytes) => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Joins a line to the paragraph, undoing hyphenation at the line end.</summary>
    private static void Append(StringBuilder paragraph, string text)
    {
        if (paragraph.Length == 0) { paragraph.Append(text); return; }
        if (paragraph.Length > 1 && paragraph[^1] == '-' && char.IsLetter(paragraph[^2]) && text.Length > 0 && char.IsLower(text[0]))
        {
            paragraph.Length--;
            paragraph.Append(text);
            return;
        }
        paragraph.Append(' ').Append(text);
    }

    /// <summary>
    /// The line's words as Markdown: bold and italic runs inside a line kept, and words inside a link area linked to
    /// its target (consecutive words of one area form one link).
    /// </summary>
    private static string Words(Line line, string? marker, Func<PdfTextLink, string?> target)
    {
        var words = new List<PdfTextWord>(line.Words);
        // Drop the bullet or number the list marker replaces.
        if (marker is { Length: > 0 } && words.Count > 0)
        {
            if (words[0].Text == marker) words.RemoveAt(0);
            else if (words[0].Text.StartsWith(marker, StringComparison.Ordinal)) words[0] = words[0] with { Text = words[0].Text[marker.Length..] };
        }
        if (words.Count == 0) return string.Empty;
        bool allBold = words.All(w => w.Bold), allItalic = words.All(w => w.Italic);
        var links = line.Page.Links ?? [];
        var addresses = words.Select(word =>
        {
            double x = (word.Left + word.Right) / 2, y = (word.Bottom + word.Top) / 2;
            if (line.Marks?.ContainsKey(word) == true) return null;
            var link = links.FirstOrDefault(l => x >= l.Left - 1 && x <= l.Right + 1 && y >= l.Bottom - 1 && y <= l.Top + 1);
            return link is null ? null : target(link);
        }).ToArray();
        // A footnote mark becomes a footnote reference, any other short superscript an HTML superscript.
        string Display(int i, bool link)
        {
            var word = words[i];
            string Text(string text) => link ? MarkdownText.Inline(text) : Escape(text);
            if (line.Marks is not null && line.Marks.TryGetValue(word, out int number))
                return (word.RaisedFrom > 0 ? Text(word.Text[..word.RaisedFrom]) : "") + "[^" + number.ToString(CultureInfo.InvariantCulture) + "]";
            if (word.RaisedFrom <= 0 || !Superscript().IsMatch(word.Text[word.RaisedFrom..])
                || word.RaisedFrom == 1 && char.IsLetter(word.Text[0]) && ReferenceEquals(word, line.Words[0]))
                return Text(word.Text);
            return Text(word.Text[..word.RaisedFrom]) + "<sup>" + word.Text[word.RaisedFrom..] + "</sup>";
        }
        var builder = new StringBuilder();
        int index = 0;
        while (index < words.Count)
        {
            string? address = addresses[index];
            var segment = new StringBuilder();
            while (index < words.Count && addresses[index] == address)
            {
                var first = words[index];
                bool bold = first.Bold && !allBold, italic = first.Italic && !allItalic;
                var run = new List<string>();
                while (index < words.Count && (words[index].Bold && !allBold) == bold && (words[index].Italic && !allItalic) == italic && addresses[index] == address)
                {
                    // A mark set apart from its word still follows it directly.
                    bool glued = run.Count > 0 && words[index].RaisedFrom < 0 && line.Marks?.ContainsKey(words[index]) == true;
                    string text = Display(index++, address is not null);
                    if (glued) run[^1] += text; else run.Add(text);
                }
                string emphasis = (bold ? "**" : "") + (italic ? "*" : "");
                if (segment.Length > 0) segment.Append(' ');
                segment.Append(emphasis.Length > 0 ? emphasis + string.Join(' ', run) + emphasis : string.Join(' ', run));
            }
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(address is null ? segment.ToString() : "[" + segment + "](" + Destination(address) + ")");
        }
        return builder.ToString();
    }

    /// <summary>A link destination in angle brackets when it contains spaces or parentheses.</summary>
    private static string Destination(string address) =>
        address.IndexOfAny([' ', '(', ')', '<', '>']) < 0 ? address : "<" + address.Replace("<", "%3C").Replace(">", "%3E") + ">";

    /// <summary>Addresses stay unescaped so they remain clickable.</summary>
    private static string Escape(string word) => Address().IsMatch(word) ? word : MarkdownText.Inline(word);

    /// <summary>
    /// Footnotes: a mark in the text (a number or symbol set smaller and raised, after a word or on its own) and, on the
    /// same page, a line in the lower half set smaller than the body text that starts with the same mark. The note's
    /// lines (that line and the lines of the same size after it in its block) leave the text; the mark becomes a
    /// reference. Notes are numbered through the document, as many books start again in every chapter.
    /// </summary>
    private static List<Footnote> FindFootnotes(List<List<Line>> blocks, double body, CancellationToken ct)
    {
        var notes = new List<Footnote>();
        var placed = new Dictionary<Line, (List<Line> Block, int At)>();
        foreach (var block in blocks)
            for (int i = 0; i < block.Count; i++) placed[block[i]] = (block, i);
        foreach (var page in blocks.SelectMany(b => b).Where(l => !l.Removed).GroupBy(l => l.Page.Number))
        {
            ct.ThrowIfCancellationRequested();
            var lines = page.ToList();
            var marks = new List<(Line Line, PdfTextWord Word, string Mark)>();
            foreach (var line in lines)
                for (int i = 0; i < line.Words.Count; i++)
                    if (MarkOf(line, i) is string mark) marks.Add((line, line.Words[i], mark));
            if (marks.Count == 0) continue;
            var starts = lines.Where(l => l.Size <= body * .92 && l.Top < l.Page.Height * .5)
                .Select(l => (Line: l, Mark: LeadingMark().Match(l.Words[0].Text)))
                .Where(s => s.Mark.Success && s.Line.Text[s.Mark.Length..].Any(char.IsLetter)).ToList();
            var markTexts = marks.Select(m => m.Mark).ToHashSet(StringComparer.Ordinal);
            foreach (var (line, word, mark) in marks)
            {
                int at = starts.FindIndex(s => s.Mark.Groups[1].Value == mark && s.Line != line && !s.Line.Removed && !line.Removed);
                if (at < 0) continue;
                var start = starts[at].Line;
                starts.RemoveAt(at);
                var noteLines = new List<Line> { start };
                var (block, index) = placed[start];
                for (int k = index + 1; k < block.Count; k++)
                {
                    var next = block[k];
                    var other = LeadingMark().Match(next.Words[0].Text);
                    if (next.Removed || Math.Abs(next.Size - start.Size) > .6 || other.Success && (markTexts.Contains(other.Groups[1].Value)
                        || other.Length == next.Words[0].Text.Length || int.TryParse(mark, out int n) && other.Groups[1].Value == (n + 1).ToString(CultureInfo.InvariantCulture)))
                        break;
                    noteLines.Add(next);
                }
                foreach (var noteLine in noteLines) noteLine.Removed = true;
                line.Marks ??= new Dictionary<PdfTextWord, int>(ReferenceEqualityComparer.Instance);
                line.Marks[word] = notes.Count + 1;
                notes.Add(new Footnote(notes.Count + 1, noteLines, mark));
            }
        }
        return notes;
    }

    /// <summary>
    /// The footnote mark a word of the line may be: a raised number or symbol after a word, or on its own, smaller and
    /// above the line's baseline. Only a mark a note on the page starts with becomes one (else "10¹⁸" is an exponent).
    /// </summary>
    private static string? MarkOf(Line line, int index)
    {
        var word = line.Words[index];
        if (word.RaisedFrom > 0)
        {
            string raised = word.Text[word.RaisedFrom..];
            return NoteMark().IsMatch(raised) ? raised : null;
        }
        if (index == 0 || !NoteMark().IsMatch(word.Text) || word.FontSize > line.Size * .85) return null;
        var baseline = line.Words.Where(w => w.FontSize >= line.Size * .9).Select(w => w.Bottom).Order().ToList();
        return baseline.Count > 0 && word.Bottom > baseline[baseline.Count / 2] + line.Size * .2 ? word.Text : null;
    }

    private static double Size(IReadOnlyList<PdfTextWord> words)
    {
        var sizes = words.SelectMany(w => Enumerable.Repeat(w.FontSize, Math.Max(1, w.Text.Length))).OrderBy(s => s).ToArray();
        return sizes.Length == 0 ? 0 : sizes[sizes.Length / 2];
    }

    /// <summary>The most common size, weighted by characters.</summary>
    private static double BodySize(IEnumerable<Line> lines) => lines.SelectMany(l => l.Words)
        .GroupBy(w => Math.Round(w.FontSize * 2) / 2).OrderByDescending(g => g.Sum(w => w.Text.Length)).First().Key;

    /// <summary>Text in the top or bottom tenth of the page that repeats on most pages, and bare page numbers there.</summary>
    private static void RemoveRunningText(List<Line> lines, int pages)
    {
        bool Margin(Line l) => l.Top > l.Page.Height * 0.9 || l.Bottom < l.Page.Height * 0.1;
        var marginal = lines.Where(Margin).ToList();
        foreach (var line in marginal.Where(l => PageNumber().IsMatch(l.Text.Trim()))) line.Removed = true;
        if (pages < 3) return;
        foreach (var group in marginal.GroupBy(l => Digits().Replace(l.Text.Trim().ToLowerInvariant(), "#")))
            if (group.Select(l => l.Page.Number).Distinct().Count() >= Math.Max(2, pages / 2))
                foreach (var line in group) line.Removed = true;
    }

    /// <summary>Ruled tables only: a grid of drawn lines is a reliable sign, whitespace alignment is not.</summary>
    private static void FindTables(PdfTextDocument document, List<Line> lines, CancellationToken ct)
    {
        foreach (var page in document.Pages.Where(p => p.Rules.Count >= 4))
        {
            var pageLines = lines.Where(l => l.Page == page && !l.Removed).OrderByDescending(l => l.Top).ToList();
            var positioned = pageLines.SelectMany(l => l.Words).Where(w => w.Right > w.Left && w.Top > w.Bottom)
                .Select(w => new PositionedWord(w.Text, w.Left, w.Bottom, w.Right, w.Top)).ToArray();
            IReadOnlyList<DetectedTable> tables;
            try { tables = LayoutTableDetector.Detect(new PositionedPage(page.Number, positioned, page.Rules), inferUnruled: false, ct); }
            catch (TableLayoutException) { continue; }
            foreach (var table in tables.Where(t => t.Rows.Count >= 2))
            {
                Claim(pageLines.Where(l => l.Words.All(w => Inside(w, table.Left - 2, table.Bottom - 2, table.Right + 2, table.Top + 2))), table.Rows);
            }
        }
    }

    private static bool Inside(PdfTextWord word, double left, double bottom, double right, double top)
    {
        double x = (word.Left + word.Right) / 2, y = (word.Bottom + word.Top) / 2;
        return x >= left && x <= right && y >= bottom && y <= top;
    }

    /// <summary>The table is written where the first of its lines is in reading order; its other lines leave the text.</summary>
    private static void Claim(IEnumerable<Line> lines, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var claimed = lines.Where(l => l.Table is null && !l.Removed).ToList();
        if (claimed.Count == 0) return;
        foreach (var line in claimed) line.Table = string.Empty;
        claimed.MinBy(l => l.Index)!.Table = MarkdownText.Table(rows.Select(r => (IReadOnlyList<string>)r.Select(c => MarkdownText.Inline(MarkdownText.Clean(c))).ToArray()).ToArray());
    }

    /// <summary>
    /// Tables set without a full grid (no rules, or only rules above and below): at least three rows close together whose
    /// short lines fall into the same two or more columns, separated by gaps no row's text crosses. Only short lines take
    /// part (lines of paragraphs, headings, contents entries and code do not), a column mostly of bullets or numbers is a
    /// list, and cells mostly longer than a few words are text set in columns. Text between vertical rules is a diagram
    /// or a grid the ruled detection refused. Beyond that a table needs a sign: a rule above or below it, a caption
    /// ("Table 2", "Tabelle 2") next to it, or at least three columns and four rows. A row whose first column is empty
    /// continues the row above (a cell that wraps). Tagged PDFs say where their tables are, so they are left alone.
    /// </summary>
    private static void FindUnruledTables(List<List<Line>> blocks, List<Line> all, CancellationToken ct)
    {
        // Paragraph lines: blocks of three or more lines, most of them as wide as the block, a quarter of the page or wider.
        var prose = new HashSet<Line>();
        foreach (var block in blocks.Where(b => b.Count >= 3))
        {
            double widest = block.Max(l => l.Right - l.Left);
            if (widest >= block[0].Page.Width * .25 && block.Count(l => l.Right - l.Left >= widest * .9) >= block.Count * .6) prose.UnionWith(block);
        }
        foreach (var page in all.Where(l => !prose.Contains(l) && !l.Removed && l.Table is null && l.Toc is null && l.TocList is null && l.Bookmark is null && !l.Mono && l.Words.Count <= 8
                     && !Caption().IsMatch(l.Text)).GroupBy(l => l.Page))
        {
            ct.ThrowIfCancellationRequested();
            var captions = all.Where(l => l.Page == page.Key && !l.Removed && Caption().IsMatch(l.Text)).ToList();
            // Captions side by side head tables side by side: each takes the part of the page from its caption's left edge.
            var splits = captions.Where(c => captions.Any(o => o != c && o.Right < c.Left && Math.Abs(o.Top - c.Top) < c.Top - c.Bottom))
                .Select(c => c.Left - 4).Distinct().Order().ToList();
            foreach (var region in page.GroupBy(l => splits.Count(x => (l.Left + l.Right) / 2 >= x)))
                FindUnruledTables(page.Key, region.ToList(), captions);
        }
    }

    /// <summary>The unruled tables among the candidate lines of one page (or of one side of it).</summary>
    private static void FindUnruledTables(PdfTextPage page, List<Line> lines, List<Line> captions)
    {
        var owner = new Dictionary<PdfTextWord, Line>(ReferenceEqualityComparer.Instance);
        foreach (var line in lines) foreach (var word in line.Words) owner[word] = line;
        // Visual rows, top to bottom; a row splits into cells at line ends and at wide gaps.
        var rows = new List<List<PdfTextWord>>();
        foreach (var word in owner.Keys.OrderByDescending(w => w.Top))
        {
            var row = rows.LastOrDefault(r => Math.Min(r[0].Top, word.Top) - Math.Max(r[0].Bottom, word.Bottom) > 0.5 * Math.Min(r[0].Top - r[0].Bottom, word.Top - word.Bottom));
            if (row is null) rows.Add([word]); else row.Add(word);
        }
        var cells = rows.Select(r =>
        {
            r.Sort((a, b) => a.Left.CompareTo(b.Left));
            var split = new List<List<PdfTextWord>>();
            foreach (var word in r)
                if (split.Count == 0 || owner[split[^1][^1]] != owner[word] || word.Left - split[^1][^1].Right > Math.Max(6, word.FontSize * .9)) split.Add([word]);
                else split[^1].Add(word);
            return split;
        }).ToList();
        int start = 0;
        while (start < rows.Count)
        {
            if (cells[start].Count < 2) { start++; continue; }
            // A run of rows close together, single-cell rows allowed between rows of several cells.
            int end = start + 1, last = start;
            while (end < rows.Count)
            {
                double height = rows[end - 1].Max(w => w.Top) - rows[end - 1].Min(w => w.Bottom);
                if (rows[end - 1].Min(w => w.Bottom) - rows[end].Max(w => w.Top) > height * 1.6) break;
                if (cells[end].Count >= 2) last = end;
                end++;
            }
            var table = Unruled(rows.GetRange(start, last - start + 1), cells.GetRange(start, last - start + 1), page, captions);
            if (table is not null)
            {
                var words = rows.GetRange(start, last - start + 1).SelectMany(r => r).ToHashSet(ReferenceEqualityComparer.Instance);
                Claim(lines.Where(l => l.Words.All(words.Contains)), table);
            }
            start = last + 1;
        }
    }

    /// <summary>The rows of a run as a table, or null when they do not form one.</summary>
    private static List<IReadOnlyList<string>>? Unruled(List<List<PdfTextWord>> rows, List<List<List<PdfTextWord>>> cells, PdfTextPage page, List<Line> captions)
    {
        double pageWidth = page.Width;
        // Columns come from the rows with the usual number of cells (headers may span columns): the gaps no word of them crosses.
        int usual = cells.Where(c => c.Count >= 2).GroupBy(c => c.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var data = cells.Where(c => c.Count == usual).ToList();
        if (data.Count < 3) return null;
        double gap = Math.Max(6, data.SelectMany(r => r.SelectMany(c => c)).Average(w => w.FontSize) * .8);
        var columns = new List<(double Left, double Right)>();
        foreach (var (left, right) in data.SelectMany(r => r.Select(c => (Left: c.Min(w => w.Left), Right: c.Max(w => w.Right)))).OrderBy(c => c.Left))
            if (columns.Count == 0 || left - columns[^1].Right > gap) columns.Add((left, right));
            else columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, right));
        if (columns.Count < 2 || columns.Count > 20 || columns.Any(c => c.Right - c.Left > pageWidth * .4)) return null;
        var table = new List<string[]>();
        foreach (var row in rows)
        {
            var texts = new List<PdfTextWord>[columns.Count];
            foreach (var word in row)
            {
                double x = (word.Left + word.Right) / 2;
                int column = columns.FindLastIndex(c => x >= c.Left - gap / 2);
                (texts[Math.Max(0, column)] ??= []).Add(word);
            }
            var cellTexts = texts.Select(t => t is null ? string.Empty : string.Join(' ', t.Select(w => w.Text))).ToArray();
            // A row with nothing in the first column continues the row above.
            if (table.Count > 0 && cellTexts[0].Length == 0)
                for (int c = 0; c < cellTexts.Length; c++)
                    table[^1][c] = (table[^1][c] + " " + cellTexts[c]).Trim();
            else table.Add(cellTexts);
        }
        if (table.Count < 3) return null;
        var filled = table.SelectMany(r => r).Where(c => c.Length > 0).ToList();
        if (filled.Count(c => c.Split(' ').Length <= 6) < filled.Count * .75) return null;
        if (table.SelectMany(r => r).Count(c => c.Length == 0) > table.Count * columns.Count * .4) return null;
        // A first column mostly of bullets or item numbers is a list (bullets inside later cells are fine).
        var labels = table.Select(r => r[0]).Where(t => t.Length > 0).ToList();
        if (labels.Count(t => Bullet().IsMatch(t + " ") || Number().IsMatch(t + " ") || t.Length <= 2 && !t.Any(char.IsLetterOrDigit)) * 2 >= labels.Count) return null;
        double x0 = columns[0].Left, x1 = columns[^1].Right, top = rows[0].Max(w => w.Top), bottom = rows[^1].Min(w => w.Bottom);
        double line = rows[0].Max(w => w.Top) - rows[0].Min(w => w.Bottom);
        bool captioned = captions.Any(c => c.Bottom >= top - line && c.Bottom <= top + line * 4 || c.Top <= bottom + line && c.Top >= bottom - line * 4);
        // Vertical rules between the columns: text in boxes (a diagram), unless a captioned table's rules run between its
        // words (a grid with joined cells the ruled detection refused).
        var verticals = page.Rules.Where(r => Math.Abs(r.X1 - r.X2) < 1.5 && Math.Abs(r.Y2 - r.Y1) >= line && r.X1 > x0 + 2 && r.X1 < x1 - 2
            && Math.Min(r.Y1, r.Y2) < top && Math.Max(r.Y1, r.Y2) > bottom).ToList();
        if (verticals.Count > 0 && !(captioned && verticals.All(v => rows.SelectMany(r => r).All(w => w.Right <= v.X1 + 1 || w.Left >= v.X1 - 1
                || (w.Bottom + w.Top) / 2 > Math.Max(v.Y1, v.Y2) || (w.Bottom + w.Top) / 2 < Math.Min(v.Y1, v.Y2)))))
            return null;
        bool ruled = page.Rules.Any(r => Math.Abs(r.Y1 - r.Y2) < 1.5 && Math.Min(r.X1, r.X2) <= x0 + (x1 - x0) * .2 && Math.Max(r.X1, r.X2) >= x1 - (x1 - x0) * .2
            && (r.Y1 >= top - 2 && r.Y1 <= top + line * 2.5 || r.Y1 <= bottom + 2 && r.Y1 >= bottom - line * 2.5));
        if (ruled || captioned) return table.Select(r => (IReadOnlyList<string>)r).ToList();
        // Without either, three columns and four rows, and no column reading on as text (cells starting in lower case).
        if (columns.Count < 3 || table.Count < 4) return null;
        for (int c = 0; c < columns.Count; c++)
        {
            var texts = table.Select(r => r[c]).Where(t => t.Length > 0).ToList();
            if (texts.Count(t => char.IsLower(t[0])) * 2 >= texts.Count) return null;
        }
        return table.Select(r => (IReadOnlyList<string>)r).ToList();
    }

    [GeneratedRegex(@"^[•◦▪▫‣⁃●○■□–—\-*·](?:\s+|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(?<n>\d{1,3})[.)](?:\s+|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Number();

    [GeneratedRegex(@"[.!?:…""”’)\]]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Terminal();

    [GeneratedRegex(@"^\W*(?:(?:page|seite|p\.|s\.)\s*)?\d{1,4}(?:\s*(?:of|von|/)\s*\d{1,4})?\W*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Digits();

    [GeneratedRegex(@"^(?:https?://|www\.)\S+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();

    /// <summary>A contents entry with a dot leader before its page number (arabic or roman).</summary>
    [GeneratedRegex(@"^(?<title>.*[^\s.·…‧∙_])\s*(?:[.·…‧∙_]\s*){3,}(?<page>\d{1,4}|[ivxlcdm]{1,7})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Leader();

    /// <summary>A contents entry with only space before its page number.</summary>
    [GeneratedRegex(@"^(?<title>[^\p{L}]*\p{L}.*[^\s\d.])\s+(?<page>\d{1,4}|[ivxlcdm]{1,7})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingPage();

    [GeneratedRegex(@"^(?:table\s+of\s+contents|contents|inhalt|inhaltsverzeichnis|sommaire|table\s+des\s+matières|índice|indice|inhoud|spis\s+treści)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContentsTitle();

    /// <summary>Leading section numbering such as "2.", "2.3.1" or "IV." with the space after it.</summary>
    [GeneratedRegex(@"^\s*(?:\d+(?:\.\d+)*\.?|[IVXLC]+\.)\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Numbering();

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)*)\.?\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SectionNumber();

    /// <summary>A footnote mark: a number or up to three of the usual footnote symbols.</summary>
    [GeneratedRegex(@"^(?:\d{1,3}|[*†‡§¶]{1,3})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoteMark();

    /// <summary>The mark a footnote's text starts with, alone or joined to its first word ("1Quick", "5", "†Also").</summary>
    [GeneratedRegex(@"^(\d{1,3}|[*†‡§¶]{1,3})(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingMark();

    /// <summary>A superscript worth keeping as one: a short number, ordinal or index (not text a large initial left raised).</summary>
    [GeneratedRegex(@"^[\p{L}\d+\-−*†‡§¶]{1,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Superscript();

    /// <summary>A table caption: "Table 2.1", "Tabelle 3", "Tab. 4", "TABLE I".</summary>
    [GeneratedRegex(@"^(?:table|tabelle|tab\.|tableau|tabla|tabella)\s*(?:\d|[ivxlc]+\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Caption();
}
