using PowerTrove.Application.Configuration;
using PowerTrove.Core.Catalog;

namespace PowerTrove.Application.Tests;

public sealed class ActionCatalogFileTests
{
    [Fact]
    public async Task AbsentFile_IsOptional_AndBadMetadataIsSafelyIgnored()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "catalog.json");
            var absent = await ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
            Assert.Empty(absent.Catalog.Entries); Assert.Null(absent.Warning);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":\"1\",\"packs\":[]}");
            var valid = await ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
            Assert.Empty(valid.Catalog.Entries); Assert.Null(valid.Warning);
            await File.WriteAllTextAsync(path, "{synthetic private malformed value");
            var invalid = await ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
            Assert.Empty(invalid.Catalog.Entries); Assert.False(string.IsNullOrWhiteSpace(invalid.Warning));
            Assert.DoesNotContain("synthetic", invalid.Warning!); Assert.DoesNotContain(directory, invalid.Warning!);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OversizedOrInvalidUtf8File_IsRejected_AndCancellationPropagates()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[ActionCatalog.MaximumJsonBytes + 1]);
            var oversized = await ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
            Assert.NotNull(oversized.Warning); Assert.Empty(oversized.Catalog.Entries);
            await File.WriteAllBytesAsync(path, new byte[] { 0xFF, 0xFE });
            var invalid = await ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0));
            Assert.NotNull(invalid.Warning);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ActionCatalogFile.LoadAsync(path, Array.Empty<ActionPackInfo>(), new Version(0, 1, 0), new CancellationToken(true)));
        }
        finally { File.Delete(path); }
    }
}
