using System.Security.Cryptography;
using PowerTrove.Abstractions.Office;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Office.Windows;
using FileKit.Pdf;

namespace PowerTrove.Platform.Windows.Tests;

/// <summary>Explicit opt-in only: starts real locally installed Office. Never part of ordinary test runs.</summary>
public sealed class OfficeNativeAcceptanceTests
{
    [Theory]
    [InlineData(OfficeApplication.Word, ".rtf", "{\\rtf1\\ansi Generated conversion fixture.}")]
    [InlineData(OfficeApplication.Excel, ".csv", "Name,Count\r\nGenerated,3\r\n")]
    public async Task GeneratedDocument_ExportsReadablePdfWithoutChangingInput(OfficeApplication application, string extension, string content)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CONTEXTBAR_RUN_OFFICE_ACCEPTANCE") != "1") return;
        var converter = new OfficeComConverter(new OfficeInputValidator());
        if (!converter.IsAvailable(application)) return; // record Blocked in the manual worksheet
        using var dir = new TempDirectory();
        string input = dir.Write("generated" + extension, content), output = dir.File("export.pdf");
        byte[] original = SHA256.HashData(File.ReadAllBytes(input));
        await converter.ConvertToPdfAsync(input, output, CancellationToken.None);
        Assert.True(PdfDocument.Open(output).PageCount > 0);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(input)));
    }
}
