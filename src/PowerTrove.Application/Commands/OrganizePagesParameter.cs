using System.Globalization;

namespace PowerTrove.Application.Commands;

/// <summary>
/// Writes the "Pages" parameter of the PDF pack's organize action from the visual organizer:
/// comma-separated one-based "file:page", plus "@degrees" for a clockwise rotation. The pack parses and validates it.
/// </summary>
public static class OrganizePagesParameter
{
    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360;

    public static string Format(IEnumerable<(int Source, int Page, int Rotation)> pages) => string.Join(", ", pages.Select(p =>
    {
        int rotation = NormalizeRotation(p.Rotation);
        return rotation == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{p.Source + 1}:{p.Page + 1}")
            : string.Create(CultureInfo.InvariantCulture, $"{p.Source + 1}:{p.Page + 1}@{rotation}");
    }));
}
