using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PowerTrove.Architecture.Tests;

public class SecurityRulesTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Application_NeverReadsClipboard()
    {
        var srcFolder = Path.Combine(Root, "src");
        var allCsFiles = Directory.GetFiles(srcFolder, "*.cs", SearchOption.AllDirectories);

        foreach (var file in allCsFiles)
        {
            var content = File.ReadAllText(file);

            // Ensure we are not silently modifying user's clipboard unless specified
            // And more importantly, the specs state "without ever reading the clipboard"
            Assert.False(content.Contains("Clipboard.GetContent", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("Clipboard.GetText", StringComparison.OrdinalIgnoreCase),
                $"File {file} uses a clipboard read API. PowerTrove must never read the clipboard.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PowerTrove.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
