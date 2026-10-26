using System.Text;
using System.Text.Json;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Manifest;
using Xunit;

namespace PowerTrove.Core.Tests.Manifest;

public sealed class ManifestV2MetadataTests
{
    [Fact]
    public void ExistingV1Example_IsStillReadableWithoutInventingV2SafetyMetadata()
    {
        var parsed = ActionPackManifest.Read(File.ReadAllText(Path.Combine("Examples", "future-plugin-manifest.json")));
        Assert.Equal("1.0", parsed.SchemaVersion);
        Assert.Null(parsed.MinHostVersion); Assert.Null(parsed.Payload); Assert.Null(parsed.Capabilities);
        Assert.Null(Assert.Single(parsed.Actions).ChangesFiles);
    }

    [Fact]
    public void PayloadHashesAndAllSelectionRoles_RoundTrip()
    {
        var selection = new SelectionRequirements(new HashSet<string> { "pdf", "document" }, MinItems: 2, MaxItems: 5,
            AllowOtherItems: false, IdealMinItems: 2, IdealMaxItems: 4, IdealNeedsEveryFamily: true,
            FamilyLimits: new Dictionary<string, FamilyLimit> { ["pdf"] = new(1, 3), ["document"] = new(1, 2) }, IdealMinDistinctFamilies: 2);
        var manifest = Valid() with
        {
            Payload = new("pack.dll", 5, new[] { new ManifestPayloadFile("pack.dll", new string('a', 64), 5) }),
            Capabilities = new[] { "ocr" },
            Actions = new[] { Valid().Actions[0] with { InputKinds = new[] { "files" }, Selection = ManifestSelection.FromRequirements(selection), Extensions = new[] { ".pdf", ".docx" }, ChangesFiles = true } }
        };
        var parsed = ActionPackManifest.Read(ActionPackManifest.ToJson(manifest));
        Assert.Equal("pack.dll", parsed.Payload!.EntryAssembly);
        Assert.Equal(5, parsed.Payload.SizeBytes);
        Assert.Equal(new string('a', 64), Assert.Single(parsed.Payload.Files).Sha256);
        Assert.Equal(new[] { "ocr" }, parsed.Capabilities);
        var restored = Assert.Single(parsed.Actions).Selection!.ToRequirements();
        Assert.Equal(2, restored.IdealMinDistinctFamilies); Assert.True(restored.IdealNeedsEveryFamily);
        Assert.Equal(new FamilyLimit(1, 3), restored.FamilyLimits!["pdf"]);
        Assert.Equal(new[] { ".pdf", ".docx" }, parsed.Actions[0].Extensions);
    }

    [Theory]
    [InlineData("../pack.dll")]
    [InlineData("/pack.dll")]
    [InlineData("C:/pack.dll")]
    [InlineData("folder\\pack.dll")]
    [InlineData("folder./pack.dll")]
    [InlineData("folder//pack.dll")]
    [InlineData("CON.dll")]
    public void UnsafePayloadPaths_AreRejected(string path)
    {
        var metadata = Valid() with { Payload = new(path, 5, new[] { new ManifestPayloadFile(path, new string('b', 64), 5) }) };
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(metadata));
    }

    [Fact]
    public void PayloadMissingEntryBadHashOrSizeMismatch_IsRejected()
    {
        var invalid = new[]
        {
            new ManifestPayload("pack.dll", 1, new[] { new ManifestPayloadFile("other.dll", new string('c', 64), 1) }),
            new ManifestPayload("pack.dll", 1, new[] { new ManifestPayloadFile("pack.dll", "invalid", 1) }),
            new ManifestPayload("pack.dll", 2, new[] { new ManifestPayloadFile("pack.dll", new string('c', 64), 1) }),
            new ManifestPayload("pack.dll", 1, new[] { new ManifestPayloadFile("pack.dll", new string('c', 64), 1), new ManifestPayloadFile("PACK.dll", new string('c', 64), 0) })
        };
        foreach (var payload in invalid) Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Payload = payload }));
    }

    [Fact]
    public void Reader_RejectsUnknownVersionDuplicatePropertiesAndMissingV2Fields()
    {
        string json = ActionPackManifest.ToJson(Valid());
        Assert.Throws<JsonException>(() => ActionPackManifest.Read(json.Replace("\"2.0\"", "\"9.0\"")));
        Assert.Throws<JsonException>(() => ActionPackManifest.Read(json.Insert(1, "\"schemaVersion\":\"1.0\",")));
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Capabilities = null }));
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Actions = new[] { Valid().Actions[0] with { ChangesFiles = null } } }));
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Actions = new[] { Valid().Actions[0] with { InputKinds = new[] { "files" } } } }));
    }

    [Fact]
    public void LimitsMalformedJsonAndNegativeSelection_AreRejected()
    {
        Assert.Throws<JsonException>(() => ActionPackManifest.Read(new string(' ', ActionPackManifest.MaximumJsonBytes + 1)));
        Assert.Throws<JsonException>(() => ActionPackManifest.Read(new string('ä', ActionPackManifest.MaximumJsonBytes / 2 + 1)));
        string valid = ActionPackManifest.ToJson(Valid());
        string nested = valid.Insert(1, "\"extra\":" + new string('[', 33) + "0" + new string(']', 33) + ",");
        Assert.ThrowsAny<JsonException>(() => ActionPackManifest.Read(nested));
        var random = new Random(157);
        for (int size = 1; size < 300; size += 17)
        {
            byte[] bytes = new byte[size]; random.NextBytes(bytes);
            Assert.ThrowsAny<JsonException>(() => ActionPackManifest.Read(Encoding.UTF8.GetString(bytes)));
        }
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Actions = new[] { Valid().Actions[0] with { Selection = new(new[] { "pdf" }, -1, null, null, null, false) } } }));
    }

    [Fact]
    public void PasswordDefaultsAndDuplicateActionIds_AreRejected()
    {
        var action = Valid().Actions[0];
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with { Actions = new[] { action, action } }));
        Assert.Throws<JsonException>(() => ActionPackManifest.ToJson(Valid() with
        {
            Actions = new[] { action with { Parameters = new[] { new ManifestParameter("Secret", "password", Default: "do-not-store") } } }
        }));
    }

    private static ActionManifest Valid() => new("2.0", "contextbar.test", "0.1.0", "Test", "PowerTrove", "1",
        new[] { new ManifestAction("contextbar.test.action", "Action", new[] { "none" }, ChangesFiles: false) },
        MinHostVersion: "0.1.0", Capabilities: Array.Empty<string>());
}
