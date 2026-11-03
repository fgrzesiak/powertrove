using PowerTrove.Pack.Markdown.Markdig;

namespace PowerTrove.Pack.Markdown.Tests;

public sealed class MarkdownEngineTests
{
    private readonly MarkdigMarkdownEngine _engine = new();

    private string Html(string markdown, Func<string, string?>? map = null) =>
        _engine.Render(markdown, default).ToHtml(map ?? (_ => null), default);

    [Fact]
    public void RendersGitHubFlavoredMarkdown()
    {
        string html = Html("""
            ---
            title: hidden front matter
            ---
            # Release notes

            | Name | Size |
            |:-----|-----:|
            | a    | 10   |

            - [x] done
            - [ ] open

            ~~old~~ and https://example.com

            ```csharp
            var x = 1;
            ```

            Text[^1]

            [^1]: A footnote.
            """);
        Assert.DoesNotContain("hidden front matter", html);
        Assert.Contains("<h1 id=\"release-notes\">Release notes</h1>", html);
        Assert.Contains("<th class=\"align-left\">Name</th>", html);
        Assert.Contains("<td class=\"align-right\">10</td>", html);
        Assert.Contains("<span class=\"task\">☑</span>", html);
        Assert.Contains("<span class=\"task\">☐</span>", html);
        Assert.DoesNotContain("<input", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<a href=\"https://example.com\">https://example.com</a>", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.Contains("class=\"footnotes\"", html);
        Assert.DoesNotContain("style=", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>Hello", "alert")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("[click](javascript:alert(1))", "javascript")]
    [InlineData("<a href=\"java&#x09;script:alert(1)\">x</a>", "script:")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>", "iframe")]
    [InlineData("<style>body{background:url(https://tracker.example/x)}</style>", "tracker")]
    [InlineData("<svg><script>alert(1)</script></svg>", "svg")]
    [InlineData("<form action=\"https://example.com\"><input name=a><button>Go</button></form>", "<input")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://example.com\">", "refresh")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "data:")]
    [InlineData("<p style=\"background:url(https://tracker.example/x)\">x</p>", "tracker")]
    [InlineData("<object data=\"x.swf\"></object>", "object")]
    public void RemovesActiveAndExternalContent(string markdown, string forbidden)
    {
        var rendered = _engine.Render(markdown, default);
        string html = rendered.ToHtml(_ => null, default);
        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.True(rendered.RemovedContent > 0);
    }

    [Fact]
    public void KeepsHarmlessRawHtmlAndUnwrapsUnknownElements()
    {
        var rendered = _engine.Render("<details open><summary>More</summary><font color=red>Inside</font> <kbd>Ctrl</kbd></details>", default);
        string html = rendered.ToHtml(_ => null, default);
        Assert.Contains("<details open=\"\"><summary>More</summary>Inside <kbd>Ctrl</kbd></details>", html);
        Assert.Equal(1, rendered.RemovedContent);
    }

    [Fact]
    public void ImagesAreMappedByTheCallerOrReplacedByTheirDescription()
    {
        var rendered = _engine.Render("![Logo](img/logo.png) ![Remote](https://example.com/a.png) <img src=\"img/logo.png\" data-powertrove-src=\"evil\">", default);
        Assert.Equal(["img/logo.png", "https://example.com/a.png"], rendered.ImageSources);
        string html = rendered.ToHtml(source => source == "img/logo.png" ? "https://document.powertrove.example/img/logo.png" : null, default);
        Assert.Contains("<img alt=\"Logo\" src=\"https://document.powertrove.example/img/logo.png\">", html);
        Assert.Contains("<span class=\"image-missing\">Remote</span>", html);
        Assert.DoesNotContain("evil", html);
        Assert.DoesNotContain("data-powertrove-src", html);
        // A mapping may not smuggle a script URL into the document.
        Assert.DoesNotContain("javascript", rendered.ToHtml(_ => "javascript:alert(1)", default));
    }

    [Fact]
    public void TitleIsTheFirstLevelOneHeading()
    {
        Assert.Equal("Guide", _engine.Render("Intro\n\n# Guide\n\n# Second", default).Title);
        Assert.Null(_engine.Render("## Only level two", default).Title);
    }

    [Fact]
    public void DeeplyNestedHtmlIsRefused() =>
        Assert.Throws<MarkdownLimitException>(() => _engine.Render(string.Concat(Enumerable.Repeat("<div>", 400)) + "x", default));

    [Fact]
    public void ConvertsHtmlToMarkdown()
    {
        var conversion = _engine.ConvertHtml("""
            <html><head><title>T</title><style>p{color:red}</style><script>alert(1)</script></head><body>
            <h1>Title</h1>
            <p>Some <strong>bold</strong>, <em>italic</em> and <code>co`de</code> with a <a href="https://example.com/a b" title="Site">link</a>.</p>
            <ul><li>One<ul><li>Nested</li></ul></li><li><input type="checkbox" checked> Done</li></ul>
            <ol start="3"><li>Third</li><li>Fourth</li></ol>
            <blockquote><p>Quoted</p></blockquote>
            <pre><code class="language-js">let a = "```";</code></pre>
            <table><thead><tr><th>Name</th><th align="right">Size</th></tr></thead><tbody><tr><td>a|b</td><td>10</td></tr></tbody></table>
            <p><img src="pics/a.png" alt="A"> <img src="data:image/png;base64,AAAA" alt="Inline"></p>
            <p>* not a list and file_name and _under_</p>
            <form><input name="q"><button>Send</button></form><iframe src="https://example.com"></iframe>
            </body></html>
            """, default);
        string md = conversion.Markdown;
        Assert.StartsWith("# Title\n\nSome **bold**, *italic* and ``co`de`` with a [link](<https://example.com/a b> \"Site\").", md);
        Assert.Contains("- One\n  - Nested\n- [x] Done", md);
        Assert.Contains("3. Third\n4. Fourth", md);
        Assert.Contains("> Quoted", md);
        Assert.Contains("````js\nlet a = \"```\";\n````", md);
        Assert.Contains("| Name | Size |\n| --- | ---: |\n| a\\|b | 10 |", md);
        Assert.Contains("![A](pics/a.png) Inline", md);
        Assert.Contains("\\* not a list and file_name and \\_under\\_", md);
        Assert.DoesNotContain("alert", md);
        Assert.DoesNotContain("color", md);
        Assert.DoesNotContain("Send", md);
        Assert.Equal(1, conversion.OmittedImages);
        Assert.True(conversion.RemovedContent > 0);
        Assert.EndsWith("\n", md);
    }

    [Fact]
    public void ConvertedMarkdownRendersBackToEquivalentStructure()
    {
        string md = _engine.ConvertHtml("<h2>A</h2><p>B <a href=\"#c\">C</a></p><ul><li>D</li></ul>", default).Markdown;
        string html = Html(md);
        Assert.Contains("<h2 id=\"a\">A</h2>", html);
        Assert.Contains("<a href=\"#c\">C</a>", html);
        Assert.Contains("<li>D</li>", html);
    }
}
