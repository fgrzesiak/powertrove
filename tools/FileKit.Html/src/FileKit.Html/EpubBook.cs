using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileKit.Html;

public enum EpubFailure { Invalid, Unsupported, Limit, Protected }
public sealed class EpubException(string message, EpubFailure failure = EpubFailure.Invalid, Exception? inner = null) : Exception(message, inner)
{ public EpubFailure Failure { get; } = failure; }
public sealed record EpubItem(string Id, string Path, string MediaType);
public interface IEpubDocumentBuilder
{
    HtmlPrintDocument Build(EpubBook book, CancellationToken cancellationToken);
}

/// <summary>In-memory EPUB resources. Never extracts archive paths onto the filesystem.</summary>
public sealed class EpubBook
{
    public const int MaxArchiveBytes = 25 * 1024 * 1024;
    public const int MaxResourceBytes = 10 * 1024 * 1024;
    public const int MaxExpandedBytes = 50 * 1024 * 1024;
    private readonly Dictionary<string, byte[]> _resources;
    private readonly IReadOnlyDictionary<string, EpubItem> _manifest;
    private EpubBook(Dictionary<string, byte[]> resources, Dictionary<string, EpubItem> manifest, List<EpubItem> chapters)
    { _resources = resources; _manifest = new ReadOnlyDictionary<string, EpubItem>(manifest); Chapters = chapters.AsReadOnly(); }
    public IReadOnlyList<EpubItem> Chapters { get; }
    public EpubItem ResolveItem(string ownerPath, string reference)
    {
        string path = ResolvePath(ownerPath, reference);
        return _manifest.TryGetValue(path, out var item) ? item : throw new EpubException("A required resource is absent from the EPUB manifest.");
    }
    public ReadOnlyMemory<byte> Read(EpubItem item) => _resources.TryGetValue(item.Path, out var bytes) ? bytes : throw new EpubException("A required EPUB resource is missing.");
    public string ReadText(EpubItem item)
    {
        try { return new UTF8Encoding(false, true).GetString(Read(item).Span).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex) { throw new EpubException("EPUB text must be UTF-8.", EpubFailure.Unsupported, ex); }
    }
    public static string ResolvePath(string ownerPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 2048 || reference.Contains('?') || reference.Contains('#')) throw new EpubException("Invalid EPUB resource reference.");
        string decoded;
        try { decoded = Uri.UnescapeDataString(reference); } catch (UriFormatException ex) { throw new EpubException("Invalid EPUB resource reference.", inner: ex); }
        if (decoded.StartsWith('/') || decoded.Contains('\\') || decoded.Contains(':') || decoded.Any(char.IsControl)) throw new EpubException("External EPUB resources are unsupported.", EpubFailure.Unsupported);
        var parts = ownerPath.Split('/').SkipLast(1).ToList();
        foreach (string part in decoded.Split('/'))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new EpubException("EPUB resource escapes its container."); parts.RemoveAt(parts.Count - 1); }
            else { if (part.Length == 0) throw new EpubException("Invalid EPUB resource path."); parts.Add(part); }
        }
        return string.Join('/', parts);
    }
    public static async Task<EpubBook> ReadAsync(Stream source, CancellationToken ct = default)
    {
        using var memory = new MemoryStream(); byte[] buffer = new byte[81920]; int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxArchiveBytes) throw new EpubException("EPUB archive size limit exceeded.", EpubFailure.Limit);
            await memory.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return await Task.Run(() => Parse(memory, ct), ct).ConfigureAwait(false);
    }
    private static EpubBook Parse(MemoryStream memory, CancellationToken ct)
    {
        try
        {
            memory.Position = 0; using var zip = new ZipArchive(memory, ZipArchiveMode.Read, true);
            if (zip.Entries.Count is 0 or > 3000) throw new EpubException("EPUB entry count limit exceeded.", EpubFailure.Limit);
            if (zip.Entries[0].FullName != "mimetype") throw new EpubException("Missing EPUB mimetype entry.");
            var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested(); string name = entry.FullName;
                if (name.Length > 2048 || name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl) || name.Split('/').Any(p => p is "." or "..")) throw new EpubException("Unsafe EPUB archive path.");
                if (!names.Add(name)) throw new EpubException("Ambiguous EPUB archive path.");
                if (name.EndsWith('/')) { if (entry.Length != 0) throw new EpubException("Invalid EPUB directory entry."); continue; }
                if (name.Split('/').Any(p => p.Length == 0)) throw new EpubException("Invalid EPUB archive path.");
                if (entry.Length > MaxResourceBytes || (total += entry.Length) > MaxExpandedBytes || entry.Length > 1024 * Math.Max(1, entry.CompressedLength)) throw new EpubException("EPUB expansion limit exceeded.", EpubFailure.Limit);
                using var input = entry.Open(); using var content = new MemoryStream();
                byte[] buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); if (content.Length + read > entry.Length) throw new EpubException("Invalid EPUB entry length."); content.Write(buffer, 0, read);
                }
                if (content.Length != entry.Length) throw new EpubException("Truncated EPUB resource.");
                resources.Add(name, content.ToArray());
            }
            if (Encoding.ASCII.GetString(Resource("mimetype")) != "application/epub+zip") throw new EpubException("Invalid EPUB mimetype.");
            // Encryption includes DRM and font obfuscation: neither is bypassed or silently dropped.
            if (resources.ContainsKey("META-INF/encryption.xml")) throw new EpubException("Encrypted or obfuscated EPUB resources are unsupported.", EpubFailure.Protected);
            XNamespace containerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
            var container = Xml(Resource("META-INF/container.xml"));
            var roots = container.Root?.Element(containerNs + "rootfiles")?.Elements(containerNs + "rootfile").ToArray();
            if (container.Root?.Name != containerNs + "container" || roots is not { Length: 1 } || (string?)roots[0].Attribute("media-type") != "application/oebps-package+xml") throw new EpubException("Unsupported EPUB container.", EpubFailure.Unsupported);
            string packagePath = ResolvePath("", (string?)roots[0].Attribute("full-path") ?? "");
            var package = Xml(Resource(packagePath)); XNamespace opf = "http://www.idpf.org/2007/opf";
            if (package.Root?.Name != opf + "package" || (string?)package.Root.Attribute("version") is not ("2.0" or "3.0" or "3.1" or "3.2" or "3.3")) throw new EpubException("Unsupported EPUB package version.", EpubFailure.Unsupported);
            if (package.Descendants(opf + "meta").Any(m => (string?)m.Attribute("property") == "rendition:layout" && m.Value.Trim() == "pre-paginated")) throw new EpubException("Fixed-layout EPUB is unsupported.", EpubFailure.Unsupported);
            var byId = new Dictionary<string, EpubItem>(StringComparer.Ordinal); var byPath = new Dictionary<string, EpubItem>(StringComparer.Ordinal);
            foreach (var element in package.Root.Element(opf + "manifest")?.Elements(opf + "item") ?? [])
            {
                ct.ThrowIfCancellationRequested(); string id = (string?)element.Attribute("id") ?? ""; string type = (string?)element.Attribute("media-type") ?? "";
                string path = ResolvePath(packagePath, (string?)element.Attribute("href") ?? "");
                if (id.Length is 0 or > 256 || type.Length is 0 or > 128 || !resources.ContainsKey(path)) throw new EpubException("Invalid EPUB manifest.");
                var item = new EpubItem(id, path, type); if (!byId.TryAdd(id, item) || !byPath.TryAdd(path, item)) throw new EpubException("Duplicate EPUB manifest entry.");
                string properties = (string?)element.Attribute("properties") ?? "";
                if (properties.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(p => p is "scripted" or "remote-resources")) throw new EpubException("Scripted or remote EPUB resources are unsupported.", EpubFailure.Unsupported);
            }
            var chapters = new List<EpubItem>();
            var spine = package.Root.Element(opf + "spine") ?? throw new EpubException("Missing EPUB spine.");
            foreach (var reference in spine.Elements(opf + "itemref"))
            {
                ct.ThrowIfCancellationRequested();
                if (!byId.TryGetValue((string?)reference.Attribute("idref") ?? "", out var chapter) || chapter.MediaType != "application/xhtml+xml") throw new EpubException("Unsupported EPUB reading-order content.", EpubFailure.Unsupported);
                // Include non-linear entries too: never silently discard supplementary chapters.
                chapters.Add(chapter); if (chapters.Count > 500) throw new EpubException("EPUB chapter limit exceeded.", EpubFailure.Limit);
                if (((string?)reference.Attribute("properties") ?? "").Contains("rendition:layout-pre-paginated", StringComparison.Ordinal)) throw new EpubException("Fixed-layout EPUB is unsupported.", EpubFailure.Unsupported);
            }
            if (chapters.Count == 0) throw new EpubException("EPUB has no readable chapters.");
            return new(resources, byPath, chapters);
            byte[] Resource(string path) => resources.TryGetValue(path, out var value) ? value : throw new EpubException("Required EPUB metadata is missing.");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or NotSupportedException or ArgumentException or OverflowException)
        { throw new EpubException("The EPUB archive or metadata is invalid.", inner: ex); }
    }
    private static XDocument Xml(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new EpubException("EPUB metadata limit exceeded.", EpubFailure.Limit);
        using var stream = new MemoryStream(bytes);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
        using (var bounded = XmlReader.Create(stream, settings))
        {
            int elements = 0;
            while (bounded.Read()) if (bounded.Depth > 128 || bounded.NodeType == XmlNodeType.Element && ++elements > 10000)
                throw new EpubException("EPUB metadata structure limit exceeded.", EpubFailure.Limit);
        }
        stream.Position = 0; using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader); if (document.Descendants().Count() > 10000) throw new EpubException("EPUB metadata element limit exceeded.", EpubFailure.Limit); return document;
    }
}
