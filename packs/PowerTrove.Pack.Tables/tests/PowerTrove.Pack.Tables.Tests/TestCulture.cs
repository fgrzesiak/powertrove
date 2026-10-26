using System.Globalization;
using System.Runtime.CompilerServices;

namespace PowerTrove.Pack.Tables.Tests;

/// <summary>Runs the tests with English resources, independent of the machine's display language.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseInvariantEnglish()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
