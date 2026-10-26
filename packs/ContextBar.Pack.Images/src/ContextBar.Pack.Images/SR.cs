using System.Globalization;
using System.Resources;

namespace ContextBar.Pack.Images;

internal static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("ContextBar.Pack.Images.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }
}
