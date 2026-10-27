using System.Text;

namespace FileKit.Imaging.Tests;

public sealed class SvgRasterSourceTests
{
    private static SvgRasterSource Prepare(string body, int width = 200, int height = 100) => SvgRasterSource.Prepare(Encoding.UTF8.GetBytes(body), width, height);
    [Fact]
    public void StaticShapesAndLocalGradientsFitWithinResolutionAndKeepAspectRatio()
    {
        var svg = Prepare("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40'><defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs><rect x='2' y='2' width='30' height='30' fill='url(#g)'/></svg>");
        Assert.Equal((100, 100), (svg.Width, svg.Height)); Assert.Contains("width=\"100\"", svg.Xml);
        var pixels = Prepare("<svg xmlns='http://www.w3.org/2000/svg' width='20px' height='10px'><path d='M0 0 L20 10'/></svg>");
        Assert.Equal((200, 100), (pixels.Width, pixels.Height));
    }
    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///private'>]><svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>&x;</svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><image href='https://example.test/pixel'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1' onload='alert(1)'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><use href='#loop' id='loop'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><style>@import 'https://example.test';</style></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect style='fill:url(https://example.test)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><rect fill='u\\72l(file:///private)'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 NaN 1'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 -1 1'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1e-300 1'/>")]
    public void UnsafeOrUnsupportedInputsAreRejectedBeforeRendering(string svg) => Assert.Throws<SvgInputException>(() => Prepare(svg));
    [Fact]
    public void DimensionsElementLimitsAndCancellationAreEnforced()
    {
        Assert.Throws<SvgInputException>(() => Prepare("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>", 10000, 10000));
        Assert.Throws<SvgInputException>(() => Prepare("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'>" + string.Concat(Enumerable.Repeat("<g/>", 5001)) + "</svg>"));
        Assert.Throws<OperationCanceledException>(() => SvgRasterSource.Prepare(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>"), 1, 1, new(true)));
    }
}
