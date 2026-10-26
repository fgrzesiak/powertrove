using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;

namespace PowerTrove.Pack.Images.Magick;

/// <summary>Normalizes orientation/dimension facts without reserializing private EXIF fields.</summary>
internal static class JpegMetadata
{
    public static byte[] Normalize(byte marker, byte[] original, int width, int height, bool swapAxes)
    {
        byte[] data = (byte[])original.Clone();
        if (marker == 0xE0 && swapAxes && data.Length >= 12 && data.AsSpan().StartsWith("JFIF\0"u8))
        { (data[8], data[10]) = (data[10], data[8]); (data[9], data[11]) = (data[11], data[9]); }
        if (marker != 0xE1) return data;
        if (data.AsSpan().StartsWith("http://ns.adobe.com/xmp/extension/\0"u8)) throw Unsupported();
        if (data.AsSpan().StartsWith("Exif\0\0"u8))
        {
            if (data.Length < 14 || !(data.AsSpan(6, 4).SequenceEqual("II*\0"u8) || data.AsSpan(6, 4).SequenceEqual("MM\0*"u8))) throw Unsupported();
            bool little = data[6] == 'I'; int length = data.Length - 6; var visited = new HashSet<int>();
            int Read16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6 + p)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6 + p));
            uint Read32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6 + p)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6 + p));
            void Set(int p, int type, uint value)
            {
                if (type == 3)
                { if (little) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6 + p), checked((ushort)value)); else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6 + p), checked((ushort)value)); }
                else if (type == 4)
                { if (little) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6 + p), value); else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(6 + p), value); }
            }
            void Visit(uint offset, int depth)
            {
                if (offset > length - 2 || depth > 4 || !visited.Add((int)offset)) throw Unsupported();
                int count = Read16((int)offset);
                if (count > 4096 || offset + 2L + count * 12L + 4 > length) throw Unsupported();
                for (int i = 0; i < count; i++)
                {
                    int p = (int)offset + 2 + i * 12, tag = Read16(p), type = Read16(p + 2); uint n = Read32(p + 4);
                    if (n == 1 && type is 3 or 4)
                    {
                        if (tag == 0x112) Set(p + 8, type, 1);
                        else if (tag is 256 or 0xA002) Set(p + 8, type, (uint)width);
                        else if (tag is 257 or 0xA003) Set(p + 8, type, (uint)height);
                        else if (tag == 0x8769 && type == 4) Visit(Read32(p + 8), depth + 1);
                    }
                }
            }
            Visit(Read32(4), 0); return data;
        }
        ReadOnlySpan<byte> prefix = "http://ns.adobe.com/xap/1.0/\0"u8;
        if (data.AsSpan().StartsWith(prefix))
        {
            try
            {
                using var stream = new MemoryStream(data, prefix.Length, data.Length - prefix.Length);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                string? Replacement(XName name) => (name.NamespaceName, name.LocalName) switch {
                    ("http://ns.adobe.com/tiff/1.0/", "Orientation") => "1",
                    ("http://ns.adobe.com/tiff/1.0/", "ImageWidth") or ("http://ns.adobe.com/exif/1.0/", "PixelXDimension") => width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ("http://ns.adobe.com/tiff/1.0/", "ImageLength") or ("http://ns.adobe.com/exif/1.0/", "PixelYDimension") => height.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
                foreach (var a in doc.Descendants().Attributes()) if (Replacement(a.Name) is string value) a.Value = value;
                foreach (var e in doc.Descendants().Where(e => !e.HasElements)) if (Replacement(e.Name) is string value) e.Value = value;
                byte[] xml = Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
                if (xml.Length + prefix.Length > 65533) throw Unsupported();
                return prefix.ToArray().Concat(xml).ToArray();
            }
            catch (XmlException ex) { throw new FileProcessingException(ExtendedImageMessages.Get("JpegUnsupported"), ex); }
        }
        return data;
    }
    private static FileProcessingException Unsupported() => new(ExtendedImageMessages.Get("JpegUnsupported"));
}
