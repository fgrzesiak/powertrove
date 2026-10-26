using System.Globalization;
using System.Runtime.CompilerServices;

namespace ContextBar.Abstractions.Tests;

public static class TestCulture
{
    [ModuleInitializer]
    public static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
    }
}
