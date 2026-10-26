using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Infrastructure.Files;
using FileKit.Pdf;
using Xunit;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class RemainingPdfActionTests
{
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());

    [Fact]
    public void PdfPackRegistersBothActionsAndNeverRemembersPasswords()
    {
        var pack = new PdfPack(Services());
        var protect = Assert.Single(pack.Actions.Where(a => a.Descriptor.Id == "powertrove.pdf.protect"));
        Assert.False(protect.Descriptor.CanRememberParameters);
        Assert.Equal(2, protect.Descriptor.Parameters!.Count(x => x.Kind == ActionParameterKind.Password));
        var flatten = Assert.Single(pack.Actions.Where(a => a.Descriptor.Id == "powertrove.pdf.flatten"));
        Assert.Equal("KeepLinks", Assert.Single(flatten.Descriptor.Parameters!).Name);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("alpha", "beta", true)]
    [InlineData("alpha", "alpha", false)]
    public async Task ProtectPreview_RejectsMissingOrMismatchedPasswords(string password, string confirmation, bool blocked)
    {
        var action = new ProtectPdfAction(Services());
        string folder = Path.Combine(Path.GetTempPath(), "PowerTrove-PdfProtect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "source.pdf");
            using (var target = File.Create(path))
                new PdfDocumentBuilder().AddBlankPage(200, 200).Save(target);
            var parameters = new DictionaryParameters(new Dictionary<string, string>
            {
                ["Password"] = password, ["ConfirmPassword"] = confirmation,
                ["AllowPrinting"] = "true", ["AllowCopying"] = "false"
            });
            var preview = await action.PreviewAsync(Snapshots.ForPaths(path), parameters, CancellationToken.None);
            Assert.Equal(blocked, preview.IsBlocked);
            Assert.DoesNotContain("alpha", preview.PreviewText ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("beta", preview.PreviewText ?? "", StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
