using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

/// <summary>
/// Tokenizer and object parser for PDF syntax over an in-memory buffer.
/// Every loop is bounded by the buffer length; nesting is bounded by
/// <see cref="PdfReadLimits.MaxNestingDepth"/>.
/// </summary>
internal sealed class PdfParser
{
    private readonly byte[] _data;
    private readonly PdfReadLimits _limits;
    private readonly Func<PdfReference, long?>? _resolveLength;
    private readonly PdfDecryptor? _decryptor;

    public PdfParser(byte[] data, PdfReadLimits limits, Func<PdfReference, long?>? resolveLength = null, int position = 0, PdfDecryptor? decryptor = null)
    {
        _data = data;
        _limits = limits;
        _resolveLength = resolveLength;
        Position = position;
        _decryptor = decryptor;
    }

    public int CurrentObjectNumber { get; set; }
    public int CurrentGeneration { get; set; }

    public int Position { get; set; }

    public bool AtEnd => Position >= _data.Length;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public void SkipWhitespaceAndComments()
    {
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (Position < _data.Length && _data[Position] != '\n' && _data[Position] != '\r') Position++;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Reads a run of regular characters (a keyword or number) without consuming it.</summary>
    public string PeekKeyword()
    {
        int save = Position;
        string token = ReadKeyword();
        Position = save;
        return token;
    }

    public string ReadKeyword()
    {
        SkipWhitespaceAndComments();
        int start = Position;
        while (Position < _data.Length && IsRegular(_data[Position])) Position++;
        return Encoding.ASCII.GetString(_data, start, Position - start);
    }

    public bool TryReadInteger(out long value)
    {
        int save = Position;
        SkipWhitespaceAndComments();
        int start = Position;
        if (Position < _data.Length && (_data[Position] == '+' || _data[Position] == '-')) Position++;
        int digitsStart = Position;
        while (Position < _data.Length && _data[Position] >= '0' && _data[Position] <= '9') Position++;
        if (Position == digitsStart || Position - digitsStart > 18 || (Position < _data.Length && IsRegular(_data[Position])))
        {
            Position = save;
            value = 0;
            return false;
        }
        value = long.Parse(Encoding.ASCII.GetString(_data, start, Position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Parses "N G obj ... endobj" at the current position.</summary>
    public (int ObjectNumber, int Generation, PdfObject Value) ParseIndirectObject()
    {
        if (!TryReadInteger(out long number) || !TryReadInteger(out long generation))
            throw new PdfFormatException("Expected an indirect object header.");
        if (ReadKeyword() != "obj")
            throw new PdfFormatException("Expected 'obj'.");
        if (number < 0 || number > int.MaxValue || generation < 0 || generation > 65535)
            throw new PdfFormatException("Invalid object number.");

        CurrentObjectNumber = (int)number;
        CurrentGeneration = (int)generation;
        var value = ParseObject(0);
        SkipWhitespaceAndComments();

        if (value is PdfDictionary dict && PeekKeyword() == "stream")
        {
            ReadKeyword();
            value = ReadStream(dict);
        }

        // "endobj" is expected but often missing in damaged files; tolerate its absence.
        int save = Position;
        if (ReadKeyword() != "endobj") Position = save;
        return ((int)number, (int)generation, value);
    }

    public PdfObject ParseObject(int depth)
    {
        if (depth > _limits.MaxNestingDepth)
            throw new PdfLimitExceededException("The document is nested too deeply.");

        SkipWhitespaceAndComments();
        if (Position >= _data.Length)
            throw new PdfFormatException("Unexpected end of data.");

        byte b = _data[Position];
        switch (b)
        {
            case (byte)'/':
                return ReadName();
            case (byte)'(':
                return ReadLiteralString();
            case (byte)'<':
                if (Position + 1 < _data.Length && _data[Position + 1] == '<')
                    return ReadDictionary(depth);
                return ReadHexString();
            case (byte)'[':
                return ReadArray(depth);
            case (byte)'n':
                if (PeekKeyword() == "null")
                {
                    ReadKeyword();
                    return PdfNull.Instance;
                }
                break;
            case (byte)'t':
                if (PeekKeyword() == "true")
                {
                    ReadKeyword();
                    return PdfBoolean.True;
                }
                break;
            case (byte)'f':
                if (PeekKeyword() == "false")
                {
                    ReadKeyword();
                    return PdfBoolean.False;
                }
                break;
        }

        if (b is (byte)'+' or (byte)'-' or (byte)'.' || (b >= '0' && b <= '9'))
        {
            int save = Position;
            if (TryReadInteger(out long first) && TryReadInteger(out long second) && ReadKeyword() == "R")
            {
                if (first < 0 || first > int.MaxValue || second < 0 || second > 65535)
                    throw new PdfFormatException("Invalid indirect reference.");
                return new PdfReference((int)first, (int)second);
            }
            Position = save;
            string numStr = ReadKeyword();
            if (numStr.Contains('.') && double.TryParse(numStr, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d))
                return new PdfReal(d);
            if (long.TryParse(numStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                return new PdfInteger(l);
        }

        throw new PdfFormatException("Unrecognized PDF token.");
    }

    private PdfName ReadName()
    {
        Position++; // '/'
        var bytes = new List<byte>();
        while (Position < _data.Length && IsRegular(_data[Position]))
        {
            byte c = _data[Position];
            if (c == '#' && Position + 2 < _data.Length && IsHex(_data[Position + 1]) && IsHex(_data[Position + 2]))
            {
                bytes.Add((byte)(HexValue(_data[Position + 1]) * 16 + HexValue(_data[Position + 2])));
                Position += 3;
            }
            else
            {
                bytes.Add(c);
                Position++;
            }
        }
        return new PdfName(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    private PdfString ReadLiteralString()
    {
        Position++; // '('
        int open = 1;
        var bytes = new List<byte>();
        while (Position < _data.Length)
        {
            byte c = _data[Position];
            if (c == ')') { open--; if (open == 0) break; }
            if (c == '(') open++;

            if (c == '\\')
            {
                Position++;
                if (Position >= _data.Length) break;
                c = _data[Position];
                if (c == 'n') bytes.Add((byte)'\n');
                else if (c == 'r') bytes.Add((byte)'\r');
                else if (c == 't') bytes.Add((byte)'\t');
                else if (c == 'b') bytes.Add((byte)'\b');
                else if (c == 'f') bytes.Add((byte)'\f');
                else if (c == '(') bytes.Add((byte)'(');
                else if (c == ')') bytes.Add((byte)')');
                else if (c == '\\') bytes.Add((byte)'\\');
                else if (c >= '0' && c <= '7')
                {
                    int octal = c - '0';
                    if (Position + 1 < _data.Length && _data[Position + 1] >= '0' && _data[Position + 1] <= '7')
                    {
                        Position++;
                        octal = (octal << 3) + (_data[Position] - '0');
                        if (Position + 1 < _data.Length && _data[Position + 1] >= '0' && _data[Position + 1] <= '7')
                        {
                            Position++;
                            octal = (octal << 3) + (_data[Position] - '0');
                        }
                    }
                    bytes.Add((byte)octal);
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && Position + 1 < _data.Length && _data[Position + 1] == '\n') Position++;
                }
                else
                {
                    bytes.Add(c);
                }
            }
            else
            {
                bytes.Add(c);
            }
            Position++;
        }
        if (Position < _data.Length) Position++; // skip ')'

        byte[] stringData = bytes.ToArray();
        if (_decryptor != null && CurrentObjectNumber > 0)
            stringData = _decryptor.DecryptString(CurrentObjectNumber, CurrentGeneration, stringData);

        return new PdfString(stringData);
    }

    private PdfString ReadHexString()
    {
        Position++; // '<'
        var bytes = new List<byte>();
        int high = -1;
        while (Position < _data.Length)
        {
            byte c = _data[Position];
            if (_data[Position] == '>')
            {
                Position++;
                if (high >= 0) bytes.Add((byte)(high << 4));
                byte[] stringData = bytes.ToArray();
                if (_decryptor != null && CurrentObjectNumber > 0)
                    stringData = _decryptor.DecryptString(CurrentObjectNumber, CurrentGeneration, stringData);
                return new PdfString(stringData, isHex: true);
            }
            if (IsWhitespace(c)) { Position++; continue; }
            if (!IsHex(c)) throw new PdfFormatException("Invalid hexadecimal string.");
            if (high < 0) high = HexValue(c);
            else
            {
                bytes.Add((byte)((high << 4) | HexValue(c)));
                high = -1;
            }
            Position++;
        }
        throw new PdfFormatException("Unterminated hexadecimal string.");
    }

    private PdfArray ReadArray(int depth)
    {
        Position++; // '['
        var array = new PdfArray();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Position >= _data.Length) throw new PdfFormatException("Unterminated array.");
            if (_data[Position] == ']')
            {
                Position++;
                return array;
            }
            array.Items.Add(ParseObject(depth + 1));
        }
    }

    private PdfDictionary ReadDictionary(int depth)
    {
        Position += 2; // '<<'
        var dict = new PdfDictionary();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Position >= _data.Length) throw new PdfFormatException("Unterminated dictionary.");
            if (_data[Position] == '>' && Position + 1 < _data.Length && _data[Position + 1] == '>')
            {
                Position += 2;
                return dict;
            }
            if (_data[Position] != '/')
            {
                // Skip a stray token rather than failing on slightly malformed dictionaries.
                ParseObject(depth + 1);
                continue;
            }

            var key = ReadName();
            SkipWhitespaceAndComments();
            if (Position < _data.Length && _data[Position] == '>' && Position + 1 < _data.Length && _data[Position + 1] == '>')
            {
                dict[key.Value] = PdfNull.Instance;
                continue;
            }
            var value = ParseObject(depth + 1);
            if (value is not PdfNull) dict[key.Value] = value;
        }
    }

    private PdfStream ReadStream(PdfDictionary dict)
    {
        // After "stream": CRLF or LF (tolerate a lone CR).
        if (Position < _data.Length && _data[Position] == '\r') Position++;
        if (Position < _data.Length && _data[Position] == '\n') Position++;
        int start = Position;

        long? length = dict["Length"] switch
        {
            PdfInteger i => i.Value,
            PdfReference r => _resolveLength?.Invoke(r),
            _ => null
        };

        byte[] streamData;
        if (length is long declared && declared >= 0 && start + declared <= _data.Length && EndstreamFollows(start + (int)declared))
        {
            Position = start + (int)declared;
            SkipWhitespaceAndComments();
            if (ReadKeyword() == "endstream")
            {
                streamData = new byte[(int)declared];
                Array.Copy(_data, start, streamData, 0, (int)declared);
            }
            else
            {
                Position = start;
                streamData = FallbackReadStream(start);
            }
        }
        else
        {
            streamData = FallbackReadStream(start);
        }

        if (_decryptor != null && CurrentObjectNumber > 0 && dict.GetName("Type") != "XRef")
        {
            var filter = dict["Filter"];
            bool isIdentity = false;

            if (filter is PdfArray filterArr)
            {
                for (int i = 0; i < filterArr.Count; i++)
                {
                    if (filterArr[i] is PdfName fn && fn.Value == "Crypt")
                    {
                        var dp = dict["DecodeParms"] as PdfArray;
                        var dpDict = dp != null && i < dp.Count ? dp[i] as PdfDictionary : dict["DecodeParms"] as PdfDictionary;
                        if (dpDict != null && dpDict.GetName("Name") == "Identity") isIdentity = true;
                    }
                }
            }
            else if (filter is PdfName fn && fn.Value == "Crypt")
            {
                var dpDict = dict["DecodeParms"] as PdfDictionary;
                if (dpDict != null && dpDict.GetName("Name") == "Identity") isIdentity = true;
            }

            if (!isIdentity)
            {
                streamData = _decryptor.DecryptStream(CurrentObjectNumber, CurrentGeneration, streamData);
            }
        }

        return new PdfStream(dict, streamData);
    }

    private byte[] FallbackReadStream(int start)
    {
        int end = FindSequence("\nendstream"u8, start);
        if (end < 0) end = FindSequence("\rendstream"u8, start);
        if (end < 0) end = FindSequence("endstream"u8, start);
        if (end < 0) throw new PdfFormatException("Unterminated stream.");

        int size = end - start;
        byte[] fallbackData = new byte[size];
        Array.Copy(_data, start, fallbackData, 0, size);
        Position = end;
        ReadKeyword(); // endstream
        return fallbackData;
    }

    private bool EndstreamFollows(int position)
    {
        int save = Position;
        Position = position;
        SkipWhitespaceAndComments();
        bool isEnd = ReadKeyword() == "endstream";
        Position = save;
        return isEnd;
    }

    private int FindSequence(ReadOnlySpan<byte> needle, int from)
    {
        if (from >= _data.Length) return -1;
        int index = _data.AsSpan(from).IndexOf(needle);
        return index < 0 ? -1 : from + index;
    }

    private static bool IsHex(byte c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexValue(byte c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
