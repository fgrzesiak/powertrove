using System.Globalization;
using System.Resources;
namespace ContextBar.Pack.Pdf.Windows.Resources;
public static class Strings
{
    private static readonly ResourceManager Manager = new("ContextBar.Pack.Pdf.Windows.Resources.Strings", typeof(Strings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string ImageConversionFailed => Get(nameof(ImageConversionFailed));
    public static string PdfOpenFailed => Get(nameof(PdfOpenFailed));
    public static string PdfPasswordProtected => Get(nameof(PdfPasswordProtected));
}
