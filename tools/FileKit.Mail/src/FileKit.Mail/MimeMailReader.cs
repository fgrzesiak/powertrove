using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileKit.Mail;

public sealed class MimeMailException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record MimeMailLimits
{
    public int MaxInputBytes { get; init; } = 25 * 1024 * 1024;
    public int MaxParts { get; init; } = 1000;
    public int MaxDepth { get; init; } = 32;
    public int MaxHeaders { get; init; } = 256;
    public int MaxHeaderValueBytes { get; init; } = 32768;
    public int MaxDelimiters { get; init; } = 2200;
    public int MaxAttachmentCount { get; init; } = 100;
    public int MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxInlineImageBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxBodyBytes { get; init; } = 5 * 1024 * 1024;
    public long MaxTotalDecodedBytes { get; init; } = 25L * 1024 * 1024;
}

public sealed record MimeMailPart(
    string MediaType,
    string? ContentId,
    string? FileName,
    bool IsAttachment,
    bool IsMessageAttachment,
    ReadOnlyMemory<byte> Content);

public sealed record MimeMailMessage(
    string From, string To, string Cc, string Subject, string Date,
    string? HtmlBody, string? TextBody, IReadOnlyList<MimeMailPart> Parts);

/// <summary>
/// BCL-only bounded RFC 5322 / MIME reader. Never executes content or opens external URLs.
/// All part payloads are decoded in memory under explicit cumulative and per-part budgets.
/// </summary>
public static class MimeMailReader
{
    private static readonly Regex EncodedWord = new(
        @"=\?([A-Za-z0-9_.\-]+)\?([bBqQ])\?([^?]*)\?=",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    static MimeMailReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MimeMailMessage Parse(
        ReadOnlyMemory<byte> data, MimeMailLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new MimeMailLimits();
        if (data.Length is < 1 || data.Length > limits.MaxInputBytes)
            throw new MimeMailException("EML exceeds its 25 MiB input limit.");
        return new Reader(limits, cancellationToken).Parse(data);
    }

    private sealed class Reader(MimeMailLimits limits, CancellationToken token)
    {
        private int _parts;
        private int _delimiters;
        private int _attachmentCount;
        private long _decodedTotal;
        private readonly List<MimeMailPart> _leaves = [];
        private string? _html;
        private string? _text;

        public MimeMailMessage Parse(ReadOnlyMemory<byte> data)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (headers, start) = ReadHeaders(data, limits, token);
                string from = Header(headers, "from");
                string date = Header(headers, "date");
                if (string.IsNullOrWhiteSpace(from) || !from.Contains('@') || !ValidMailDate(date))
                    throw new MimeMailException("EML requires valid sender and date headers.");

                Traverse(data[start..], headers, 0);
                if (_html is null && _text is null)
                    throw new MimeMailException("No supported plain-text or HTML body.");
                return new MimeMailMessage(
                    from, Header(headers, "to"), Header(headers, "cc"),
                    Header(headers, "subject"), date, _html, _text, _leaves.AsReadOnly());
            }
            catch (Exception e) when (e is FormatException or ArgumentException or DecoderFallbackException
                or OverflowException or RegexMatchTimeoutException)
            {
                throw new MimeMailException("Malformed or unsupported MIME mail.", e);
            }
        }

        private void Traverse(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
            int depth, bool preferLatestAlternative = false)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= limits.MaxDepth || ++_parts > limits.MaxParts)
                throw new MimeMailException("MIME structure limit exceeded.");
            var (media, parameters) = Media(Header(headers, "content-type", "text/plain"));
            if (media is "multipart/encrypted" or "application/pkcs7-mime" or
                "application/x-pkcs7-mime" or "application/pgp-encrypted")
                throw new MimeMailException("Encrypted MIME content is unsupported.");

            if (media.StartsWith("multipart/", StringComparison.Ordinal))
            {
                if (!parameters.TryGetValue("boundary", out string? boundary) ||
                    boundary.Length is < 1 or > 200 || boundary.Any(ch => ch < 32 || ch > 126))
                    throw new MimeMailException("Multipart boundary missing or invalid.");
                bool any = false;
                foreach (var child in SplitMultipart(body, boundary))
                {
                    token.ThrowIfCancellationRequested();
                    any = true;
                    var (fields, offset) = ReadHeaders(child, limits, token);
                    Traverse(child[offset..], fields, depth + 1,
                        preferLatestAlternative || media == "multipart/alternative");
                }
                if (!any) throw new MimeMailException("Empty multipart message.");
                return;
            }

            string transfer = Header(headers, "content-transfer-encoding", "7bit").Trim().ToLowerInvariant();
            var disposition = Media(Header(headers, "content-disposition", ""));
            string? name = disposition.Parameters.GetValueOrDefault("filename") ??
                           parameters.GetValueOrDefault("name");
            string? cid = Header(headers, "content-id").Trim().Trim('<', '>');
            if (cid.Length == 0) cid = null;

            bool inlineImage = media is "image/png" or "image/jpeg" or "image/gif" or "image/webp" &&
                               cid is not null;
            bool message = media == "message/rfc822";
            bool attachment = disposition.Type == "attachment" || message ||
                              name is not null && !inlineImage;
            if (attachment && ++_attachmentCount > limits.MaxAttachmentCount)
                throw new MimeMailException("Too many MIME attachments.");

            int maxPart = inlineImage ? limits.MaxInlineImageBytes :
                attachment ? limits.MaxAttachmentBytes : limits.MaxBodyBytes;
            // Unsupported media can still be attachments, but never become renderable HTML.
            byte[] decoded = DecodeTransfer(body.Span, transfer, maxPart, token);
            _decodedTotal += decoded.LongLength;
            if (_decodedTotal > limits.MaxTotalDecodedBytes)
                throw new MimeMailException("Decoded MIME resources exceed 25 MiB.");

            _leaves.Add(new MimeMailPart(media, cid, name,
                attachment, message, decoded));

            if (!attachment && media is "text/plain" or "text/html")
            {
                string charset = parameters.GetValueOrDefault("charset") ?? "us-ascii";
                string value = DecodeText(decoded, charset);
                if (Encoding.UTF8.GetByteCount(value) > limits.MaxBodyBytes)
                    throw new MimeMailException("Decoded mail body exceeds 5 MiB.");
                if (media == "text/html")
                {
                    if (preferLatestAlternative || _html is null) _html = value;
                }
                else if (preferLatestAlternative || _text is null) _text = value;
            }
        }

        private List<ReadOnlyMemory<byte>> SplitMultipart(ReadOnlyMemory<byte> body, string boundary)
        {
            byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);
            var parts = new List<ReadOnlyMemory<byte>>();
            int partStart = -1;
            bool ended = false;
            for (int position = 0; position < body.Length;)
            {
                token.ThrowIfCancellationRequested();
                int lineStart = position;
                var span = body.Span;
                while (position < span.Length && span[position] != '\n') position++;
                int end = position;
                if (position < span.Length) position++;
                if (end > lineStart && span[end - 1] == '\r') end--;
                var line = span[lineStart..end];
                if (line.Length < marker.Length || !line[..marker.Length].SequenceEqual(marker))
                    continue;
                // Whitespace after a delimiter is transport padding (RFC 2046).
                var tail = line[marker.Length..].TrimEnd(" \t"u8);
                bool close = tail.SequenceEqual("--"u8);
                if (!close && tail.Length != 0) continue;
                if (++_delimiters > limits.MaxDelimiters)
                    throw new MimeMailException("MIME delimiter limit exceeded.");

                if (partStart >= 0)
                {
                    int endOfPart = lineStart;
                    while (endOfPart > partStart && body.Span[endOfPart - 1] is (byte)'\r' or (byte)'\n')
                        endOfPart--;
                    parts.Add(body[partStart..endOfPart]);
                }
                if (close) { ended = true; break; }
                partStart = position;
            }
            // A message cut off before its final delimiter keeps its last part, as mail clients read it.
            if (!ended && partStart >= 0 && partStart < body.Length)
            {
                int endOfPart = body.Length;
                while (endOfPart > partStart && body.Span[endOfPart - 1] is (byte)'\r' or (byte)'\n') endOfPart--;
                parts.Add(body[partStart..endOfPart]);
            }
            if (parts.Count == 0) throw new MimeMailException("Multipart body without parts.");
            return parts;
        }
    }

    private static (Dictionary<string, string> Headers, int BodyOffset) ReadHeaders(
        ReadOnlyMemory<byte> data, MimeMailLimits limits, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        int count = 0;
        for (int position = 0; position < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            int begin = position;
            while (position < data.Length && data.Span[position] != '\n') position++;
            int end = position;
            if (position < data.Length) position++;
            if (end > begin && data.Span[end - 1] == '\r') end--;
            if (end == begin) return (headers, position);
            var line = data.Span[begin..end];
            if (line[0] is (byte)' ' or (byte)'\t')
            {
                if (current is null || !headers.TryGetValue(current, out var prev))
                    throw new MimeMailException("Invalid folded MIME header.");
                string fragment = Encoding.Latin1.GetString(line).Trim();
                if (prev.Length + fragment.Length + 1 > limits.MaxHeaderValueBytes)
                    throw new MimeMailException("Header line is too long.");
                headers[current] = prev + " " + fragment;
                continue;
            }
            int colon = line.IndexOf((byte)':');
            if (colon is <= 0 or > 128 || ++count > limits.MaxHeaders)
                throw new MimeMailException("Invalid MIME header line/count.");
            current = Encoding.ASCII.GetString(line[..colon]).Trim().ToLowerInvariant();
            if (current.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
                throw new MimeMailException("Invalid MIME header name.");
            if (line.Length - colon - 1 > limits.MaxHeaderValueBytes)
                throw new MimeMailException("MIME header value is too long.");
            string value = Encoding.Latin1.GetString(line[(colon + 1)..]).Trim();
            if (value.IndexOf('\0') >= 0)
                throw new MimeMailException("Invalid control character in mail header.");
            // Duplicate informational headers can occur, but the first authoritative
            // Content-Type/Transfer-Encoding prevents header-smuggling ambiguity.
            if (headers.ContainsKey(current) && current is
                "content-type" or "content-transfer-encoding" or "content-disposition")
                throw new MimeMailException("Duplicate MIME structural header.");
            headers.TryAdd(current, value);
        }
        throw new MimeMailException("MIME header/body separator is missing.");
    }

    private static bool ValidMailDate(string raw)
    {
        // RFC 5322 numeric zones have no colon. DateTimeOffset's accepted forms
        // vary by culture; normalize +HHMM / -HHMM to a portable +HH:MM first.
        string date = raw.Trim();
        Match match = Regex.Match(date, @"([+-])(\d{2})(\d{2})\s*$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        if (match.Success)
            date = date[..match.Index] + match.Groups[1].Value +
                   match.Groups[2].Value + ":" + match.Groups[3].Value;
        string[] accepted =
        [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm zzz",
            "dd MMM yyyy HH:mm zzz"
        ];
        return DateTimeOffset.TryParseExact(date, accepted, CultureInfo.GetCultureInfo("en-US"),
                   DateTimeStyles.AllowWhiteSpaces, out _) ||
               DateTimeOffset.TryParse(date, CultureInfo.GetCultureInfo("en-US"),
                   DateTimeStyles.AllowWhiteSpaces, out _);
    }

    private static string Header(IReadOnlyDictionary<string, string> headers, string name, string fallback = "") =>
        headers.TryGetValue(name, out string? raw) ? DecodeEncodedWords(raw) : fallback;

    private static string DecodeEncodedWords(string value)
    {
        var matches = EncodedWord.Matches(value);
        if (matches.Count == 0) return value;
        var result = new StringBuilder(value.Length);
        int end = 0;
        bool previousEncoded = false;
        foreach (Match match in matches)
        {
            string between = value[end..match.Index];
            if (!previousEncoded || !string.IsNullOrWhiteSpace(between))
                result.Append(between);
            // RFC 2047 removes linear whitespace *between* adjacent encoded words.
            byte[] content = match.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(match.Groups[3].Value)
                : DecodeQuotedPrintable(Encoding.ASCII.GetBytes(match.Groups[3].Value.Replace('_', ' ')),
                    32768, CancellationToken.None);
            result.Append(DecodeText(content, match.Groups[1].Value));
            end = match.Index + match.Length;
            previousEncoded = true;
        }
        result.Append(value.AsSpan(end));
        return result.ToString();
    }

    private static string DecodeText(byte[] data, string charset)
    {
        try
        {
            var strict = Encoding.GetEncoding(charset.Trim().Trim('"'),
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return strict.GetString(data);
        }
        catch (ArgumentException e)
        {
            throw new MimeMailException("Unknown mail character set.", e);
        }
    }

    private static (string Type, Dictionary<string, string> Parameters) Media(string header)
    {
        string[] parts = SplitSemicolons(header);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pieces = new Dictionary<string, SortedDictionary<int, (string Value, bool Extended)>>(
            StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < parts.Length; i++)
        {
            int equal = parts[i].IndexOf('=');
            if (equal < 1) continue;
            string key = parts[i][..equal].Trim().ToLowerInvariant();
            string value = Unquote(parts[i][(equal + 1)..].Trim());
            if (key.Length > 128 || value.Length > 32768) throw new MimeMailException("MIME parameter limit exceeded.");
            int star = key.IndexOf('*');
            if (star < 0) { parameters.TryAdd(key, value); continue; }
            string baseKey = key[..star];
            string suffix = key[(star + 1)..];
            if (suffix.Length == 0)
            {
                parameters[baseKey] = Decode2231(value);
                continue;
            }
            bool encoded = suffix.EndsWith('*');
            string indexText = encoded ? suffix[..^1] : suffix;
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                index is < 0 or > 100) continue;
            if (!pieces.TryGetValue(baseKey, out var sequences))
                pieces.Add(baseKey, sequences = new());
            if (!sequences.TryAdd(index, (value, encoded)))
                throw new MimeMailException("Duplicate RFC 2231 parameter segment.");
        }
        foreach (var (key, segments) in pieces)
        {
            if (parameters.ContainsKey(key) || !segments.ContainsKey(0)) continue;
            parameters[key] = Decode2231Segments(segments);
        }
        return (parts[0].Trim().ToLowerInvariant(), parameters);
    }

    private static string Decode2231Segments(
        SortedDictionary<int, (string Value, bool Extended)> segments)
    {
        var bytes = new List<byte>();
        string charset = "us-ascii";
        int expected = 0;
        foreach (var (number, segment) in segments)
        {
            if (number != expected++)
                throw new MimeMailException("Missing RFC 2231 parameter segment.");

            string fragment = segment.Value;
            if (number == 0 && segment.Extended)
            {
                int first = fragment.IndexOf('\'');
                int second = first < 0 ? -1 : fragment.IndexOf('\'', first + 1);
                if (second >= 0)
                {
                    charset = fragment[..first];
                    fragment = fragment[(second + 1)..];
                }
            }

            if (segment.Extended)
            {
                bytes.AddRange(PercentDecode(fragment));
            }
            else
            {
                foreach (char ch in fragment)
                {
                    if (ch > 255)
                        throw new MimeMailException("Invalid MIME parameter character.");
                    bytes.Add((byte)ch);
                }
            }
        }
        return DecodeText(bytes.ToArray(), charset);
    }

    private static byte[] PercentDecode(string encoded)
    {
        var bytes = new List<byte>(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out byte decoded))
            {
                bytes.Add(decoded);
                i += 2;
            }
            else if (encoded[i] < 128)
            {
                bytes.Add((byte)encoded[i]);
            }
            else throw new MimeMailException("Invalid extended MIME parameter.");
        }
        return bytes.ToArray();
    }

    private static string Decode2231(string value)
    {
        int first = value.IndexOf('\'');
        int second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        string charset = second >= 0 ? value[..first] : "us-ascii";
        string encoded = second >= 0 ? value[(second + 1)..] : value;
        return DecodeText(PercentDecode(encoded), charset);
    }

    private static string[] SplitSemicolons(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, escaped = false;
        foreach (char c in value)
        {
            if (escaped) { current.Append(c); escaped = false; continue; }
            if (c == '\\' && quoted) { escaped = true; current.Append(c); continue; }
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var result = new StringBuilder();
        for (int i = 1; i < value.Length - 1; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length - 1) i++;
            result.Append(value[i]);
        }
        return result.ToString();
    }

    private static byte[] DecodeTransfer(ReadOnlySpan<byte> input, string encoding, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (encoding)
        {
            case "base64":
            {
                var builder = new StringBuilder(input.Length);
                foreach (byte b in input)
                {
                    ct.ThrowIfCancellationRequested();
                    if (b is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t') continue;
                    if (b > 127) throw new MimeMailException("Invalid base64 MIME data.");
                    builder.Append((char)b);
                    if (builder.Length > (long)(max + 2) * 4 / 3 + 8)
                        throw new MimeMailException("Decoded MIME part exceeds its limit.");
                }
                byte[] decoded = Convert.FromBase64String(builder.ToString());
                if (decoded.Length > max) throw new MimeMailException("Decoded MIME part exceeds its limit.");
                return decoded;
            }
            case "quoted-printable": return DecodeQuotedPrintable(input, max, ct);
            case "7bit":
                foreach (byte b in input)
                {
                    ct.ThrowIfCancellationRequested();
                    if (b > 127) throw new MimeMailException("7bit MIME contains non-ASCII bytes.");
                }
                goto case "8bit";
            case "8bit":
                if (input.Length > max) throw new MimeMailException("Decoded MIME part exceeds its limit.");
                return input.ToArray();
            default: throw new MimeMailException("Unsupported MIME transfer encoding.");
        }
    }

    private static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> input, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        for (int i = 0; i < input.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (input[i] == '=')
            {
                if (i + 1 >= input.Length) throw new MimeMailException("Incomplete quoted-printable escape.");
                if (input[i + 1] == '\n') { i++; continue; }
                if (input[i + 1] == '\r' && i + 2 < input.Length && input[i + 2] == '\n')
                { i += 2; continue; }
                if (i + 2 < input.Length &&
                    byte.TryParse(Encoding.ASCII.GetString(input.Slice(i + 1, 2)),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte decoded))
                {
                    buffer.WriteByte(decoded);
                    if (buffer.Length > max) throw new MimeMailException("Decoded MIME part exceeds its limit.");
                    i += 2;
                    continue;
                }
                throw new MimeMailException("Malformed quoted-printable data.");
            }
            buffer.WriteByte(input[i]);
            if (buffer.Length > max) throw new MimeMailException("Decoded MIME part exceeds its limit.");
        }
        return buffer.ToArray();
    }
}
