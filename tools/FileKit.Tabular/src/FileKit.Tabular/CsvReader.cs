using System.Text;

namespace FileKit.Tabular;

/// <summary>Bounded RFC 4180 reader. Cells stay text; malformed and ragged rows are rejected.</summary>
public static class CsvReader
{
    public static async Task<Table> ReadAsync(Stream input, bool firstRowAsHeader = true, char? delimiter = null,
        CsvReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new();
        if (limits.MaxRows < 1 || limits.MaxColumns < 1 || limits.MaxBytes < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + n > limits.MaxBytes) throw new TabularException("CSV byte limit exceeded.");
            await bytes.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new TabularException("CSV must be valid UTF-8."); }
        return await Task.Run(() => Parse(text, firstRowAsHeader, delimiter, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static Table Parse(string text, bool firstRowAsHeader = true, char? delimiter = null,
        CsvReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new();
        if (Encoding.UTF8.GetByteCount(text) > limits.MaxBytes) throw new TabularException("CSV byte limit exceeded.");
        text = text.TrimStart('\uFEFF');
        char separator = delimiter ?? DetectDelimiter(text);
        if (separator is not (',' or ';' or '\t')) throw new TabularException("Unsupported CSV delimiter.");
        var rows = new List<IReadOnlyList<Cell>>();
        var row = new List<Cell>();
        var field = new StringBuilder();
        bool quoted = false, closed = false, started = false;
        int? width = null; int cells = 0;
        void Field()
        {
            if (row.Count >= limits.MaxColumns || ++cells > limits.MaxCells) throw new TabularException("CSV column or cell limit exceeded.");
            row.Add(new TextCell(field.ToString())); field.Clear(); closed = false; started = false;
        }
        void Row()
        {
            Field();
            width ??= row.Count;
            if (row.Count != width) throw new TabularException("CSV rows have different column counts.");
            if (rows.Count >= limits.MaxRows) throw new TabularException("CSV row limit exceeded.");
            rows.Add(row.ToArray()); row.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c == separator) Field();
            else if (c is '\r' or '\n') { Row(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '"' && !started && !closed) { quoted = true; started = true; }
            else
            {
                if (closed || c == '"') throw new TabularException("Malformed CSV quoting.");
                field.Append(c); started = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (quoted) throw new TabularException("CSV has an unterminated quoted field.");
        if (started || closed || row.Count > 0 || field.Length > 0) Row();
        if (firstRowAsHeader && rows.Count > 0)
            return new Table(JsonTableWriter.ResolveHeaders(rows[0]), rows.Skip(1).ToArray());
        return new Table(null, rows);
    }

    private static char DetectDelimiter(string text)
    {
        var counts = new int[3]; char[] candidates = [',', ';', '\t'];
        bool quoted = false; int lines = 0;
        for (int i = 0; i < text.Length && lines < 10; i++)
        {
            char c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++; else quoted = !quoted; }
            else if (!quoted)
            {
                if (c == '\n' || (c == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))) lines++;
                int index = Array.IndexOf(candidates, c); if (index >= 0) counts[index]++;
            }
        }
        return candidates[Array.IndexOf(counts, counts.Max())];
    }
}
