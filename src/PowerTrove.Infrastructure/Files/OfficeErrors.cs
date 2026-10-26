using System.Globalization;
using System.Resources;
using PowerTrove.Abstractions.Office;

namespace PowerTrove.Infrastructure.Files;

internal static class OfficeErrors
{
    private static readonly ResourceManager Manager = new("PowerTrove.Infrastructure.Resources.Strings", typeof(OfficeErrors).Assembly);
    public static string Message(OfficeFailure failure) => Manager.GetString("Office" + failure, CultureInfo.CurrentUICulture)
        ?? Manager.GetString("OfficeConversionFailed", CultureInfo.CurrentUICulture)!;
}
