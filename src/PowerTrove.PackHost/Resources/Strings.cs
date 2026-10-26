using System.Globalization;
using System.Resources;
namespace PowerTrove.PackHost.Resources;
internal static class Strings
{
    private static readonly ResourceManager Manager = new("PowerTrove.PackHost.Resources.Strings", typeof(Strings).Assembly);
    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string Busy => Get(nameof(Busy));
    public static string InvalidRequest => Get(nameof(InvalidRequest));
    public static string UnknownAction => Get(nameof(UnknownAction));
    public static string Failed => Get(nameof(Failed));
}
