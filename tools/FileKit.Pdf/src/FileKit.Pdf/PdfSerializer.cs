using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

/// <summary>Writes PDF objects in canonical syntax.</summary>
internal static class PdfSerializer
{
    public static void Write(Stream output, PdfObject value, int depth = 0)
    {
        if (depth > 512) throw new PdfLimitExceededException("Object nesting is too deep to write.");

        switch (value)
        {
            case PdfNull:
                WriteAscii(output, "null");
                break;
            case PdfBoolean b:
                WriteAscii(output, b.Value ? "true" : "false");
                break;
            case PdfInteger i:
                WriteAscii(output, i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal r:
                WriteAscii(output, FormatReal(r.Value));
                break;
            case PdfName n:
                WriteName(output, n.Value);
                break;
            case PdfString s:
                WriteString(output, s);
                break;
            case PdfReference reference:
                WriteAscii(output, $"{reference.ObjectNumber} {reference.Generation} R");
                break;
            case PdfArray array:
                output.WriteByte((byte)'[');
                for (int k = 0; k < array.Count; k++)
                {
                    if (k > 0) output.WriteByte((byte)' ');
                    Write(output, array[k], depth + 1);
                }
                output.WriteByte((byte)']');
                break;
            case PdfDictionary dict:
                WriteDictionary(output, dict, depth);
                break;
            case PdfStream stream:
                stream.Dictionary["Length"] = new PdfInteger(stream.Data.Length);
                WriteDictionary(output, stream.Dictionary, depth);
                WriteAscii(output, "\nstream\n");
                output.Write(stream.Data, 0, stream.Data.Length);
                WriteAscii(output, "\nendstream");
                break;
            default:
                throw new PdfFormatException("Unknown object type.");
        }
    }

    public static string FormatReal(double value)
    {
        if (!double.IsFinite(value)) return "0";
        string text = value.ToString("0.##########", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static void WriteAscii(Stream output, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void WriteDictionary(Stream output, PdfDictionary dict, int depth)
    {
        WriteAscii(output, "<<");
        foreach (var (key, entry) in dict.Entries)
        {
            WriteName(output, key);
            output.WriteByte((byte)' ');
            Write(output, entry, depth + 1);
        }
        WriteAscii(output, ">>");
    }

    private static void WriteName(Stream output, string name)
    {
        output.WriteByte((byte)'/');
        foreach (byte b in Encoding.Latin1.GetBytes(name))
        {
            if (b < 0x21 || b > 0x7E || b == '#' || PdfParser.IsDelimiter(b))
                WriteAscii(output, "#" + b.ToString("X2", CultureInfo.InvariantCulture));
            else
                output.WriteByte(b);
        }
    }

    private static void WriteString(Stream output, PdfString value)
    {
        bool printable = !value.IsHex && value.Value.All(b => b >= 0x20 && b < 0x7F);
        if (!printable)
        {
            output.WriteByte((byte)'<');
            WriteAscii(output, Convert.ToHexString(value.Value));
            output.WriteByte((byte)'>');
            return;
        }

        output.WriteByte((byte)'(');
        foreach (byte b in value.Value)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') output.WriteByte((byte)'\\');
            output.WriteByte(b);
        }
        output.WriteByte((byte)')');
    }
}

/// <summary>Counts bytes written so cross-reference offsets can be recorded.</summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    public CountingStream(Stream inner) => _inner = inner;

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        BytesWritten++;
    }
}
