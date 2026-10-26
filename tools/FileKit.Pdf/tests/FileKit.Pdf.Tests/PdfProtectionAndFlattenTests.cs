using System.Text;
using Xunit;

namespace FileKit.Pdf.Tests;

public sealed class PdfProtectionAndFlattenTests
{
    [Fact]
    public void AES256_R6_RoundTrip_RequiresCorrectPassword()
    {
        using var output = new MemoryStream();
        new PdfDocumentBuilder().AddBlankPage(220, 330).Save(output,
            new PdfWriteOptions { Encryption = new PdfEncryptionOptions("s3cret!") });
        byte[] data = output.ToArray();
        Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(data));
        Assert.Throws<PdfPasswordRequiredException>(() => PdfDocument.Load(data, password: "incorrect"));
        Assert.Single(PdfDocument.Load(data, password: "s3cret!").Pages);
        Assert.DoesNotContain("s3cret!", Encoding.Latin1.GetString(data));
    }

    [Fact]
    public void AES256_R6_IsRandomized_AndRespectsPermissions()
    {
        byte[] Make()
        {
            using var output = new MemoryStream();
            new PdfDocumentBuilder().AddBlankPage(200, 200).Save(output,
                new PdfWriteOptions
                {
                    Encryption = new PdfEncryptionOptions("pass",
                        Permissions: new PdfPermissions(false, false))
                });
            return output.ToArray();
        }
        byte[] first = Make(), second = Make();
        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(second));
        Assert.Contains("/AESV3", Encoding.Latin1.GetString(first));
        var source = PdfDocument.Load(first, password: "pass");
        var dict = Assert.IsType<PdfDictionary>(source.Resolve(source.Trailer["Encrypt"]));
        Assert.Equal(6, Assert.IsType<PdfInteger>(dict["R"]).Value);
        Assert.Equal(5, Assert.IsType<PdfInteger>(dict["V"]).Value);
        int flags = checked((int)Assert.IsType<PdfInteger>(dict["P"]).Value);
        Assert.Equal(0, flags & 4);
        Assert.Equal(0, flags & 16);
    }

    [Fact]
    public void AES256_RefusesEmptyPassword_AndCompactMode()
    {
        Assert.Throws<PdfOperationException>(() =>
        {
            using var output = new MemoryStream();
            new PdfDocumentBuilder().AddBlankPage(100, 100).Save(output,
                new PdfWriteOptions { Encryption = new PdfEncryptionOptions("") });
        });
        Assert.Throws<PdfOperationException>(() =>
        {
            using var output = new MemoryStream();
            new PdfDocumentBuilder().AddBlankPage(100, 100).Save(output,
                new PdfWriteOptions { Compact = true, Encryption = new PdfEncryptionOptions("pass") });
        });
    }

    [Fact]
    public void Flatten_EmbedsAppearance_RemovesAcroForm_PreservesOptionalLinks()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources <<>> /Contents 4 0 R /Annots [5 0 R 8 0 R] >>",
            TestPdfFactory.StreamObject("", "q Q"),
            "<< /Type /Annot /Subtype /Widget /Rect [10 20 110 70] /AP << /N 6 0 R >> >>",
            TestPdfFactory.StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 100 50] /Resources <<>>", "0 0 m 10 10 l S"),
            "<< /Fields [5 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] /A << /S /URI /URI (https://example.com) >> >>"
        };
        var source = PdfDocument.Load(TestPdfFactory.Serialize(objects, "/Root 1 0 R"));
        Assert.Equal(new PdfFlattenReport(1, 0, 1), PdfAnnotationFlattener.Analyze(source));
        using var output = new MemoryStream();
        PdfTools.Flatten(source, output, keepLinks: true);
        var result = PdfDocument.Load(output.ToArray());
        Assert.Null(result.Catalog["AcroForm"]);
        var resource = Assert.IsType<PdfDictionary>(result.Resolve(result.Pages[0].Resources));
        var xobjects = Assert.IsType<PdfDictionary>(result.Resolve(resource["XObject"]));
        Assert.Contains(xobjects.Entries, x => x.Key.StartsWith("FKAp", StringComparison.Ordinal));
        var annots = Assert.IsType<PdfArray>(result.Resolve(result.Pages[0].Dictionary["Annots"]));
        Assert.Single(annots.Items);
        Assert.Equal("Link", Assert.IsType<PdfDictionary>(result.Resolve(annots[0])).GetName("Subtype"));
        using var withoutLinks = new MemoryStream();
        PdfTools.Flatten(source, withoutLinks, keepLinks: false);
        Assert.Null(PdfDocument.Load(withoutLinks.ToArray()).Pages[0].Dictionary["Annots"]);
    }

    [Fact]
    public void FlattenPlacement_RespectsRotatedAppearanceBBox()
    {
        var result = PdfAnnotationFlattener.AppearancePlacement(
            [10, 20, 110, 70], [0, 0, 50, 100], [0, 1, -1, 0, 0, 0]);
        Assert.True(result.a > 0 && result.d > 0);
        Assert.True(double.IsFinite(result.e) && double.IsFinite(result.f));
    }
}
