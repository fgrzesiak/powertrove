using System.Globalization;
using System.Resources;

namespace PowerTrove.Pack.Pdf;

public static class SR
{
    private static readonly ResourceManager ResourceManager = new ResourceManager("PowerTrove.Pack.Pdf.Resources.Strings", typeof(SR).Assembly);

    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
    }
}
