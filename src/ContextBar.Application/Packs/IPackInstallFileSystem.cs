namespace ContextBar.Application.Packs;

/// <summary>Injectable local IO; atomic replace must preserve the old target if it fails.</summary>
public interface IPackInstallFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> Directories(string path);
    void CheckSafePath(string path);
    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void CreateDirectory(string path);
    void MoveDirectory(string source, string target);
    void ActivateFile(string temporary, string target);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

public sealed class PhysicalPackInstallFileSystem : IPackInstallFileSystem
{
    public void CheckSafePath(string path) => PackOutputCommitter.RejectReparse(PackOutputCommitter.Canonical(path));
    public bool FileExists(string path) { CheckSafePath(path); return File.Exists(path); }
    public bool DirectoryExists(string path) { CheckSafePath(path); return Directory.Exists(path); }
    public IReadOnlyList<string> Directories(string path) { CheckSafePath(path); return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
    public Stream OpenRead(string path) { CheckSafePath(path); return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); }
    public Stream CreateNew(string path) { CheckSafePath(path); return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); }
    public void CreateDirectory(string path) { CheckSafePath(path); Directory.CreateDirectory(path); }
    public void MoveDirectory(string source, string target) { CheckSafePath(source); CheckSafePath(target); Directory.Move(source, target); }
    public void ActivateFile(string temporary, string target)
    {
        CheckSafePath(temporary); CheckSafePath(target);
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target, overwrite: false);
    }
    public void DeleteFile(string path) { CheckSafePath(path); File.Delete(path); }
    public void DeleteDirectory(string path)
    {
        CheckSafePath(path);
        if (!Directory.Exists(path)) return;
        var pending = new Stack<(string Path, bool Visited)>(); pending.Push((path, false));
        while (pending.TryPop(out var item))
        {
            var attributes = File.GetAttributes(item.Path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(item.Path); else File.Delete(item.Path); }
            else if ((attributes & FileAttributes.Directory) == 0)
            { if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly); File.Delete(item.Path); }
            else if (item.Visited) Directory.Delete(item.Path);
            else { pending.Push((item.Path, true)); foreach (string child in Directory.EnumerateFileSystemEntries(item.Path)) pending.Push((child, false)); }
        }
    }
}
