using System.Text;

namespace FileKit.Html;

/// <summary>Bounded CSS resource rewriting; unsupported escapes/import conditions fail explicitly.
/// The browser still enforces its independent content and network policies.</summary>
public static class EpubCss
{
    public static string Rewrite(string css, Func<string, string> embedResource, Func<string, string> importStylesheet, CancellationToken ct)
    {
        if (css.Length > 1024 * 1024 || css.Contains('\\') || css.Contains('\0') || css.Contains('<')) throw new EpubException("Unsupported CSS escaping, markup or size.", EpubFailure.Unsupported);
        var output = new StringBuilder(); int i = 0;
        while (i < css.Length)
        {
            ct.ThrowIfCancellationRequested(); char c = css[i];
            if (c is '\'' or '"') { int start = i; Quoted(); output.Append(css.AsSpan(start, i - start)); }
            else if (c == '/' && i + 1 < css.Length && css[i + 1] == '*') { int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) throw new EpubException("Unterminated CSS comment."); i = end + 2; output.Append(' '); }
            else if (Matches("@import"))
            {
                i += 7; Space(); string path = i < css.Length && css[i] is '\'' or '"' ? Quoted() : Url(); Space();
                if (i >= css.Length || css[i++] != ';') throw new EpubException("Conditional CSS imports are unsupported.", EpubFailure.Unsupported);
                output.Append(importStylesheet(path));
            }
            else if (Matches("url") && (i == 0 || !Identifier(css[i - 1])))
            { string path = Url(); output.Append("url(\"").Append(embedResource(path)).Append("\")"); }
            else { output.Append(c); i++; }
            if (output.Length > HtmlPrintDocument.MaxBytes) throw new EpubException("Expanded EPUB styles exceed the document limit.", EpubFailure.Limit);
        }
        return output.ToString();
        bool Matches(string token) => css.AsSpan(i).StartsWith(token, StringComparison.OrdinalIgnoreCase) && (i + token.Length == css.Length || !Identifier(css[i + token.Length]));
        void Space() { while (i < css.Length && char.IsWhiteSpace(css[i])) i++; }
        string Quoted()
        {
            char quote = css[i++]; int start = i;
            while (i < css.Length && css[i] != quote) { if (css[i] is '\r' or '\n') throw new EpubException("Invalid CSS string."); i++; }
            if (i == css.Length) throw new EpubException("Unterminated CSS string."); string value = css[start..i]; i++; return value;
        }
        string Url()
        {
            if (!Matches("url")) throw new EpubException("Unsupported CSS resource syntax.", EpubFailure.Unsupported);
            i += 3; Space(); if (i >= css.Length || css[i++] != '(') throw new EpubException("Invalid CSS resource syntax."); Space(); string path;
            if (i < css.Length && css[i] is '\'' or '"') { path = Quoted(); Space(); }
            else { int start = i; while (i < css.Length && css[i] != ')') i++; path = css[start..i].Trim(); if (path.Any(char.IsWhiteSpace) || path.Contains('(') || path.Contains('"') || path.Contains('\'')) throw new EpubException("Invalid CSS URL."); }
            if (i >= css.Length || css[i++] != ')') throw new EpubException("Unterminated CSS URL."); return path;
        }
    }
    private static bool Identifier(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
}
