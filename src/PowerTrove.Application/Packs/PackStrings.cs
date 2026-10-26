using System.Globalization;
using System.Resources;

namespace PowerTrove.Application.Packs;

internal static class PackStrings
{
    private static readonly ResourceManager Manager = new("PowerTrove.Application.Resources.Strings", typeof(PackStrings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
    public static string Stopped => Get("PackWorkerStopped");
    public static string OutputFailed => Get("PackOutputFailed");
    public static string InvalidSelection => Get("PackInvalidSelection");
}
