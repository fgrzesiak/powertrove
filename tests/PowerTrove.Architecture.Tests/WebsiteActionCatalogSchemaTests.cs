using System.Text.Json;

namespace PowerTrove.Architecture.Tests;

public sealed class WebsiteActionCatalogSchemaTests
{
    private static readonly string[] Required =
    [
        "id", "slug", "title", "description", "searchTerms", "accepts", "families",
        "minItems", "maxItems", "changesFiles", "requiresPreview"
    ];

    private static readonly HashSet<string> Allowed =
        new([.. Required, "guide", "conversions"], StringComparer.Ordinal);

    [Fact]
    public void EveryActionConformsToWebsiteSchema()
    {
        string file = Path.Combine(PackFactoriesTests.FindRoot(), "docs/catalog/actions.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in document.RootElement.GetProperty("packs").EnumerateArray())
        {
            foreach (var action in pack.GetProperty("actions").EnumerateArray())
            {
                var keys = action.EnumerateObject().Select(item => item.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string key in Required)
                    Assert.True(keys.Contains(key), $"Missing catalog field '{key}': {action.GetRawText()}");
                Assert.All(keys, key => Assert.Contains(key, Allowed));

                string id = NonEmpty(action.GetProperty("id"));
                Assert.True(ids.Add(id), $"Duplicate action ID: {id}");
                string slug = NonEmpty(action.GetProperty("slug"));
                Assert.True(slugs.Add(slug), $"Duplicate action slug: {slug}");

                foreach (string key in new[] { "title", "description" })
                    AssertLocalizedString(action.GetProperty(key));
                var searchTerms = action.GetProperty("searchTerms");
                foreach (string locale in new[] { "en", "de" })
                    AssertStringArray(searchTerms.GetProperty(locale));
                AssertStringArray(action.GetProperty("accepts"));
                AssertStringArray(action.GetProperty("families"));

                var minimum = action.GetProperty("minItems");
                Assert.Equal(JsonValueKind.Number, minimum.ValueKind);
                Assert.True(minimum.TryGetInt32(out int min) && min >= 0, id);
                var maximum = action.GetProperty("maxItems");
                if (maximum.ValueKind != JsonValueKind.Null)
                {
                    Assert.Equal(JsonValueKind.Number, maximum.ValueKind);
                    Assert.True(maximum.TryGetInt32(out int max) && max >= min, id);
                }

                Assert.Contains(action.GetProperty("changesFiles").ValueKind,
                    new[] { JsonValueKind.True, JsonValueKind.False });
                Assert.Contains(action.GetProperty("requiresPreview").ValueKind,
                    new[] { JsonValueKind.True, JsonValueKind.False });

                if (action.TryGetProperty("guide", out var guide))
                    Assert.Equal(JsonValueKind.Object, guide.ValueKind);
                if (action.TryGetProperty("conversions", out var conversions))
                    Assert.Equal(JsonValueKind.Array, conversions.ValueKind);
            }
        }
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void TextAndBrowserActionsHaveDistinctBilingualDescriptions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(PackFactoriesTests.FindRoot(), "docs/catalog/actions.json")));
        foreach (var pack in document.RootElement.GetProperty("packs").EnumerateArray()
                     .Where(p => new[] { "powertrove.text", "powertrove.browser" }
                         .Contains(p.GetProperty("id").GetString(), StringComparer.Ordinal)))
        {
            var actions = pack.GetProperty("actions").EnumerateArray().ToArray();
            Assert.NotEmpty(actions);
            foreach (string locale in new[] { "en", "de" })
            {
                var descriptions = actions.Select(a =>
                    NonEmpty(a.GetProperty("description").GetProperty(locale))).ToArray();
                Assert.Equal(descriptions.Length, descriptions.Distinct(StringComparer.Ordinal).Count());
            }
            Assert.All(actions, a => Assert.Equal(1, a.GetProperty("maxItems").GetInt32()));
        }
    }

    private static string NonEmpty(JsonElement value)
    {
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        string result = value.GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(result));
        return result;
    }

    private static void AssertLocalizedString(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        foreach (string locale in new[] { "en", "de" })
            NonEmpty(value.GetProperty(locale));
    }

    private static void AssertStringArray(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        foreach (var item in value.EnumerateArray())
            NonEmpty(item);
    }
}
