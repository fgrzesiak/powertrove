using System.Globalization;
using System.Text.RegularExpressions;

namespace ContextBar.Pack.Files;

public sealed record RenameOptions(string Pattern, string Find, string Replace, string Case, int StartAt, bool KeepExtension, string Approval);

public static class RenameNames
{
    private static readonly Regex Token = new(@"\{(name|ext|n(?::0{1,12})?|date|exifdate)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string Generate(string fileName, RenameOptions options, int index, DateTime modified, DateTime? photoDate = null)
    {
        if (options.Pattern.Length > 1024 || options.Find.Length > 1024 || options.Replace.Length > 1024) throw new ArgumentException("Rename option too long.");
        string unknown = Token.Replace(options.Pattern, "");
        if (unknown.Contains('{') || unknown.Contains('}')) throw new ArgumentException("Unknown rename token.");
        string stem = Path.GetFileNameWithoutExtension(fileName), extension = Path.GetExtension(fileName);
        if (options.Find.Length != 0) stem = stem.Replace(options.Find, options.Replace, StringComparison.Ordinal);
        string name = Token.Replace(options.Pattern, match => match.Groups[1].Value switch
        {
            "name" => stem, "ext" => extension.TrimStart('.'),
            "date" => modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "exifdate" => (photoDate ?? modified).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "n" => checked(options.StartAt + index).ToString(CultureInfo.InvariantCulture),
            var padded => checked(options.StartAt + index).ToString(padded[2..], CultureInfo.InvariantCulture)
        });
        name = options.Case switch
        {
            "lower" => name.ToLowerInvariant(), "upper" => name.ToUpperInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()), _ => name
        };
        return name + (options.KeepExtension && !options.Pattern.Contains("{ext}", StringComparison.Ordinal) ? extension : "");
    }
}
