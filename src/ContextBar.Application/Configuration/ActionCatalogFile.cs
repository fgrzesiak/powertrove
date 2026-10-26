using System.Text;
using ContextBar.Abstractions;
using ContextBar.Core.Catalog;

namespace ContextBar.Application.Configuration;

public sealed record ActionCatalogLoadResult(IActionCatalog Catalog, string? Warning = null);

/// <summary>Optional, bounded local metadata. No network access or pack execution.</summary>
public static class ActionCatalogFile
{
    public static async Task<ActionCatalogLoadResult> LoadAsync(string path, IEnumerable<ActionPackInfo> installedPacks,
        Version hostVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Also bound reads when a file grows after opening it.
            var buffer = new byte[ActionCatalog.MaximumJsonBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
            if (total > ActionCatalog.MaximumJsonBytes) throw new CatalogFormatException();
            cancellationToken.ThrowIfCancellationRequested();
            int offset = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            string json = new UTF8Encoding(false, true).GetString(buffer, offset, total - offset);
            return new(ActionCatalog.Read(json, installedPacks, hostVersion));
        }
        catch (FileNotFoundException)
        {
            return new(ActionCatalog.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or CatalogFormatException)
        {
            // Do not expose paths, raw metadata or exception text in the palette.
            return new(ActionCatalog.Empty, ContextBar.Core.Resources.Strings.CatalogLoadFailed);
        }
    }
}
