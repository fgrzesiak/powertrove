using System.Globalization;
using System.Resources;

namespace ContextBar.Infrastructure.Resources;

internal static class Strings
{
    private static readonly ResourceManager ResourceManager = new("ContextBar.Infrastructure.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) => ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    public static string FileAccessDenied => Get(nameof(FileAccessDenied));
    public static string FileReadFailed => Get(nameof(FileReadFailed));
    public static string FileFormatNotRecognized(string format) => string.Format(CultureInfo.CurrentUICulture, Get("FileFormatNotRecognized"), format);
    public static string FileFormatMismatch(string format) => string.Format(CultureInfo.CurrentUICulture, Get("FileFormatMismatch"), format);

    public static string ImageDecodeFailed => Get(nameof(ImageDecodeFailed));
    public static string ImageEncodeFailed => Get(nameof(ImageEncodeFailed));
    public static string ImageUnsupportedFormat => Get(nameof(ImageUnsupportedFormat));
}
