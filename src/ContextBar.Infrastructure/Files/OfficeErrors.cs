using System.Globalization;
using System.Resources;
using ContextBar.Abstractions.Office;

namespace ContextBar.Infrastructure.Files;

internal static class OfficeErrors
{
    private static readonly ResourceManager Manager = new("ContextBar.Infrastructure.Resources.Strings", typeof(OfficeErrors).Assembly);
    public static string Message(OfficeFailure failure) => Manager.GetString("Office" + failure, CultureInfo.CurrentUICulture)
        ?? Manager.GetString("OfficeConversionFailed", CultureInfo.CurrentUICulture)!;
}
