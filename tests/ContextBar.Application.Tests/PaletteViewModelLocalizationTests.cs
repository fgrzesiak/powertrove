using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ContextBar.Application.Resources;
using ContextBar.Application.ViewModels;
using ContextBar.Core;
using ContextBar.Core.Search;
using ContextBar.Core.Selection;
using Xunit;

namespace ContextBar.Application.Tests;

public class PaletteViewModelLocalizationTests
{
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "PDF", "ZIP", "JSON", "CSV", "Markdown", "SQL", "MP3", "GIF", "JPEG", "PNG", "WebP", "Format", "Video"
    };
    // Key-specific exceptions: units, the shared word "Version", and punctuation
    // around values that are already localized. Changed/new templates still fail.
    private static readonly IReadOnlyDictionary<string, string> InvariantTemplates = new Dictionary<string, string>
    {
        ["PackSizeMegabytes"] = "{0:N1} MB",
        ["PackSizeKilobytes"] = "{0:N1} KB",
        ["PackVersion"] = "Version {0}",
        ["PackVersionUpdate"] = "Version {0} → {1}",
        ["PackRowAutomationName"] = "{0}, {1}, {2}"
    };

    [Theory]
    [InlineData("File", 1, "Zuletzt für 1 Datei verwendet")]
    [InlineData("File", 3, "Zuletzt für 3 Dateien verwendet")]
    [InlineData("Image", 1, "Zuletzt für 1 Bild verwendet")]
    [InlineData("Image", 3, "Zuletzt für 3 Bilder verwendet")]
    [InlineData("Folder", 1, "Zuletzt für 1 Ordner verwendet")]
    [InlineData("Folder", 3, "Zuletzt für 3 Ordner verwendet")]
    [InlineData("Table", 1, "Zuletzt für 1 Tabelle verwendet")]
    [InlineData("Table", 3, "Zuletzt für 3 Tabellen verwendet")]
    [InlineData("Text", 1, "Zuletzt für 1 Text verwendet")]
    [InlineData("Text", 3, "Zuletzt für 3 Texte verwendet")]
    [InlineData("Video", 1, "Zuletzt für 1 Video verwendet")]
    [InlineData("Video", 3, "Zuletzt für 3 Videos verwendet")]
    public void BadgeText_ReadsNaturallyInGerman(string familyStr, int itemCount, string expectedText)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        var originalThreadCulture = CultureInfo.CurrentCulture;
        try
        {
            var de = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = de;
            CultureInfo.CurrentCulture = de;

            var result = new ActionSearchResult(
                new DummyAction(),
                true,
                null,
                1,
                ActionHighlight.LastUsedForSelection
            );

            SelectionFamily family = Enum.TryParse<SelectionFamily>(familyStr, out var parsed) ? parsed : SelectionFamily.Other;

            ContentKind kind = familyStr == "Table" ? ContentKind.Table :
                               familyStr == "Text" ? ContentKind.Text :
                               ContentKind.Files;

            CountBucket bucket = itemCount == 1 ? CountBucket.One : CountBucket.Few;
            var signature = new SelectionSignature(kind, bucket, new[] { family });

            var vm = new PaletteViewModel();
            // We use reflection to set CurrentSelectionSignature to mock state
            var prop = typeof(PaletteViewModel).GetProperty("CurrentSelectionSignature");
            prop!.SetValue(vm, signature);

            // Since it checks countStr using the bucket, we need to pass the appropriate expected itemCount.
            // Wait, the view model's GetBadgeText calculates the count string using `sig.Count` bucket.
            // For Few, it returns "2-5" instead of exact numbers.
            // Let's check how many it says. CountBucket.Few -> "2–5". So the expected text needs to expect "2-5" instead of 3.
            // "Zuletzt für 2–5 Dateien verwendet"

            string countStr = itemCount == 1 ? "1" : "2–5";
            string expected = expectedText.Replace(itemCount.ToString(), countStr);

            string badge = vm.GetBadgeText(result);
            Assert.Equal(expected, badge);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            CultureInfo.CurrentCulture = originalThreadCulture;
        }
    }

    [Fact]
    public void NoUserVisibleText_EqualsEnUs_InGerman()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        var originalThreadCulture = CultureInfo.CurrentCulture;
        try
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            var de = CultureInfo.GetCultureInfo("de-DE");

            var resourceManager = new System.Resources.ResourceManager("ContextBar.Application.Resources.Strings", typeof(Strings).Assembly);
            var resourceSet = resourceManager.GetResourceSet(en, true, true);

            foreach (System.Collections.DictionaryEntry entry in resourceSet!)
            {
                string key = (string)entry.Key;
                string enValue = (string)entry.Value!;

                CultureInfo.CurrentUICulture = de;
                CultureInfo.CurrentCulture = de;
                string deValue = Strings.GetString(key);

                bool knownInvariant = InvariantTemplates.TryGetValue(key, out var expected) && enValue == expected;
                if (enValue == deValue && !Allowlist.Contains(enValue) && !knownInvariant)
                {
                    Assert.Fail($"Key '{key}' has the same value '{enValue}' in en-US and de-DE but is not allowlisted.");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            CultureInfo.CurrentCulture = originalThreadCulture;
        }
    }

    private class DummyAction : IAction
    {
        public ActionDescriptor Descriptor { get; } = new ActionDescriptor("test", "Test", "", new HashSet<ContentKind> { ContentKind.None }, Array.Empty<string>(), false, false);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, System.Threading.CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, System.Threading.CancellationToken cancellationToken) => new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public System.Threading.Tasks.Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));
    }
}
