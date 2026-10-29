using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PowerTrove.ReleaseTools;

/// <summary>Prepares the verified Core payload for SDK validation. Does not sign or install anything.</summary>
public static class MsixManifest
{
    public static void Prepare(string repository, string payload, string name, string publisher, string version)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9.-]{2,49}\z") ||
            name.EndsWith('.') || name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the package identity assigned by Partner Center or an explicit test identity.");
        if (publisher.Length is < 4 or > 256 || !publisher.StartsWith("CN=", StringComparison.Ordinal) || publisher.Any(char.IsControl))
            throw new ArgumentException("A publisher distinguished name beginning with CN= is required.");
        if (!Regex.IsMatch(version, @"\A[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.0\z") ||
            !Version.TryParse(version, out var parsed) || parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build > 65535)
            throw new ArgumentException("Use a four-part Store package version with a positive major and revision zero.");
        payload = Path.GetFullPath(payload);
        for (DirectoryInfo? ancestor = new(payload); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MSIX payload paths must not traverse reparse points.");
        foreach (string required in new[] { "PowerTrove.exe", "PowerTrove.PackHost.exe", "PowerTrove.pri", "App.xbf", "MainWindow.xbf", "SettingsWindow.xbf", "catalog.json", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt" })
            if (!File.Exists(Path.Combine(payload, required))) throw new InvalidDataException("The Core package payload is incomplete.");
        if (File.Exists(Path.Combine(payload, "AppxManifest.xml"))) throw new IOException("A package manifest already exists.");
        foreach (string file in SafeFiles(payload))
        {
            string leaf = Path.GetFileName(file);
            if (leaf.StartsWith("PowerTrove.Pack.", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Store packages must use the Core profile without compiled-in tool packs.");
            if (leaf.Equals("setup-packs.json", StringComparison.OrdinalIgnoreCase) ||
                new[] { ".pfx", ".pem", ".key", ".cbpack" }.Contains(Path.GetExtension(leaf), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Store payload contains setup state, pack bundles or signing material.");
        }

        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        var manifest = new XDocument(new XElement(foundation + "Package",
            new XAttribute(XNamespace.Xmlns + "uap", uap), new XAttribute(XNamespace.Xmlns + "rescap", rescap),
            new XAttribute("IgnorableNamespaces", "uap rescap"),
            new XElement(foundation + "Identity", new XAttribute("Name", name), new XAttribute("Publisher", publisher), new XAttribute("Version", version), new XAttribute("ProcessorArchitecture", "x64")),
            new XElement(foundation + "Properties",
                new XElement(foundation + "DisplayName", "ms-resource:AppDisplayName"),
                new XElement(foundation + "PublisherDisplayName", "ms-resource:AppPublisherName"),
                new XElement(foundation + "Logo", "StoreAssets/StoreLogo.png")),
            new XElement(foundation + "Dependencies", new XElement(foundation + "TargetDeviceFamily",
                new XAttribute("Name", "Windows.Desktop"), new XAttribute("MinVersion", "10.0.22000.0"), new XAttribute("MaxVersionTested", "10.0.26100.0"))),
            new XElement(foundation + "Resources", new XElement(foundation + "Resource", new XAttribute("Language", "en-US")), new XElement(foundation + "Resource", new XAttribute("Language", "de-DE"))),
            new XElement(foundation + "Applications", new XElement(foundation + "Application",
                new XAttribute("Id", "App"), new XAttribute("Executable", "PowerTrove.exe"), new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
                new XElement(uap + "VisualElements", new XAttribute("DisplayName", "ms-resource:AppDisplayName"),
                    new XAttribute("Description", "ms-resource:AppDescription"), new XAttribute("BackgroundColor", "transparent"),
                    new XAttribute("Square150x150Logo", "StoreAssets/Square150x150Logo.png"), new XAttribute("Square44x44Logo", "StoreAssets/Square44x44Logo.png")))),
            new XElement(foundation + "Capabilities", new XElement(rescap + "Capability", new XAttribute("Name", "runFullTrust")))));

        foreach (string language in new[] { "en-US", "de-DE" })
        {
            string suffix = language == "de-DE" ? ".de" : "";
            var source = XDocument.Load(Path.Combine(repository, "src", "PowerTrove.Desktop", "Resources", "Strings" + suffix + ".resx"));
            string Read(string key) => source.Root!.Elements("data").Single(e => (string?)e.Attribute("name") == key).Element("value")!.Value;
            var resources = new XDocument(new XElement("root",
                new[] { ("AppDisplayName", "MainWindowTitle"), ("AppPublisherName", "StorePublisherDisplayName"), ("AppDescription", "StorePackageDescription") }
                    .Select(pair => new XElement("data", new XAttribute("name", pair.Item1), new XAttribute(XNamespace.Xml + "space", "preserve"), new XElement("value", Read(pair.Item2))))));
            string directory = Path.Combine(payload, "Strings", language);
            Directory.CreateDirectory(directory);
            resources.Save(Path.Combine(directory, "Resources.resw"));
        }
        manifest.Save(Path.Combine(payload, "AppxManifest.xml"));
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MSIX payload contains a linked file or directory.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string file in SafeFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }
}
