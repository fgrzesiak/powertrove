using System.Xml.Linq;
using Xunit;

namespace PowerTrove.ReleaseTools.Tests;

public sealed class MsixManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PowerTrove-MSIX-tests-" + Guid.NewGuid().ToString("N"));
    private string Payload => Path.Combine(_root, "payload");

    public MsixManifestTests()
    {
        Directory.CreateDirectory(Payload);
        // Manifest-only fixtures are generated; SDK/runtime validation uses the real published payload separately.
        foreach (string file in new[] { "PowerTrove.exe", "PowerTrove.PackHost.exe", "PowerTrove.pri", "App.xbf", "MainWindow.xbf", "SettingsWindow.xbf", "catalog.json", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt" })
            File.WriteAllText(Path.Combine(Payload, file), "generated manifest fixture");
        string resources = Path.Combine(_root, "src", "PowerTrove.Desktop", "Resources");
        Directory.CreateDirectory(resources);
        foreach (string suffix in new[] { "", ".de" })
            new XDocument(new XElement("root", new[] { "MainWindowTitle", "StorePublisherDisplayName", "StorePackageDescription" }
                .Select(key => new XElement("data", new XAttribute("name", key), new XElement("value", key + suffix)))))
                .Save(Path.Combine(resources, "Strings" + suffix + ".resx"));
    }

    private void Prepare(string identity = "PowerTrove.Test", string publisher = "CN=PowerTrove Test", string version = "1.0.1.0") =>
        MsixManifest.Prepare(_root, Payload, identity, publisher, version);

    [Fact]
    public void Manifest_IsDesktopOnlyFullTrust_WithLocalizedMetadataAndEscapedPublisher()
    {
        Prepare(publisher: "CN=Example & Test");
        var doc = XDocument.Load(Path.Combine(Payload, "AppxManifest.xml"));
        XNamespace f = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        Assert.Equal("CN=Example & Test", (string?)doc.Root!.Element(f + "Identity")!.Attribute("Publisher"));
        Assert.Equal("Windows.Desktop", (string?)doc.Descendants(f + "TargetDeviceFamily").Single().Attribute("Name"));
        Assert.Equal("Windows.FullTrustApplication", (string?)doc.Descendants(f + "Application").Single().Attribute("EntryPoint"));
        Assert.Equal("runFullTrust", (string?)doc.Descendants().Single(e => e.Name.LocalName == "Capability").Attribute("Name"));
        Assert.Equal(new[] { "en-US", "de-DE" }, doc.Descendants(f + "Resource").Select(e => (string?)e.Attribute("Language")));
        string en = File.ReadAllText(Path.Combine(Payload, "Strings", "en-US", "Resources.resw"));
        string de = File.ReadAllText(Path.Combine(Payload, "Strings", "de-DE", "Resources.resw"));
        Assert.NotEqual(en, de);
        Assert.Contains("ms-resource:AppDescription", File.ReadAllText(Path.Combine(Payload, "AppxManifest.xml")));
        Assert.Throws<IOException>(() => Prepare());
    }

    [Theory]
    [InlineData("0.3.2.0")]
    [InlineData("1.0.1.4")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.65536.0.0")]
    [InlineData("1.0.0")]
    public void InvalidStoreVersion_IsRejectedBeforeWriting(string version)
    {
        Assert.Throws<ArgumentException>(() => Prepare(version: version));
        Assert.False(File.Exists(Path.Combine(Payload, "AppxManifest.xml")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad identity")]
    [InlineData("ab")]
    public void InvalidIdentity_IsRejected(string identity) => Assert.Throws<ArgumentException>(() => Prepare(identity: identity));

    [Theory]
    [InlineData("PowerTrove.Pack.Images.dll")]
    [InlineData("setup-packs.json")]
    [InlineData("generated.pfx")]
    [InlineData("generated.cbpack")]
    public void CompiledInPacksSetupStateAndSigningKeys_AreRejected(string file)
    {
        File.WriteAllText(Path.Combine(Payload, file), "generated fixture");
        Assert.Throws<InvalidDataException>(() => Prepare());
    }

    [Fact]
    public void MissingWorker_RejectsAnIncompletePackage()
    {
        File.Delete(Path.Combine(Payload, "PowerTrove.PackHost.exe"));
        Assert.Throws<InvalidDataException>(() => Prepare());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
