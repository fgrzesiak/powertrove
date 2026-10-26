using System.Globalization;
using System.Resources;

namespace ContextBar.Pack.Files;

internal static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("ContextBar.Pack.Files.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }

    public static string Format(string name, params object?[] values) => string.Format(CultureInfo.CurrentCulture, GetString(name), values);
    public static string Count(string name, int count) => Format(name + (count == 1 ? "_One" : "_Other"), count);
}
