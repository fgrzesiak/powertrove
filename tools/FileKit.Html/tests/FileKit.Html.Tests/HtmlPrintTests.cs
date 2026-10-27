using System.Text;

namespace FileKit.Html.Tests;

public sealed class HtmlPrintTests
{
    [Fact]
    public void BrowserPolicyPrecedesUntrustedMarkupAndAllowsOnlyEmbeddedResources()
    {
        const string input = "<html><head><style>h1{color:red}</style></head><body><h1>Grüße</h1></body></html>";
        var document = HtmlPrintDocument.FromText(input, new SyntheticValidator());
        Assert.True(document.Html.IndexOf("Content-Security-Policy", StringComparison.Ordinal) < document.Html.IndexOf("<style>", StringComparison.Ordinal));
        Assert.Contains("default-src 'none'", document.Html); Assert.Contains("base-uri 'none'", document.Html);
        Assert.Contains("img-src data:", document.Html); Assert.Contains("style-src 'unsafe-inline'", document.Html);
        Assert.Contains("frame-src 'none'", document.Html); Assert.Contains("form-action 'none'", document.Html);
        Assert.Contains("Grüße", document.Html);
    }
    [Fact]
    public async Task EncodingSizeAndCancellationAreBoundedBeforeAnyBrowserStarts()
    {
        using var invalid = new MemoryStream([0xff, 0xfe]);
        await Assert.ThrowsAsync<HtmlPrintException>(() => HtmlPrintDocument.ReadAsync(invalid, new SyntheticValidator()));
        Assert.Throws<HtmlPrintException>(() => HtmlPrintDocument.FromText(new string('a', HtmlPrintDocument.MaxBytes + 1), new SyntheticValidator()));
        Assert.Throws<HtmlPrintException>(() => HtmlPrintDocument.FromText("\0", new SyntheticValidator()));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("<h1>text</h1>"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlPrintDocument.ReadAsync(input, new SyntheticValidator(), new(true)));
        var launcher = new Absent(); var writer = new ChromiumPdfWriter(launcher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(HtmlPrintDocument.FromText("<p>text</p>", new SyntheticValidator()), "unused.pdf", new(), new(true)));
        Assert.False(launcher.Started);
    }
    [Fact]
    public void PrintOptionsValidatePaperMarginsAndPhysicalSize()
    {
        Assert.Equal((8.5, 11d), new HtmlPrintOptions("letter").Inches);
        Assert.InRange(new HtmlPrintOptions().Inches.Width, 8.26, 8.28);
        Assert.Throws<HtmlPrintException>(() => new HtmlPrintOptions("unknown").Validate());
        Assert.Throws<HtmlPrintException>(() => new HtmlPrintOptions(MarginMillimeters: -1).Validate());
    }
    private sealed class SyntheticValidator : IHtmlMarkupValidator
    { public string ValidateAndNormalize(string html) => html; }
    private sealed class Absent : IHtmlBrowserLauncher
    {
        public bool Started { get; private set; }
        public bool IsAvailable => false;
        public IHtmlBrowserProcess Start(IReadOnlyList<string> arguments) { Started = true; throw new InvalidOperationException(); }
    }
}
