namespace ContextBar.Pack.Files;

public interface ICancellableDirectoryScanner : IDirectoryScanner
{
    DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks, CancellationToken cancellationToken);
}

public sealed class ReportScanner : ICancellableDirectoryScanner
{
    public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false) => EnumerateFiles(path, maxDepth, maxItems, followSymlinks, CancellationToken.None);
    public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxDepth < 0 || maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var files = new List<string>(); int skipped = 0, found = 0; bool limited = false, truncated = false;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((path, 0));
        // Reports deliberately never follow reparse points, even if a caller requests it.
        while (pending.TryPop(out var directory) && !limited)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (FileTools.IsLink(directory.Path)) continue;
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (directory.Depth == maxDepth) { skipped++; truncated = true; continue; }
                            if (pending.Count >= maxItems) { limited = true; break; }
                            pending.Push((entry, directory.Depth + 1));
                        }
                        else { found++; if (files.Count == maxItems) { limited = true; break; } files.Add(entry); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(files, limited || truncated, found) { SkippedDirectories = skipped };
    }
}
