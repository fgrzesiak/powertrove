using System.Text;
using System.Xml;
using System.Xml.Linq;
using PowerTrove.Abstractions.Files;
using PowerTrove.Pack.Images;
using ImageMagick;

namespace PowerTrove.Pack.Images.Magick;

internal static class GpsProfiles
{
    public static bool Remove(MagickImage image)
    {
        bool changed = false;
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            var tags = exif.Values.Select(v => v.Tag).Where(t => t.ToString().StartsWith("GPS", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var tag in tags) changed |= exif.RemoveValue(tag);
            if (tags.Length > 0) image.SetProfile(exif);
        }
        var xmp = image.GetXmpProfile();
        if (xmp is not null)
        {
            try
            {
                byte[] data = xmp.ToByteArray();
                if (data.Length > 2 * 1024 * 1024) throw new XmlException();
                using var source = new MemoryStream(data);
                using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                bool IsGps(XName name) => name.LocalName.StartsWith("GPS", StringComparison.OrdinalIgnoreCase) &&
                    (name.NamespaceName == "http://ns.adobe.com/exif/1.0/" || name.NamespaceName == "http://ns.adobe.com/exif/1.0/aux/");
                var attributes = doc.Descendants().Attributes().Where(a => IsGps(a.Name)).ToArray();
                var elements = doc.Descendants().Where(e => IsGps(e.Name)).ToArray();
                foreach (var a in attributes) a.Remove(); foreach (var e in elements) e.Remove();
                if (attributes.Length + elements.Length > 0)
                {
                    image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting)))); changed = true;
                }
            }
            catch (Exception ex) when (ex is XmlException or ArgumentException)
            { throw new FileProcessingException(ExtendedImageMessages.Get("GpsFailed"), ex); }
        }
        return changed;
    }
}
