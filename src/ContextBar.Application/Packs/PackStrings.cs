using System.Globalization;
using System.Resources;

namespace ContextBar.Application.Packs;

internal static class PackStrings
{
    private static readonly ResourceManager Manager = new("ContextBar.Application.Resources.Strings", typeof(PackStrings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);
    public static string Stopped => Get("PackWorkerStopped");
    public static string OutputFailed => Get("PackOutputFailed");
    public static string InvalidSelection => Get("PackInvalidSelection");
}
