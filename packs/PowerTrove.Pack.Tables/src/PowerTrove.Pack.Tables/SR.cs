using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Tables;

internal static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Pack.Tables.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }

    public static string Format(string name, params object?[] values) => string.Format(CultureInfo.CurrentCulture, GetString(name), values);
}
