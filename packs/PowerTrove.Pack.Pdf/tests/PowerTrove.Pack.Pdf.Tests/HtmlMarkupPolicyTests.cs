using FileKit.Html;
using PowerTrove.Pack.Pdf.Html;

namespace PowerTrove.Pack.Pdf.Tests;

public sealed class HtmlMarkupPolicyTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")][InlineData("<META HTTP-EQUIV='refresh' content='0;url=ms-settings:privacy'>")]
    [InlineData("<base href='file:///private/'>")][InlineData("<iframe srcdoc='<p>hidden</p>'></iframe>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><foreignObject/></svg>")]
    [InlineData("<p onclick='alert(1)'>text</p>")][InlineData("<template shadowrootmode='open'><meta http-equiv='refresh'></template>")]
    [InlineData("<link rel='dns-prefetch' href='//example.test'>")]
    [InlineData("<form action='https://example.test'><input autocomplete='on'></form>")]
    public void ActiveMarkupAndNavigationAreRejectedByTheRealHtmlParser(string html) => Assert.Throws<HtmlPrintException>(() => HtmlPrintDocument.FromText(html, new SafeHtmlMarkupValidator()));
}
