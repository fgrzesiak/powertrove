using System.Text.Json;
using PowerTrove.Pack.Images;
using PowerTrove.Pack.Media;

namespace PowerTrove.Architecture.Tests;

public sealed class WebsiteConversionCatalogTests
{
    [Fact]
    public void WebsiteFormatPairs_ReferToRealFileActionsAndValidParameterChoices()
    {
        var host = PackFactoriesTests.Host()
            .Add(new CompositeImageEngine(Array.Empty<IImageEngine>()))
            .Add(new CompositeMediaEngine(Array.Empty<IMediaEngine>()));
        var actions = PackFactoriesTests.Factories().SelectMany(f => f.Create(host).Actions).ToDictionary(a => a.Descriptor.Id);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackFactoriesTests.FindRoot(), "docs/catalog/actions.json")));
        var records = document.RootElement.GetProperty("packs").EnumerateArray().SelectMany(p => p.GetProperty("actions").EnumerateArray()).ToArray();
        Assert.Equal(actions.Keys.OrderBy(id => id, StringComparer.Ordinal), records.Select(a => a.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal));
        var slugs = records.Select(a => a.GetProperty("slug").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(a => a.TryGetProperty("conversions", out _)))
        {
            var action = actions[record.GetProperty("id").GetString()!];
            Assert.Contains(ContentKind.Files, action.Descriptor.Accepts);
            Assert.True(action.Descriptor.ChangesFiles && action.Descriptor.RequiresPreview);
            foreach (var pair in record.GetProperty("conversions").EnumerateArray())
            {
                Assert.True(slugs.Add(pair.GetProperty("slug").GetString()!), "Conversion URLs must not collide with action URLs.");
                string input = pair.GetProperty("inputFormat").GetString()!;
                var extensions = (IReadOnlyList<string>)action.GetType().GetProperty("InputExtensions")!.GetValue(action)!;
                Assert.Contains(input == "jpeg" ? ".jpg" : "." + input, extensions);
                foreach (var option in pair.GetProperty("parameters").EnumerateObject())
                {
                    var parameter = Assert.Single(action.Descriptor.Parameters!, p => p.Name == option.Name);
                    Assert.Contains(parameter.Choices!, c => c.Value == option.Value.GetString());
                }
                foreach (string locale in new[] { "en", "de" })
                    Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("requirements").GetProperty(locale).GetString()));
            }
        }
    }
}
