using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace PowerTrove.ReleaseTools;

/// <summary>Compiles the installer's English/German copy from the desktop resource catalogs.</summary>
internal static class InstallerMessages
{
    public static void Write(string root, string output)
    {
        var directory = Path.Combine(root, "src", "PowerTrove.Desktop", "Resources");
        var english = Read(Path.Combine(directory, "Strings.resx"));
        var german = Read(Path.Combine(directory, "Strings.de.resx"));
        if (english.Count == 0 || !english.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(german.Keys))
            throw new InvalidDataException("Installer translations must contain the same nonempty key set.");
        bool preview = english.ContainsKey("Installer_Browser")
            && BrowserStoreListingsMissing(root);
        var content = new StringBuilder("[CustomMessages]\n");
        foreach (var (language, messages) in new[] { ("en", english), ("de", german) })
            foreach (var key in messages.Keys.Order(StringComparer.Ordinal))
                content.Append(language).Append('.').Append(key).Append('=')
                    .Append(key == "Installer_Browser" && preview
                        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            messages["Installer_BrowserPreview"], messages[key])
                        : messages[key]).Append('\n');
        File.WriteAllText(output, content.ToString(), new UTF8Encoding(false));
    }

    private static bool BrowserStoreListingsMissing(string root)
    {
        string source = File.ReadAllText(Path.Combine(root, "src", "PowerTrove.Application",
            "Localization", "BrowserExtensionSetup.cs"));
        bool chromeMissing = IsMissing("ChromeWebStoreId");
        bool edgeMissing = IsMissing("EdgeAddonsId");
        return chromeMissing || edgeMissing;

        bool IsMissing(string name)
        {
            var match = Regex.Match(source,
                @"\b" + name + @"\s*=\s*(?<value>null|""[^""]+"")\s*;");
            if (!match.Success)
                throw new InvalidDataException("Browser store ID constant could not be read.");
            return match.Groups["value"].Value == "null";
        }
    }

    private static Dictionary<string, string> Read(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in XDocument.Load(file).Root!.Elements("data"))
        {
            string key = (string?)item.Attribute("name") ?? string.Empty;
            if (!key.StartsWith("Installer_", StringComparison.Ordinal)) continue;
            string value = (string?)item.Element("value") ?? string.Empty;
            if (key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || string.IsNullOrWhiteSpace(value)
                || value.Any(char.IsControl) || !result.TryAdd(key, value))
                throw new InvalidDataException("Invalid installer resource.");
        }
        return result;
    }
}
