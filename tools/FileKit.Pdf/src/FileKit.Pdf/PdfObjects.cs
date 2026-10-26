using System.Globalization;
using System.Text;

namespace FileKit.Pdf;

/// <summary>Base type of the PDF object model (ISO 32000-1, section 7.3).</summary>
public abstract class PdfObject
{
}

public sealed class PdfNull : PdfObject
{
    public static PdfNull Instance { get; } = new();
    private PdfNull() { }
    public override string ToString() => "null";
}

public sealed class PdfBoolean : PdfObject
{
    public static PdfBoolean True { get; } = new(true);
    public static PdfBoolean False { get; } = new(false);
    private PdfBoolean(bool value) => Value = value;
    public bool Value { get; }
    public static PdfBoolean Of(bool value) => value ? True : False;
    public override string ToString() => Value ? "true" : "false";
}

public sealed class PdfInteger : PdfObject
{
    public PdfInteger(long value) => Value = value;
    public long Value { get; }
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class PdfReal : PdfObject
{
    public PdfReal(double value) => Value = value;
    public double Value { get; }
    public override string ToString() => Value.ToString("0.#####", CultureInfo.InvariantCulture);
}

public sealed class PdfString : PdfObject
{
    public PdfString(byte[] value, bool isHex = false)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsHex = isHex;
    }

    public byte[] Value { get; }
    public bool IsHex { get; }

    public static PdfString FromText(string text)
    {
        // PDFDocEncoding covers ASCII; anything else is written as UTF-16BE with BOM.
        bool ascii = text.All(c => c >= 0x20 && c < 0x7F);
        if (ascii) return new PdfString(Encoding.ASCII.GetBytes(text));
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
        return new PdfString(bytes, isHex: true);
    }
}

public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    public PdfName(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public bool Equals(PdfName? other) => other is not null && other.Value == Value;
    public override bool Equals(object? obj) => obj is PdfName other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => "/" + Value;
}

public sealed class PdfArray : PdfObject
{
    public PdfArray() => Items = new List<PdfObject>();
    public PdfArray(IEnumerable<PdfObject> items) => Items = new List<PdfObject>(items);
    public List<PdfObject> Items { get; }
    public int Count => Items.Count;
    public PdfObject this[int index] => Items[index];
}

public sealed class PdfDictionary : PdfObject
{
    // Insertion order is preserved so rewritten objects stay diff-friendly.
    private readonly List<KeyValuePair<string, PdfObject>> _entries = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public int Count => _entries.Count;
    public IEnumerable<KeyValuePair<string, PdfObject>> Entries => _entries;

    public PdfObject? this[string key]
    {
        get => _index.TryGetValue(key, out int i) ? _entries[i].Value : null;
        set
        {
            if (value is null)
            {
                Remove(key);
                return;
            }
            if (_index.TryGetValue(key, out int i))
                _entries[i] = new(key, value);
            else
            {
                _index[key] = _entries.Count;
                _entries.Add(new(key, value));
            }
        }
    }

    public bool ContainsKey(string key) => _index.ContainsKey(key);

    public void Remove(string key)
    {
        if (!_index.TryGetValue(key, out int i)) return;
        _entries.RemoveAt(i);
        _index.Clear();
        for (int k = 0; k < _entries.Count; k++) _index[_entries[k].Key] = k;
    }

    public string? GetName(string key) => (this[key] as PdfName)?.Value;
}

public sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary, byte[] data)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public PdfDictionary Dictionary { get; }

    /// <summary>The raw (still encoded) stream bytes.</summary>
    public byte[] Data { get; }
}

public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    public PdfReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }
    public int Generation { get; }
    public bool Equals(PdfReference? other) => other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation;
    public override bool Equals(object? obj) => obj is PdfReference other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);
    public override string ToString() => $"{ObjectNumber} {Generation} R";
}
