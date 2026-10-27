using System.Text.Json;
using System.Text.RegularExpressions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Manifest;
using PowerTrove.Abstractions.Testing;

namespace PowerTrove.Architecture.Tests;

public sealed class ManifestV2Tests
{
    [Fact]
    public void FileActionExtensions_ArePublicAndCannotMutateTheFilter()
    {
        var action = Assert.IsType<PowerTrove.Pack.Images.ImageExportAction>(new PowerTrove.Pack.Images.ImagesPackFactory().Create(PackFactoriesTests.Host()).Actions[0]);
        var extensions = action.InputExtensions!;
        Assert.Contains(".png", extensions);
        var list = Assert.IsAssignableFrom<IList<string>>(extensions);
        Assert.Throws<NotSupportedException>(() => list[0] = ".unsafe");
        Assert.DoesNotContain(".unsafe", action.InputExtensions!);
    }

    [Fact]
    public void EveryFactory_ProducesSchemaValidV2WithExactStaticMetadata()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackFactoriesTests.FindRoot(), "docs", "references", "action-pack-schema.json")));
        foreach (var factory in PackFactoriesTests.Factories())
        {
            var pack = factory.Create(PackFactoriesTests.Host());
            var manifest = ActionPackManifest.Create(pack);
            string json = ActionPackManifest.ToJson(manifest);
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement, schema.RootElement, schema.RootElement, factory.Info.Id);
            var parsed = ActionPackManifest.Read(json);
            Assert.Equal("2.0", parsed.SchemaVersion);
            Assert.Equal("0.1.0", parsed.MinHostVersion);
            Assert.Empty(parsed.Capabilities!);
            // Discovery metadata does not invent hashes for an assembly that was not packaged.
            Assert.Null(parsed.Payload);
            foreach (var action in pack.Actions)
            {
                var metadata = parsed.Actions.Single(a => a.Id == action.Descriptor.Id);
                Assert.Equal(action.Descriptor.ChangesFiles, metadata.ChangesFiles);
                foreach (var parameter in action.Descriptor.Parameters ?? [])
                    Assert.Equal(parameter.Editor == ActionParameterEditor.MultiSelect ? "multiSelect" : null,
                        metadata.Parameters!.Single(p => p.Name == parameter.Name).Editor);
                if (action.Descriptor.Selection is not null)
                {
                    Assert.NotNull(metadata.Selection);
                    var restored = metadata.Selection.ToRequirements();
                    Assert.Equal(action.Descriptor.Selection.MinItems, restored.MinItems);
                    Assert.Equal(action.Descriptor.Selection.MaxItems, restored.MaxItems);
                    Assert.Equal(action.Descriptor.Selection.AllowOtherItems, restored.AllowOtherItems);
                    Assert.Equal(action.Descriptor.Selection.IdealMinItems, restored.IdealMinItems);
                    Assert.Equal(action.Descriptor.Selection.IdealMaxItems, restored.IdealMaxItems);
                    Assert.Equal(action.Descriptor.Selection.IdealNeedsEveryFamily, restored.IdealNeedsEveryFamily);
                    Assert.Equal(action.Descriptor.Selection.IdealMinDistinctFamilies, restored.IdealMinDistinctFamilies);
                    Assert.True(action.Descriptor.Selection.Families.SetEquals(restored.Families));
                    Assert.Equal(action.Descriptor.Selection.FamilyLimits?.OrderBy(x => x.Key), restored.FamilyLimits?.OrderBy(x => x.Key));
                    foreach (var selection in new[] { Snapshots.ForPaths("a.pdf", "b.docx"), Snapshots.ForPaths("a.jpg", "b.png"), Snapshots.ForPaths("a.mp4", "b.mp3") })
                        Assert.Equal(SelectionRequirements.Evaluate(action.Descriptor.Selection, selection), SelectionRequirements.Evaluate(restored, selection));
                }
                if (action is IFileActionMetadata file)
                    Assert.Equal(file.InputExtensions?.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), metadata.Extensions);
            }
        }
    }

    // Deliberately small schema checker for the keywords in our checked-in schema; no packages.
    // Unknown assertion keywords fail this helper, so a schema extension cannot silently weaken it.
    private static void Check(JsonElement value, JsonElement schema, JsonElement root, string path)
    {
        foreach (var property in schema.EnumerateObject())
            Assert.Contains(property.Name, new[] { "$schema", "$id", "$defs", "$ref", "title", "description", "default", "type", "properties", "required", "additionalProperties", "items", "enum", "const", "minLength", "pattern", "minimum", "minItems", "maxItems", "uniqueItems", "maxProperties", "allOf", "if", "then", "contains" });
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            foreach (string segment in reference.GetString()![2..].Split('/')) target = target.GetProperty(segment);
            Check(value, target, root, path);
        }
        if (schema.TryGetProperty("type", out var type))
            Assert.True(type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                _ => false
            }, path);
        if (schema.TryGetProperty("allOf", out var all)) foreach (var part in all.EnumerateArray()) Check(value, part, root, path);
        if (schema.TryGetProperty("if", out var condition) && Matches(value, condition) && schema.TryGetProperty("then", out var then)) Check(value, then, root, path);
        if (schema.TryGetProperty("enum", out var choices)) Assert.Contains(choices.EnumerateArray(), c => c.GetString() == value.GetString());
        if (schema.TryGetProperty("const", out var constant)) Assert.Equal(constant.GetString(), value.GetString());
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var name in required.EnumerateArray()) Assert.True(value.TryGetProperty(name.GetString()!, out _), path + "." + name.GetString());
            if (schema.TryGetProperty("maxProperties", out var max)) Assert.True(value.EnumerateObject().Count() <= max.GetInt32(), path);
            foreach (var property in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child)) Check(property.Value, child, root, path + "." + property.Name);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    Assert.NotEqual(JsonValueKind.False, additional.ValueKind);
                    if (additional.ValueKind == JsonValueKind.Object) Check(property.Value, additional, root, path + "." + property.Name);
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min)) Assert.True(value.GetArrayLength() >= min.GetInt32(), path);
            if (schema.TryGetProperty("maxItems", out var max)) Assert.True(value.GetArrayLength() <= max.GetInt32(), path);
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean()) Assert.Equal(value.GetArrayLength(), value.EnumerateArray().Select(x => x.GetRawText()).Distinct().Count());
            if (schema.TryGetProperty("items", out var item)) foreach (var child in value.EnumerateArray()) Check(child, item, root, path + "[]");
            if (schema.TryGetProperty("contains", out var contains)) Assert.Contains(value.EnumerateArray(), child => Matches(child, contains));
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            if (schema.TryGetProperty("minLength", out var min)) Assert.True(value.GetString()!.Length >= min.GetInt32(), path);
            if (schema.TryGetProperty("pattern", out var pattern)) Assert.True(Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), path);
        }
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum)) Assert.True(value.GetInt64() >= minimum.GetInt64(), path);
    }
    private static bool Matches(JsonElement value, JsonElement condition)
    {
        if (condition.TryGetProperty("const", out var constant)) return value.ValueKind == JsonValueKind.String && value.GetString() == constant.GetString();
        if (condition.TryGetProperty("contains", out var contains)) return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(x => Matches(x, contains));
        if (condition.TryGetProperty("properties", out var properties))
            return value.ValueKind == JsonValueKind.Object && properties.EnumerateObject().All(p => !value.TryGetProperty(p.Name, out var child) || Matches(child, p.Value));
        throw new InvalidOperationException("Unsupported schema condition.");
    }
}
