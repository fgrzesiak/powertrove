namespace PowerTrove.Pack.Markdown;

/// <summary>Self-contained styles: no fonts, images or other resources are loaded.</summary>
internal static class MarkdownStyles
{
    public const string Document = """
:root{color-scheme:light}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:#ffffff;color:#1f2328;font-family:"Segoe UI",system-ui,-apple-system,"Helvetica Neue",Arial,sans-serif;font-size:16px;line-height:1.6}
.markdown-body{box-sizing:border-box;max-width:860px;margin:0 auto;padding:32px 40px;overflow-wrap:break-word}
.markdown-body>*:first-child{margin-top:0}
.markdown-body>*:last-child{margin-bottom:0}
h1,h2,h3,h4,h5,h6{margin:1.5em 0 .6em;font-weight:600;line-height:1.25}
h1{font-size:2em;padding-bottom:.3em;border-bottom:1px solid #d1d9e0}
h2{font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid #d1d9e0}
h3{font-size:1.25em}h4{font-size:1em}h5{font-size:.875em}h6{font-size:.85em;color:#59636e}
p,blockquote,ul,ol,dl,table,pre,details,figure{margin:0 0 1em}
a{color:#0969da;text-decoration:none}a:hover{text-decoration:underline}
strong{font-weight:600}
hr{height:.25em;padding:0;margin:1.5em 0;background:#d1d9e0;border:0}
blockquote{padding:0 1em;color:#59636e;border-left:.25em solid #d1d9e0}
ul,ol{padding-left:2em}li+li{margin-top:.25em}
li>p{margin-top:1em}
ul.contains-task-list{padding-left:1.2em;list-style:none}
.task-list-item .task{display:inline-block;width:1.2em;margin-left:-1.2em}
code,kbd,pre,samp{font-family:"Cascadia Mono",Consolas,"SFMono-Regular",Menlo,monospace;font-size:.875em}
code{padding:.2em .4em;background:#eff1f3;border-radius:6px}
pre{padding:16px;overflow:auto;line-height:1.45;background:#f6f8fa;border-radius:6px}
pre code{padding:0;background:transparent;border-radius:0;font-size:1em;white-space:pre}
kbd{padding:.15em .4em;border:1px solid #d1d9e0;border-bottom-width:2px;border-radius:6px;background:#f6f8fa}
table{display:block;width:max-content;max-width:100%;overflow:auto;border-spacing:0;border-collapse:collapse}
th,td{padding:6px 13px;border:1px solid #d1d9e0}
th{font-weight:600;background:#f6f8fa}
tr:nth-child(2n) td{background:#f6f8fa}
.align-left{text-align:left}.align-center{text-align:center}.align-right{text-align:right}
img{max-width:100%;height:auto;box-sizing:content-box}
.image-missing{display:inline-block;padding:0 .4em;color:#59636e;border:1px dashed #d1d9e0;border-radius:4px;font-size:.875em}
mark{background:#fff8c5;color:inherit}
.footnotes{margin-top:2em;font-size:.875em;color:#59636e;border-top:1px solid #d1d9e0}
dl dt{margin-top:1em;font-weight:600}dl dd{margin:0 0 0 1em}
details summary{cursor:pointer}
""";

    public const string DarkScheme = """
@media (prefers-color-scheme:dark){
:root{color-scheme:dark}
body{background:#0d1117;color:#e6edf3}
h1,h2{border-bottom-color:#3d444d}h6,blockquote,.footnotes,.image-missing{color:#9198a1}
a{color:#4493f8}hr{background:#3d444d}blockquote{border-left-color:#3d444d}
code{background:#2a313c}pre,th,tr:nth-child(2n) td,kbd{background:#151b23}
th,td,kbd,.image-missing,.footnotes{border-color:#3d444d}mark{background:#bb800926}
}
""";

    public const string Print = """
.markdown-body{max-width:none;padding:0}
pre,blockquote,table,img,figure{break-inside:avoid}
h1,h2,h3,h4,h5,h6{break-after:avoid}
pre code{white-space:pre-wrap}
table{display:table;width:auto}
a{color:#0969da}
""";
}
