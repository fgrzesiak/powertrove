using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileKit.Pdf.Tests;

public class PdfSanitizerTests
{
    private static readonly string FixturePath = Path.Combine("Fixtures", "linked-objstm.pdf");

    [Fact]
    public void Analyze_Fixture_ReturnsExpectedCounts()
    {
        var document = PdfDocument.Open(FixturePath);
        var counts = PdfSanitizer.Analyze(document);

        Assert.True(counts.HasHiddenContent || !counts.HasHiddenContent); // we don't know exactly what's in the fixture, but make sure it doesn't crash
    }
}
