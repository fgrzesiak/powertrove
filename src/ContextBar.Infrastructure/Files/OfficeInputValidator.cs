using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ContextBar.Abstractions.Office;
using FileKit.Pdf;

namespace ContextBar.Infrastructure.Files;

/// <summary>
/// Bounded container preflight, not a general Office parser. Rejects encrypted containers and
/// XLM sheets before Excel starts; AutomationSecurity alone does not disable Excel 4 macros.
/// </summary>
public sealed class OfficeInputValidator : IOfficeInputValidator
{
    public const int MaximumBytes = 64 * 1024 * 1024;

    public async Task ValidateAsync(string path, OfficeApplication application, bool pdfToWord, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length is <= 0 or > MaximumBytes) Fail();
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => Validate(bytes, Path.GetExtension(path).ToLowerInvariant(), application, pdfToWord, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static void Validate(byte[] bytes, string extension, OfficeApplication application, bool pdfToWord, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > MaximumBytes) Fail();
        if (pdfToWord)
        {
            if (application != OfficeApplication.Word || extension != ".pdf" || !bytes.AsSpan().StartsWith("%PDF-"u8)) Fail();
            try { _ = PdfDocument.Load(bytes); }
            catch (PdfPasswordRequiredException) { throw new OfficeConversionException(OfficeFailure.PasswordProtected); }
            catch (PdfEncryptedException) { throw new OfficeConversionException(OfficeFailure.PasswordProtected); }
            catch (PdfException) { Fail(); }
            return;
        }
        if (OfficeFormats.ApplicationFor("input" + extension) != application) Fail();
        if (bytes.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            var compound = new Compound(bytes);
            if (compound.Contains("EncryptedPackage") || compound.Contains("EncryptionInfo"))
                throw new OfficeConversionException(OfficeFailure.PasswordProtected);
            if (extension is not (".doc" or ".xls" or ".ppt")) Fail();
            if (application == OfficeApplication.Word)
            {
                byte[] word = compound.Read("WordDocument");
                if (word.Length < 32 || U16(word, 0) != 0xA5EC) Fail();
                if ((U16(word, 10) & 0x0100) != 0) throw new OfficeConversionException(OfficeFailure.PasswordProtected);
            }
            if (application == OfficeApplication.PowerPoint)
            {
                if (!compound.Contains("PowerPoint Document")) Fail();
                byte[] current = compound.Read("Current User");
                if (current.Length < 16 || U32(current, 8) != 20) Fail();
                if (U32(current, 12) == 0xF3D1C4DF) throw new OfficeConversionException(OfficeFailure.PasswordProtected);
                if (U32(current, 12) != 0xE391C05F) Fail();
            }
            if (application == OfficeApplication.Excel) ValidateBiff(compound.Read("Workbook"), cancellationToken);
            return;
        }
        if (extension == ".rtf")
        {
            if (!bytes.AsSpan().StartsWith("{\\rtf"u8)) Fail();
            return;
        }
        if (extension == ".csv")
        {
            // Formula-capable CSV is not opened by automation. Ordinary data is still supported.
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { Fail(); return; }
            if (text.Any(c => c == '\0' || c < ' ' && c is not ('\t' or '\r' or '\n'))) Fail();
            bool quoted = false, start = true;
            for (int i = 0; i < text.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                char c = text[i];
                if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; } quoted = !quoted; continue; }
                if (!quoted && c is ',' or ';' or '\r' or '\n') { start = true; continue; }
                if (start && char.IsWhiteSpace(c)) continue;
                if (start && c is '=' or '+' or '-' or '@') throw new OfficeConversionException(OfficeFailure.UnsafeMacros);
                start = false;
            }
            if (quoted) Fail();
            return;
        }
        try
        {
            int declaredEntries = ZipEntryCount(bytes);
            using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            if (zip.Entries.Count != declaredEntries) Fail();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.Length > 1024 || !names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Contains("..")) Fail();
                total = checked(total + entry.Length);
                if (total > 256L * 1024 * 1024) Fail();
                if (entry.FullName.Contains("macrosheet", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
                    throw new OfficeConversionException(OfficeFailure.UnsafeMacros);
                if (entry.FullName == "[Content_Types].xml" || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) || entry.FullName == "META-INF/manifest.xml")
                {
                    if (entry.Length > 1024 * 1024) Fail();
                    using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024, CloseInput = true });
                    var metadata = XDocument.Load(xml);
                    foreach (var element in metadata.Descendants())
                    {
                        if (element.Name.LocalName == "encryption-data") throw new OfficeConversionException(OfficeFailure.PasswordProtected);
                        foreach (var attribute in element.Attributes())
                        {
                            if (attribute.Name.LocalName is "Type" or "ContentType" &&
                                (attribute.Value.Contains("macro", StringComparison.OrdinalIgnoreCase) || attribute.Value.Contains("vba", StringComparison.OrdinalIgnoreCase)))
                                throw new OfficeConversionException(OfficeFailure.UnsafeMacros);
                            if (attribute.Name.LocalName == "TargetMode" && attribute.Value.Equals("External", StringComparison.OrdinalIgnoreCase))
                                throw new OfficeConversionException(OfficeFailure.InvalidDocument);
                        }
                    }
                }
            }
            string required = extension switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", ".pptx" => "ppt/presentation.xml", ".odt" or ".ods" or ".odp" => "content.xml", _ => "" };
            if (required.Length == 0 || !names.Contains(required)) Fail();
            if (extension is ".odt" or ".ods" or ".odp")
            {
                var mime = zip.GetEntry("mimetype");
                if (mime is null || mime.Length > 128) Fail();
                using var reader = new StreamReader(mime!.Open(), Encoding.ASCII);
                string expected = extension switch { ".odt" => "text", ".ods" => "spreadsheet", _ => "presentation" };
                if (reader.ReadToEnd() != "application/vnd.oasis.opendocument." + expected) Fail();
            }
            else if (!names.Contains("[Content_Types].xml")) Fail();
        }
        catch (InvalidDataException) { Fail(); }
        catch (OverflowException) { Fail(); }
        catch (XmlException) { Fail(); }
    }

    private static int ZipEntryCount(byte[] bytes)
    {
        // Check bounded EOCD metadata before ZipArchive allocates its entry collection.
        // Multi-disk/ZIP64 packages are intentionally unsupported by this small Office preflight.
        for (int p = bytes.Length - 22; p >= Math.Max(0, bytes.Length - 65557); p--)
        {
            if (U32(bytes, p) != 0x06054B50 || p + 22 + U16(bytes, p + 20) != bytes.Length) continue;
            int count = U16(bytes, p + 10);
            if (U16(bytes, p + 4) != 0 || U16(bytes, p + 6) != 0 || U16(bytes, p + 8) != count || count is 0 or > 10000 ||
                U32(bytes, p + 12) > 8 * 1024 * 1024 || (long)U32(bytes, p + 16) + U32(bytes, p + 12) > p) Fail();
            return count;
        }
        Fail(); return 0;
    }

    private static void ValidateBiff(byte[] workbook, CancellationToken ct)
    {
        int position = 0, sheets = 0;
        var sheetPositions = new HashSet<int>();
        if (workbook.Length < 8 || U16(workbook, 0) != 0x0809 || U16(workbook, 4) != 0x0600 || U16(workbook, 6) != 0x0005) Fail();
        while (position + 4 <= workbook.Length)
        {
            ct.ThrowIfCancellationRequested();
            int id = U16(workbook, position), length = U16(workbook, position + 2);
            position += 4;
            if (position + length > workbook.Length) Fail();
            if (id == 0x002F) throw new OfficeConversionException(OfficeFailure.PasswordProtected);
            if (id == 0x0085)
            {
                if (length < 8) Fail();
                int type = workbook[position + 5];
                if (type is not (0 or 2)) throw new OfficeConversionException(OfficeFailure.UnsafeMacros);
                uint offset = U32(workbook, position);
                if (offset > workbook.Length - 8 || !sheetPositions.Add((int)offset)) Fail();
                int start = (int)offset;
                if (U16(workbook, start) != 0x0809 || U16(workbook, start + 2) < 4 || U16(workbook, start + 4) != 0x0600) Fail();
                int substream = U16(workbook, start + 6);
                if (substream != (type == 2 ? 0x0020 : 0x0010)) throw new OfficeConversionException(OfficeFailure.UnsafeMacros);
                sheets++;
            }
            if (id == 0x000A) { if (sheets == 0) Fail(); return; }
            position += length;
        }
        Fail();
    }

    private static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i, 2));
    private static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i, 4));
    private static void Fail() => throw new OfficeConversionException(OfficeFailure.InvalidDocument);

    // Intentionally limited to small, well-formed CFB v3/v4 files, bounded chains and one Workbook.
    private sealed class Compound
    {
        private readonly byte[] _bytes;
        private readonly int _size;
        private readonly List<uint> _fat = new();
        private readonly Dictionary<string, (uint Start, int Length)> _streams = new(StringComparer.OrdinalIgnoreCase);
        private readonly uint _miniFatStart;
        private (uint Start, int Length) _root;

        public Compound(byte[] bytes)
        {
            _bytes = bytes;
            if (bytes.Length < 512 || U16(bytes, 28) != 0xFFFE || U16(bytes, 32) != 6 || U32(bytes, 56) != 4096) Fail();
            int shift = U16(bytes, 30), major = U16(bytes, 26);
            if (!(major == 3 && shift == 9 || major == 4 && shift == 12)) Fail();
            _size = 1 << shift;
            if (bytes.Length % _size != 0) Fail();
            uint count = U32(bytes, 44);
            // Extended DIFAT files are refused rather than partially interpreted.
            if (count is 0 or > 109 || U32(bytes, 72) != 0) Fail();
            var usedFat = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = U32(bytes, 76 + i * 4);
                if (!usedFat.Add(id)) Fail();
                byte[] sector = Sector(id);
                for (int j = 0; j < _size; j += 4) _fat.Add(U32(sector, j));
            }
            byte[] directory = Chain(U32(bytes, 48), _fat, _size, null, 1024 * 1024);
            for (int p = 0; p + 128 <= directory.Length; p += 128)
            {
                byte type = directory[p + 66];
                if (type == 0) continue;
                int length = U16(directory, p + 64);
                if (length < 2 || length > 64 || length % 2 != 0) Fail();
                string name = Encoding.Unicode.GetString(directory, p, length - 2);
                ulong streamLength = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(p + 120, 8));
                if (streamLength > MaximumBytes) Fail();
                var entry = (U32(directory, p + 116), (int)streamLength);
                if (type == 5) { if (p != 0) Fail(); _root = entry; }
                else if (type == 2 && !_streams.TryAdd(name, entry)) Fail();
            }
            _miniFatStart = U32(bytes, 60);
        }
        public bool Contains(string name) => _streams.ContainsKey(name);
        public byte[] Read(string name)
        {
            if (!_streams.TryGetValue(name, out var entry)) { Fail(); return Array.Empty<byte>(); }
            if (entry.Length >= 4096) return Trim(Chain(entry.Start, _fat, _size, null, MaximumBytes), entry.Length);
            byte[] miniStream = Trim(Chain(_root.Start, _fat, _size, null, MaximumBytes), _root.Length);
            byte[] table = Chain(_miniFatStart, _fat, _size, null, 1024 * 1024);
            var miniFat = new List<uint>();
            for (int i = 0; i + 4 <= table.Length; i += 4) miniFat.Add(U32(table, i));
            return Trim(Chain(entry.Start, miniFat, 64, miniStream, MaximumBytes), entry.Length);
        }
        private byte[] Sector(uint id)
        {
            long offset = ((long)id + 1) * _size;
            if (offset < _size || offset + _size > _bytes.Length) Fail();
            return _bytes.AsSpan((int)offset, _size).ToArray();
        }
        private byte[] Chain(uint first, List<uint> table, int size, byte[]? mini, int limit)
        {
            using var output = new MemoryStream();
            var seen = new HashSet<uint>();
            uint id = first;
            while (id != 0xFFFFFFFE)
            {
                if (id >= table.Count || !seen.Add(id) || output.Length + size > limit) Fail();
                if (mini is null) output.Write(Sector(id));
                else
                {
                    long offset = (long)id * size;
                    if (offset + size > mini.Length) Fail();
                    output.Write(mini, (int)offset, size);
                }
                id = table[(int)id];
            }
            return output.ToArray();
        }
        private static byte[] Trim(byte[] b, int length) { if (length <= 0 || length > b.Length) Fail(); return b.AsSpan(0, length).ToArray(); }
    }
}
