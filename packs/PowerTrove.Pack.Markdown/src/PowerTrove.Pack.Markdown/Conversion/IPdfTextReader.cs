namespace PowerTrove.Pack.Markdown;

/// <summary>
/// Reads the text layer of a PDF with positions and font properties, grouped into blocks in reading order
/// (columns resolved). Implementations never render pages, run scripts or load external resources.
/// </summary>
public interface IPdfTextReader
{
    /// <param name="images">Also read the pictures placed on the pages, with their position.</param>
    Task<PdfTextDocument> ReadAsync(Stream source, bool images, CancellationToken cancellationToken);
}

/// <param name="SkippedImages">Pictures that could not be decoded (for example JPEG 2000) or exceeded the limits.</param>
/// <param name="Outline">The document's bookmarks in order, flattened with their depth.</param>
/// <param name="Tags">The structure of a tagged PDF, referenced by <see cref="PdfTextWord.Tag"/>.</param>
public sealed record PdfTextDocument(IReadOnlyList<PdfTextPage> Pages, int SkippedWords = 0, int SkippedImages = 0,
    IReadOnlyList<PdfTextBookmark>? Outline = null, IReadOnlyList<PdfTextTag>? Tags = null)
{
    public override string ToString() => $"PdfTextDocument {{ Pages = {Pages.Count} }}";
}

/// <param name="Rules">Straight stroked lines, used to find ruled tables.</param>
/// <param name="Links">Link areas: web or mail addresses, or places in the document.</param>
public sealed record PdfTextPage(int Number, double Width, double Height, IReadOnlyList<PdfTextBlock> Blocks, IReadOnlyList<FileKit.Tabular.TableRule> Rules,
    IReadOnlyList<PdfPageImage>? Images = null, IReadOnlyList<PdfTextLink>? Links = null)
{
    public override string ToString() => $"PdfTextPage {{ Number = {Number}, Blocks = {Blocks.Count} }}";
}

public sealed record PdfTextBlock(IReadOnlyList<PdfTextLine> Lines);

public sealed record PdfTextLine(IReadOnlyList<PdfTextWord> Words);

/// <summary>
/// PDF coordinates: the origin is at the bottom left, so Top is greater than Bottom. <see cref="Tag"/> indexes the
/// document's tags when the PDF is tagged; <see cref="Artifact"/> marks content the PDF declares as not part of the
/// text (running headers, footers, page numbers). <see cref="RaisedFrom"/> is the index in <see cref="Text"/> where
/// trailing superscript characters begin (the footnote mark in "word¹"), or -1.
/// </summary>
public sealed record PdfTextWord(string Text, double Left, double Bottom, double Right, double Top, double FontSize, bool Bold = false, bool Italic = false, bool Monospace = false,
    int? Tag = null, bool Artifact = false, int RaisedFrom = -1)
{
    public override string ToString() => $"PdfTextWord {{ FontSize = {FontSize} }}";
}

/// <summary>A picture where it is drawn on the page, encoded as JPEG or PNG, with the alternative text a tagged PDF gives it.</summary>
public sealed record PdfPageImage(double Left, double Bottom, double Right, double Top, byte[] Bytes, string Extension, string? Alt = null)
{
    public override string ToString() => $"PdfPageImage {{ Extension = {Extension}, Bytes = {Bytes.Length} }}";
}

public enum PdfTextTagKind { Paragraph, Heading, ListItem, TableCell, Figure, Code }

/// <summary>
/// What a tagged PDF says a piece of text is. <see cref="Group"/> identifies the element: one heading, one list item or
/// one table. Headings carry their level (1 to 6) and list items their depth (1 for the outer list) in
/// <see cref="Level"/>; table cells carry their row, column, column span and whether they are header cells.
/// </summary>
public sealed record PdfTextTag(PdfTextTagKind Kind, int Level = 0, int Group = 0, int Row = 0, int Column = 0, int ColumnSpan = 1, bool Header = false, string? Alt = null);

/// <summary>A bookmark: its title, depth (1 for the top level) and the page number and top it points to.</summary>
public sealed record PdfTextBookmark(string Title, int Level, int Page, double? Top);

/// <summary>A link area on a page: either an http, https or mailto address, or a page number and top in the document.</summary>
public sealed record PdfTextLink(double Left, double Bottom, double Right, double Top, string? Uri, int? TargetPage, double? TargetTop);

public enum PdfTextFailure { Invalid, Encrypted, TooComplex }

public sealed class PdfTextException(PdfTextFailure failure, string message) : Exception(message)
{
    public PdfTextFailure Failure { get; } = failure;
}
