using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ContextBar.Architecture.Tests;

/// <summary>
/// ADR 0009: every assembly with user-visible text ships English (Strings.resx) and all supported
/// translations with the same keys and format placeholders, and the desktop XAML has no literal text.
/// </summary>
public class LocalizationRulesTests
{
    private static readonly string Root = FindRepositoryRoot();

    /// <summary>Translations every Strings.resx must have (English is the neutral resource).</summary>
    private static readonly string[] RequiredTranslations = { "de" };

    public static TheoryData<string> NeutralResourceFiles => new(
        new[] { "src", "packs" }
            .Select(folder => Path.Combine(Root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "Strings.resx", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(p))
            .Select(p => Path.GetRelativePath(Root, p))
            .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase));

    [Theory]
    [MemberData(nameof(NeutralResourceFiles))]
    public void EveryResourceFile_HasAllTranslations_WithTheSameKeysAndPlaceholders(string relativePath)
    {
        string neutral = Path.Combine(Root, relativePath);
        string directory = Path.GetDirectoryName(neutral)!;
        var expected = Read(neutral);

        foreach (var language in RequiredTranslations)
        {
            string translation = Path.Combine(directory, $"Strings.{language}.resx");
            Assert.True(File.Exists(translation), $"{relativePath} has no Strings.{language}.resx translation.");
        }

        foreach (var translation in Directory.GetFiles(directory, "Strings.*.resx"))
        {
            string name = Path.GetFileName(translation);
            var actual = Read(translation);
            foreach (var key in expected.Keys)
            {
                Assert.True(actual.ContainsKey(key), $"Key '{key}' is missing in {name} next to {relativePath}.");
                Assert.True(Placeholders(expected[key]).SetEquals(Placeholders(actual[key])),
                    $"Key '{key}' in {name} uses different format placeholders than English.");
                Assert.False(string.IsNullOrWhiteSpace(actual[key]), $"Key '{key}' in {name} is empty.");
            }
            foreach (var key in actual.Keys)
                Assert.True(expected.ContainsKey(key), $"Key '{key}' in {name} does not exist in English.");
        }
    }

    [Fact]
    public void DesktopXaml_HasNoLiteralUserVisibleText()
    {
        var attribute = new Regex(
            @"\s(Text|Content|Header|PlaceholderText|Title|Description|OnContent|OffContent|PrimaryButtonText|SecondaryButtonText|CloseButtonText|ToolTipService\.ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText)=""(?<value>[^""]*)""");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "ContextBar.Desktop"), "*.xaml", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
                continue;
            foreach (Match match in attribute.Matches(File.ReadAllText(file)))
            {
                string value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
                if (value.Length > 0 && !value.StartsWith('{') && value.Any(char.IsLetter))
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Bind user-visible XAML text to UiText resources instead: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Checker_ReportsMissingKeysAndPlaceholderMismatches()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Strings.resx"), Resx(("Key1", "Value {0}"), ("Key2", "Value 2")));
            File.WriteAllText(Path.Combine(tempDir, "Strings.de.resx"), Resx(("Key1", "Wert")));

            var neutral = Read(Path.Combine(tempDir, "Strings.resx"));
            var german = Read(Path.Combine(tempDir, "Strings.de.resx"));

            Assert.False(german.ContainsKey("Key2"));
            Assert.False(Placeholders(neutral["Key1"]).SetEquals(Placeholders(german["Key1"])));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static Dictionary<string, string> Read(string resxFile) =>
        XDocument.Load(resxFile).Root!.Elements("data")
            .Where(e => e.Attribute("type") == null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty);

    private static HashSet<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).ToHashSet();

    private static string Resx(params (string Key, string Value)[] entries) =>
        "<root>" + string.Concat(entries.Select(e => $"<data name=\"{e.Key}\" xml:space=\"preserve\"><value>{e.Value}</value></data>")) + "</root>";

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ContextBar.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new System.InvalidOperationException("Repository root not found.");
    }
}
