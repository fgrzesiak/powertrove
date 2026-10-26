using System.Net;
using System.Text;

namespace FileKit.Text;

public enum EditKind { Unchanged, Added, Removed }
public sealed record LineEdit(EditKind Kind, string Text);
public sealed record DiffResult(IReadOnlyList<LineEdit> Lines)
{
    public int Added => Lines.Count(x => x.Kind == EditKind.Added);
    public int Removed => Lines.Count(x => x.Kind == EditKind.Removed);
    public int Unchanged => Lines.Count(x => x.Kind == EditKind.Unchanged);
}
public sealed class TextLimitException(string message) : Exception(message);
public sealed record ReportLabels(string Title, string Added, string Removed, string Unchanged);

public static class LineDiff
{
    public const int MaxInputBytes = 5 * 1024 * 1024;
    public static async Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > MaxInputBytes) throw new TextLimitException("Input exceeds the text comparison limit.");
            bytes.Write(buffer, 0, read);
        }
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
    public static DiffResult Compare(string before, string after, bool ignoreLineEndings = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = Split(before, ignoreLineEndings); var b = Split(after, ignoreLineEndings);
        if (a.Length > 100000 || b.Length > 100000) throw new TextLimitException("Too many lines for an interactive comparison.");
        int prefix = 0, suffix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var result = new List<LineEdit>();
        for (int i = 0; i < prefix; i++) result.Add(new(EditKind.Unchanged, a[i]));
        result.AddRange(Myers(a[prefix..(a.Length - suffix)], b[prefix..(b.Length - suffix)], cancellationToken));
        for (int i = a.Length - suffix; i < a.Length; i++) result.Add(new(EditKind.Unchanged, a[i]));
        return new(result);
    }
    private static IReadOnlyList<LineEdit> Myers(string[] a, string[] b, CancellationToken ct)
    {
        if (a.Length == 0) return b.Select(s => new LineEdit(EditKind.Added, s)).ToArray();
        if (b.Length == 0) return a.Select(s => new LineEdit(EditKind.Removed, s)).ToArray();
        int maximum = a.Length + b.Length;
        var trace = new List<int[]>(); long work = 0;
        int At(int[] values, int depth, int diagonal) => diagonal < -depth || diagonal > depth ? -1 : values[diagonal + depth + 1];
        for (int d = 0; d <= maximum; d++)
        {
            ct.ThrowIfCancellationRequested();
            if ((work += 2L * d + 3) > 8000000) throw new TextLimitException("The comparison exceeds the supported complexity limit.");
            var v = Enumerable.Repeat(-1, 2 * d + 3).ToArray();
            int[] previous = d == 0 ? [] : trace[d - 1];
            for (int k = -d; k <= d; k += 2)
            {
                int x = d == 0 ? 0 : k == -d || k != d && At(previous, d - 1, k - 1) < At(previous, d - 1, k + 1)
                    ? At(previous, d - 1, k + 1) : At(previous, d - 1, k - 1) + 1;
                int y = x - k;
                while (x < a.Length && y < b.Length && x >= 0 && y >= 0 && a[x] == b[y])
                { if (++work > 8000000) throw new TextLimitException("The comparison exceeds the supported complexity limit."); if ((work & 4095) == 0) ct.ThrowIfCancellationRequested(); x++; y++; }
                v[k + d + 1] = x;
                if (x >= a.Length && y >= b.Length)
                {
                    trace.Add(v);
                    var edits = new List<LineEdit>(); x = a.Length; y = b.Length;
                    for (int depth = d; depth > 0; depth--)
                    {
                        ct.ThrowIfCancellationRequested();
                        int diagonal = x - y; var prev = trace[depth - 1];
                        int prevK = diagonal == -depth || diagonal != depth && At(prev, depth - 1, diagonal - 1) < At(prev, depth - 1, diagonal + 1) ? diagonal + 1 : diagonal - 1;
                        int prevX = At(prev, depth - 1, prevK), prevY = prevX - prevK;
                        while (x > prevX && y > prevY) { edits.Add(new(EditKind.Unchanged, a[--x])); y--; }
                        if (x == prevX) edits.Add(new(EditKind.Added, b[--y]));
                        else edits.Add(new(EditKind.Removed, a[--x]));
                    }
                    while (x > 0 && y > 0) { edits.Add(new(EditKind.Unchanged, a[--x])); y--; }
                    edits.Reverse(); return edits;
                }
            }
            trace.Add(v);
        }
        throw new InvalidOperationException("No diff path found.");
    }
    private static string[] Split(string value, bool normalize)
    {
        if (value.Length == 0) return [];
        var lines = new List<string>(); int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('\r' or '\n')) continue;
            int end = i;
            if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
            lines.Add(value[start..(normalize ? end : i + 1)]); start = i + 1;
        }
        if (start < value.Length) lines.Add(value[start..]);
        return lines.ToArray();
    }
    public static async Task WriteHtmlAsync(TextWriter output, DiffResult diff, ReportLabels labels, CancellationToken cancellationToken = default)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        await output.WriteAsync(("<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><meta name=\"viewport\" content=\"width=device-width\"><title>" + E(labels.Title) + "</title><style>body{font:16px system-ui;margin:2rem;color:#18202a;background:#fff}table{width:100%;border-collapse:collapse}td{vertical-align:top;border-bottom:1px solid #ddd}pre{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.added{background:#e0f4e6}.removed{background:#fbe4e4}.mark{width:2rem}caption{text-align:left;margin-bottom:1rem}</style></head><body><h1>" + E(labels.Title) + "</h1><table><caption>" + E(labels.Added) + ": " + diff.Added + " · " + E(labels.Removed) + ": " + diff.Removed + " · " + E(labels.Unchanged) + ": " + diff.Unchanged + "</caption><tbody>").AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var line in diff.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string kind = line.Kind == EditKind.Added ? "added" : line.Kind == EditKind.Removed ? "removed" : "unchanged";
            string mark = line.Kind == EditKind.Added ? "+" : line.Kind == EditKind.Removed ? "−" : " ";
            await output.WriteAsync(($"<tr class=\"{kind}\"><td class=\"mark\">{mark}</td><td><pre>{E(line.Text)}</pre></td></tr>").AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await output.WriteAsync("</tbody></table></body></html>".AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
