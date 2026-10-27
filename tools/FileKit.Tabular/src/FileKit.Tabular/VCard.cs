using System.Text;
using System.Text.RegularExpressions;

namespace FileKit.Tabular;

public sealed record VCardProperty(string Name, string Parameters, string Value);
public sealed record ContactCard(IReadOnlyList<VCardProperty> Properties);

/// <summary>UTF-8 vCard 3.0/4.0 parsing. Does not fetch URIs or decode attachments.
/// Unknown properties survive merging; tabular exports contain the documented common fields.</summary>
public static partial class VCard
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private static readonly string[] BasicColumns = ["Full Name", "Given Name", "Additional Name", "Family Name", "Prefix", "Suffix", "Organization", "Title", "Notes"];
    public static async Task<IReadOnlyList<ContactCard>> ReadAsync(Stream input, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > MaxBytes) throw new TabularException("vCard byte limit exceeded.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return await Task.Run(() => Parse(text, ct), ct).ConfigureAwait(false);
        }
        catch (DecoderFallbackException) { throw new TabularException("vCard must be UTF-8."); }
    }
    public static IReadOnlyList<ContactCard> Parse(string text, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes || text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')))
            throw new TabularException("Invalid vCard content or size.");
        var lines = new List<string>(); var logical = new StringBuilder();
        foreach (string line in text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (logical.Length == 0) throw new TabularException("Invalid vCard folding.");
                logical.Append(line.AsSpan(1));
            }
            else if (line.Length > 0) { if (logical.Length > 0) lines.Add(logical.ToString()); logical.Clear().Append(line); }
        }
        if (logical.Length > 0) lines.Add(logical.ToString());
        var cards = new List<ContactCard>(); List<VCardProperty>? properties = null;
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (properties is not null || cards.Count >= 100000) throw new TabularException("Invalid or excessive vCards.");
                properties = []; continue;
            }
            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (properties is null) throw new TabularException("Unexpected vCard end.");
                var versions = properties.Where(p => p.Name == "VERSION").ToArray();
                if (versions.Length != 1 || versions[0].Value is not ("3.0" or "4.0")) throw new TabularException("Only vCard 3.0 and 4.0 are supported.");
                foreach (string single in new[] { "FN", "N", "ORG", "TITLE", "NOTE" })
                    if (properties.Count(p => Leaf(p.Name) == single) > 1) throw new TabularException("Ambiguous repeated contact field.");
                cards.Add(new(Array.AsReadOnly(properties.ToArray()))); properties = null; continue;
            }
            if (properties is null || properties.Count >= 1024) throw new TabularException("Invalid vCard boundaries or property count.");
            int colon = Colon(line);
            if (colon <= 0) throw new TabularException("Invalid vCard property.");
            string header = line[..colon]; int semicolon = header.IndexOf(';');
            string name = (semicolon < 0 ? header : header[..semicolon]).ToUpperInvariant();
            string parameters = semicolon < 0 ? "" : header[semicolon..];
            if (!PropertyName().IsMatch(name) || parameters.Contains("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase) ||
                parameters.Contains("CHARSET=", StringComparison.OrdinalIgnoreCase) || parameters.Contains("ENCODING=B", StringComparison.OrdinalIgnoreCase) ||
                parameters.Contains("VALUE=BINARY", StringComparison.OrdinalIgnoreCase))
                throw new TabularException("Unsupported property name, charset or binary encoding.");
            properties.Add(new(name, parameters, line[(colon + 1)..]));
        }
        if (properties is not null || cards.Count == 0) throw new TabularException("Incomplete or empty vCard input.");
        return Array.AsReadOnly(cards.ToArray());
    }
    private static int Colon(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++) { if (line[i] == '"') quoted = !quoted; else if (line[i] == ':' && !quoted) return i; }
        return -1;
    }
    private static string Leaf(string name) => name[(name.LastIndexOf('.') + 1)..];
    public static string Write(IReadOnlyList<ContactCard> cards, CancellationToken ct = default)
    {
        var output = new StringBuilder(); long outputBytes = 0;
        void Line(string value)
        {
            int bytes = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 75) { output.Append("\r\n "); bytes = 1; outputBytes += 3; }
                output.Append(rune); bytes += rune.Utf8SequenceLength; outputBytes += rune.Utf8SequenceLength;
            }
            output.Append("\r\n"); outputBytes += 2;
            if (outputBytes > MaxBytes) throw new TabularException("vCard output limit exceeded.");
        }
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested(); Line("BEGIN:VCARD");
            foreach (var property in card.Properties)
            {
                ct.ThrowIfCancellationRequested();
                if (!PropertyName().IsMatch(property.Name) || (property.Name + property.Parameters + property.Value).Any(c => c < 32 && c != '\t'))
                    throw new TabularException("Invalid output property.");
                Line(property.Name + property.Parameters + ":" + property.Value);
            }
            Line("END:VCARD");
        }
        return output.ToString();
    }
    public static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);
    public static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                if (++i == text.Length) throw new TabularException("Incomplete vCard escape.");
                c = text[i];
                if (c is 'n' or 'N') c = '\n'; else if (c is not ('\\' or ';' or ',')) throw new TabularException("Unsupported vCard escape.");
            }
            result.Append(c);
        }
        return result.ToString();
    }
    private static string[] Parts(string value)
    {
        var parts = new List<string>(); int start = 0; bool escaped = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (value[i] == '\\') { escaped = true; continue; }
            if (value[i] == ';') { parts.Add(Unescape(value[start..i])); start = i + 1; }
        }
        parts.Add(Unescape(value[start..])); return parts.ToArray();
    }
    public static Table ToTable(IReadOnlyList<ContactCard> cards, CancellationToken ct = default)
    {
        if (cards.Count is < 1 or > 100000) throw new TabularException("Invalid contact count.");
        int phones = cards.Max(c => c.Properties.Count(p => Leaf(p.Name) == "TEL"));
        int emails = cards.Max(c => c.Properties.Count(p => Leaf(p.Name) == "EMAIL"));
        int addresses = cards.Max(c => c.Properties.Count(p => Leaf(p.Name) == "ADR"));
        var headers = BasicColumns.ToList();
        foreach (var (prefix, count) in new[] { ("Phone", phones), ("Email", emails), ("Address", addresses) })
            for (int i = 1; i <= count; i++) { headers.Add(prefix + " " + i); headers.Add(prefix + " " + i + " Type"); }
        if ((cards.Count + 1L) * headers.Count > 4_000_000) throw new TabularException("Contact table cell limit exceeded.");
        var rows = new List<IReadOnlyList<Cell>>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            var row = new List<Cell>();
            string Field(string name) => card.Properties.FirstOrDefault(p => Leaf(p.Name) == name)?.Value ?? "";
            string[] n = Parts(Field("N")); string Part(int i) => n.Length > i ? n[i] : "";
            foreach (string value in new[] { Unescape(Field("FN")), Part(1), Part(2), Part(0), Part(3), Part(4), string.Join(" / ", Parts(Field("ORG"))), Unescape(Field("TITLE")), Unescape(Field("NOTE")) }) row.Add(new TextCell(value));
            foreach (var (name, count) in new[] { ("TEL", phones), ("EMAIL", emails), ("ADR", addresses) })
            {
                var items = card.Properties.Where(p => Leaf(p.Name) == name).ToArray();
                for (int i = 0; i < count; i++)
                {
                    var item = i < items.Length ? items[i] : null;
                    string value = item?.Value ?? "";
                    if (name == "ADR") value = string.Join("\n", Parts(value));
                    else { value = Unescape(value); if (name == "TEL" && value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) value = value[4..]; }
                    row.Add(new TextCell(value)); row.Add(new TextCell(item is null ? "" : Type(item.Parameters)));
                }
            }
            rows.Add(row);
        }
        return new(headers, rows);
    }
    private static string Type(string parameters)
    {
        var match = TypeParameter().Match(parameters);
        return match.Success ? match.Groups[1].Value.Trim('"') : "";
    }
    public static IReadOnlyList<ContactCard> FromTable(Table table, CancellationToken ct = default)
    {
        if (table.Headers is null || table.Rows.Count == 0) throw new TabularException("Contacts need headers and data rows.");
        var headers = table.Headers;
        if (table.Rows.Count > 100000 || headers.Count is < 1 or > 1024 || (long)table.Rows.Count * headers.Count > 4000000 || table.Rows.Any(row => row.Count != headers.Count))
            throw new TabularException("Contact table shape or limits exceeded.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count) throw new TabularException("Ambiguous contact headers.");
        var lookup = headers.Select((header, index) => (header, index)).ToDictionary(x => x.header, x => x.index, StringComparer.OrdinalIgnoreCase);
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Phone"] = 0, ["Email"] = 1, ["Address"] = 0 };
        foreach (string header in headers)
        {
            var match = IndexedColumn().Match(header);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[2].Value, out int index) || index is < 1 or > 1024) throw new TabularException("Contact field index limit exceeded.");
            string field = match.Groups[1].Value.Equals("E-mail", StringComparison.OrdinalIgnoreCase) ? "Email" : match.Groups[1].Value;
            slots[field] = Math.Max(slots[field], index);
        }
        var cards = new List<ContactCard>(); var consumed = new HashSet<int>();
        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested(); var properties = new List<VCardProperty> { new("VERSION", "", "3.0") };
            string Field(params string[] names)
            {
                int[] matches = names.Where(lookup.ContainsKey).Select(name => lookup[name]).ToArray();
                if (matches.Length > 1) throw new TabularException("Ambiguous contact column mapping.");
                if (matches.Length == 0) return "";
                consumed.Add(matches[0]); return CsvWriter.FormatCellValue(row[matches[0]]);
            }
            string given = Field("Given Name", "First Name"), family = Field("Family Name", "Last Name"), additional = Field("Additional Name", "Middle Name"), prefix = Field("Prefix", "Name Prefix"), suffix = Field("Suffix", "Name Suffix");
            string full = Field("Full Name", "Name"); if (string.IsNullOrWhiteSpace(full)) full = string.Join(" ", new[] { prefix, given, additional, family, suffix }.Where(v => !string.IsNullOrWhiteSpace(v)));
            properties.Add(new("FN", "", Escape(full)));
            properties.Add(new("N", "", string.Join(";", new[] { family, given, additional, prefix, suffix }.Select(Escape))));
            foreach (var (name, value) in new[] { ("ORG", Field("Organization", "Company", "Organization 1 - Name")), ("TITLE", Field("Title", "Job Title", "Organization 1 - Title")), ("NOTE", Field("Notes", "Note")) })
                if (value.Length > 0) properties.Add(new(name, "", Escape(value)));
            foreach (string field in new[] { "Phone", "Email", "Address" })
            {
                for (int i = 1; i <= slots[field]; i++)
                {
                    string[] aliases = field == "Email" ? ["Email " + i, "E-mail " + i + " - Value", i == 1 ? "E-mail Address" : "E-mail " + i + " Address"]
                        : [field + " " + i, field + " " + i + " - Value"];
                    string value = Field(aliases); string type = Field(field + " " + i + " Type", (field == "Email" ? "E-mail" : field) + " " + i + " - Type");
                    if (value.Length == 0) continue;
                    if (type.Length > 0 && !TypeValue().IsMatch(type)) throw new TabularException("Unsupported contact type label.");
                    string encoded = field == "Address" ? string.Join(";", value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Escape)) : Escape(value);
                    properties.Add(new(field == "Phone" ? "TEL" : field == "Email" ? "EMAIL" : "ADR", type.Length == 0 ? "" : ";TYPE=" + type, encoded));
                }
            }
            foreach (var (column, type) in new[] { ("Mobile Phone", "CELL"), ("Home Phone", "HOME"), ("Business Phone", "WORK") })
            {
                string phone = Field(column); if (phone.Length > 0) properties.Add(new("TEL", ";TYPE=" + type, Escape(phone)));
            }
            if (full.Length == 0 && !properties.Any(p => p.Name is "TEL" or "EMAIL")) throw new TabularException("No mapped contact identity fields.");
            cards.Add(new(Array.AsReadOnly(properties.ToArray())));
        }
        if (headers.Select((_, index) => index).Any(index => !consumed.Contains(index) && table.Rows.Any(row => CsvWriter.FormatCellValue(row[index]).Length > 0)))
            throw new TabularException("Unmapped nonempty contact columns would be lost.");
        return Array.AsReadOnly(cards.ToArray());
    }
    [GeneratedRegex(@"^(?:[A-Z0-9-]+\.)?[A-Z0-9-]+$", RegexOptions.CultureInvariant)] private static partial Regex PropertyName();
    [GeneratedRegex("(?:^|;)TYPE=(\"[^\"]*\"|[^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex TypeParameter();
    [GeneratedRegex(@"^[A-Za-z0-9-]+(?:,[A-Za-z0-9-]+)*$", RegexOptions.CultureInvariant)] private static partial Regex TypeValue();
    [GeneratedRegex(@"^(Phone|Email|E-mail|Address) ([0-9]{1,6})(?: Type| - Value| - Type| Address)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex IndexedColumn();
}
