using System.Globalization;
using System.Text.Json;
using PowerTrove.Abstractions.Manifest;
using Xunit;

namespace PowerTrove.Abstractions.Tests;

public sealed class ManifestLocalizationTests
{
    private static ActionManifest English() => new("2.0", "powertrove.test", "1.0.0", "Images", "Tests", "1",
        [new("image.convert", "Convert image", ["files"], "Create a new image", Parameters:
            [new("format", "choice", "Format", Choices: ["png"], Default: "png", ChoiceTitles: new Dictionary<string, string> { ["png"] = "PNG image" })],
            Selection: ManifestSelection.FromRequirements(SelectionRequirements.Any("image")), ChangesFiles: true)],
        MinHostVersion: "0.3.0", Capabilities: []);
    private static ActionManifest German(ActionManifest english) => english with
    {
        DisplayName = "Bilder",
        Actions = [english.Actions[0] with { Title = "Bild konvertieren", Description = "Ein neues Bild erstellen",
            Parameters = [english.Actions[0].Parameters![0] with { Title = "Zielformat", Placeholder = "Format auswählen",
                ChoiceTitles = new Dictionary<string, string> { ["png"] = "PNG-Bild" } }] }]
    };

    [Fact]
    public void RoundTrip_ResolvesParentCulture_AndPreservesOriginalContractAndFallback()
    {
        var english = English();
        var manifest = ManifestLocalization.WithTranslation(english, "de", German(english));
        string originalJson = ActionPackManifest.ToJson(manifest);
        manifest = ActionPackManifest.Read(originalJson);
        var german = ManifestLocalization.ForCulture(manifest, CultureInfo.GetCultureInfo("de-AT"));
        Assert.Equal("Bilder", german.DisplayName); Assert.Equal("Bild konvertieren", german.Actions[0].Title);
        var parameter = Assert.Single(german.Actions[0].Parameters!);
        Assert.Equal("Zielformat", parameter.Title); Assert.Equal("PNG-Bild", parameter.ChoiceTitles!["png"]);
        Assert.Equal("png", parameter.Default); Assert.Equal("format", parameter.Name); Assert.Equal(new[] { "png" }, parameter.Choices);
        Assert.Equal(manifest.Actions[0].Id, german.Actions[0].Id);
        Assert.Equal("Convert image", ManifestLocalization.ForCulture(manifest, CultureInfo.GetCultureInfo("fr-FR")).Actions[0].Title);
        Assert.Equal("Convert image", ManifestLocalization.ForCulture(manifest, CultureInfo.GetCultureInfo("en-US")).Actions[0].Title);
        Assert.Equal(originalJson, ActionPackManifest.ToJson(manifest));
        Assert.Null(ActionPackManifest.Read(ActionPackManifest.ToJson(english)).Localizations);
    }

    [Fact]
    public void TranslationCapture_RefusesChangesToIdentityPermissionsAndParameterValues()
    {
        var english = English(); var german = German(english);
        foreach (var changed in new[] { german with { Id = "other" }, german with { Permissions = ["new.permission"] },
            german with { Actions = [german.Actions[0] with { RequiresPreview = false }] },
            german with { Actions = [german.Actions[0] with { Parameters = [german.Actions[0].Parameters![0] with { Default = "other" }] }] } })
            Assert.Throws<JsonException>(() => ManifestLocalization.WithTranslation(english, "de", changed));
    }

    [Fact]
    public void ReaderRejectsUnknownLocalizedIdsChoicesAndExecutionFields()
    {
        var english = English();
        string json = ActionPackManifest.ToJson(ManifestLocalization.WithTranslation(english, "de", German(english)));
        // Replace only the translation's stable dictionary keys, leaving the base contract intact.
        foreach (var mutated in new[] { json.Replace("\"image.convert\": {", "\"other.action\": {"),
            json.Replace("\"format\": {", "\"other.parameter\": {"),
            json.Replace("\"png\": \"PNG-Bild\"", "\"other\": \"PNG-Bild\""),
            json.Replace("\"title\": \"Bild konvertieren\"", "\"title\": \"Bild konvertieren\", \"inputKinds\": [\"text\"]") })
            Assert.Throws<JsonException>(() => ActionPackManifest.Read(mutated));
        var oversized = German(english) with { DisplayName = new string('x', 4097) };
        Assert.Throws<JsonException>(() => ManifestLocalization.WithTranslation(english, "de", oversized));
    }
}
