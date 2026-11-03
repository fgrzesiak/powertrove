using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace PowerTrove.Pack.Markdown.Conversion;

/// <summary>
/// Read-only access to a ZIP-based document (Word, PowerPoint, EPUB) with bounds: entry count, expanded size
/// per entry and in total, and XML without DTDs or external entities. Paths are resolved inside the archive only.
/// </summary>
internal sealed class ZipDocument : IDisposable
{
    public const long MaximumFileBytes = 100L * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const long MaximumTotalBytes = 512L * 1024 * 1024;

    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _expanded;

    private ZipDocument(ZipArchive archive)
    {
        _archive = archive;
        if (archive.Entries.Count > MaximumEntries) throw Invalid();
        foreach (var entry in archive.Entries) _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
    }

    public static async Task<ZipDocument> OpenAsync(Stream source, CancellationToken ct)
    {
        var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > MaximumFileBytes) throw new FileProcessingException(SR.Get("TooLarge"));
            memory.Write(buffer, 0, read);
        }
        memory.Position = 0;
        try { return new ZipDocument(new ZipArchive(memory, ZipArchiveMode.Read)); }
        catch (InvalidDataException) { throw Invalid(); }
    }

    public bool Exists(string path) => _entries.ContainsKey(Normalize(path));

    public IEnumerable<string> Paths => _entries.Keys;

    public byte[]? ReadBytes(string path, long maximum = MaximumEntryBytes)
    {
        if (!_entries.TryGetValue(Normalize(path), out var entry)) return null;
        if (entry.Length > Math.Min(maximum, MaximumEntryBytes)) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        try
        {
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (memory.Length + read > Math.Min(maximum, MaximumEntryBytes) || (_expanded += read) > MaximumTotalBytes) return null;
                memory.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException) { throw Invalid(); }
        return memory.ToArray();
    }

    public XDocument? ReadXml(string path)
    {
        byte[]? bytes = ReadBytes(path);
        if (bytes is null) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { throw Invalid(); }
    }

    /// <summary>Relationships of a part: id to (target path inside the archive or external address, external).</summary>
    public IReadOnlyDictionary<string, (string Target, bool External, string Type)> Relationships(string partPath)
    {
        string folder = Folder(partPath);
        string relsPath = (folder.Length == 0 ? "" : folder + "/") + "_rels/" + Path.GetFileName(partPath) + ".rels";
        var result = new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal);
        var xml = ReadXml(relsPath);
        if (xml?.Root is null) return result;
        foreach (var rel in xml.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            string? id = (string?)rel.Attribute("Id"), target = (string?)rel.Attribute("Target");
            if (id is null || target is null) continue;
            bool external = string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            result[id] = (external ? target : Resolve(folder, target), external, (string?)rel.Attribute("Type") ?? string.Empty);
        }
        return result;
    }

    /// <summary>A path relative to a folder inside the archive, or null when it would leave the archive.</summary>
    public static string Resolve(string folder, string target)
    {
        string path = Uri.UnescapeDataString(target.Split('#')[0]);
        var parts = new List<string>(path.StartsWith('/') ? [] : folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    public static string Folder(string path)
    {
        int slash = Normalize(path).LastIndexOf('/');
        return slash < 0 ? string.Empty : Normalize(path)[..slash];
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public static DocumentFormatException Invalid() => new(SR.Get("ToMarkdownInvalid"));

    public void Dispose() => _archive.Dispose();
}

/// <summary>File extensions for images found inside documents, decided by content.</summary>
internal static class ImageKinds
{
    public static string? Extension(byte[] bytes) => FileKit.Detection.FileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, FileKit.Detection.FileSignature.HeaderLength))).Format switch
    {
        FileKit.Detection.FileFormat.Png => ".png",
        FileKit.Detection.FileFormat.Jpeg => ".jpg",
        FileKit.Detection.FileFormat.Gif => ".gif",
        FileKit.Detection.FileFormat.WebP => ".webp",
        FileKit.Detection.FileFormat.Bmp => ".bmp",
        FileKit.Detection.FileFormat.Avif => ".avif",
        _ => IsSvg(bytes) ? ".svg" : null
    };

    private static bool IsSvg(byte[] bytes)
    {
        try { return bytes.Length < 2 * 1024 * 1024 && new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, Math.Min(bytes.Length, 4096)).Contains("<svg", StringComparison.Ordinal); }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
