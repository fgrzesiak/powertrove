using System.Globalization;
using System.Resources;
namespace PowerTrove.Pack.Images.Windows.Resources;
public static class Strings
{
    private static readonly ResourceManager Manager = new("PowerTrove.Pack.Images.Windows.Resources.Strings", typeof(Strings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string ImageDecodeFailed => Get(nameof(ImageDecodeFailed));
    public static string ImageEncodeFailed => Get(nameof(ImageEncodeFailed));
    public static string ImageUnsupportedFormat => Get(nameof(ImageUnsupportedFormat));
    public static string ImagingCannotPreserveMetadata => Get(nameof(ImagingCannotPreserveMetadata));
}
