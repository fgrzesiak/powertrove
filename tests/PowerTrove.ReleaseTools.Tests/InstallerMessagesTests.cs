using System.Xml.Linq;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class InstallerMessagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PowerTrove-InstallerMessages-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RendersBothLanguagesAndExcludesUnrelatedDesktopCopy()
    {
        Write("Strings.resx", "Installer_Title", "Choose tools");
        Write("Strings.de.resx", "Installer_Title", "Tools auswählen");
        string output = Path.Combine(root, "messages.isl");
        InstallerMessages.Write(root, output);
        string content = File.ReadAllText(output);
        Assert.StartsWith("[CustomMessages]\n", content);
        Assert.Contains("en.Installer_Title=Choose tools\n", content);
        Assert.Contains("de.Installer_Title=Tools auswählen\n", content);
        Assert.DoesNotContain("Other", content);
    }

    [Fact]
    public void BrowserPreviewFollowsTheSingleStoreIdSource()
    {
        string resources = Path.Combine(root, "src", "PowerTrove.Desktop", "Resources");
        Directory.CreateDirectory(resources);
        foreach (string language in new[] { "Strings.resx", "Strings.de.resx" })
        {
            new XDocument(new XElement("root",
                new XElement("data", new XAttribute("name", "Installer_Browser"), new XElement("value", "Browser Tools")),
                new XElement("data", new XAttribute("name", "Installer_BrowserPreview"), new XElement("value", "{0} (Preview)"))))
                .Save(Path.Combine(resources, language));
        }

        string source = Path.Combine(root, "src", "PowerTrove.Application", "Localization", "BrowserExtensionSetup.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        string output = Path.Combine(root, "messages.isl");
        File.WriteAllText(source, "public const string? ChromeWebStoreId = null; public const string? EdgeAddonsId = null;");
        InstallerMessages.Write(root, output);
        Assert.Contains("en.Installer_Browser=Browser Tools (Preview)", File.ReadAllText(output));

        File.WriteAllText(source, "public const string? ChromeWebStoreId = \"chrome-id\"; public const string? EdgeAddonsId = \"edge-id\";");
        InstallerMessages.Write(root, output);
        Assert.Contains("en.Installer_Browser=Browser Tools\n", File.ReadAllText(output));
        Assert.DoesNotContain("en.Installer_Browser=Browser Tools (Preview)", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("Installer_Other", "Tools")]
    [InlineData("Installer_Title", "Injected\n[Setup]")]
    [InlineData("Installer_Title", "")]
    public void RefusesMissingTranslationsOrMultilineValues(string key, string value)
    {
        Write("Strings.resx", "Installer_Title", "Choose tools");
        Write("Strings.de.resx", key, value);
        string output = Path.Combine(root, "messages.isl");
        Assert.Throws<InvalidDataException>(() => InstallerMessages.Write(root, output));
        Assert.False(File.Exists(output));
    }

    private void Write(string file, string key, string value)
    {
        string directory = Path.Combine(root, "src", "PowerTrove.Desktop", "Resources");
        Directory.CreateDirectory(directory);
        new XDocument(new XElement("root", new XElement("data", new XAttribute("name", key), new XElement("value", value)),
            new XElement("data", new XAttribute("name", "Other"), new XElement("value", "Other"))))
            .Save(Path.Combine(directory, file));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
