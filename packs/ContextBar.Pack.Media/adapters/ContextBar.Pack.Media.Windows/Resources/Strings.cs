using System.Globalization;
using System.Resources;
namespace ContextBar.Pack.Media.Windows.Resources;
public static class Strings
{
    private static readonly ResourceManager Manager = new("ContextBar.Pack.Media.Windows.Resources.Strings", typeof(Strings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string MediaCodecNotFound => Get(nameof(MediaCodecNotFound));
    public static string MediaConversionFailed => Get(nameof(MediaConversionFailed));
    public static string MediaConversionNotSupported => Get(nameof(MediaConversionNotSupported));
    public static string MediaInvalidProfile => Get(nameof(MediaInvalidProfile));
    public static string MediaNoVideoTrack => Get(nameof(MediaNoVideoTrack));
    public static string MediaOpenFailed => Get(nameof(MediaOpenFailed));
    public static string MediaUnsupportedAudio => Get(nameof(MediaUnsupportedAudio));
    public static string MediaUnsupportedOperation => Get(nameof(MediaUnsupportedOperation));
}
