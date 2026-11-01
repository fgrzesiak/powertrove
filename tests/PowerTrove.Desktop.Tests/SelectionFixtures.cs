using System.Text;
using PowerTrove.Core;
using PowerTrove.Desktop;

namespace PowerTrove.Desktop.Tests;

/// <summary>
/// Real files with valid signatures for the full built-in catalog, so matching runs the production
/// type verification. Generated in code; the directory is deleted on dispose.
/// </summary>
internal sealed class SelectionFixtures : IDisposable
{
    private static readonly Dictionary<string, byte[]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0],
        [".jpg"] = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0],
        [".pdf"] = Encoding.ASCII.GetBytes("%PDF-1.7\n%\n"),
        [".mp4"] = [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 2, 0, 0x69, 0x73, 0x6F, 0x6D, 0x6D, 0x70, 0x34, 0x31],
        [".mp3"] = Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0"),
        [".zip"] = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [".csv"] = Encoding.UTF8.GetBytes("a,b\n1,2\n"),
        [".json"] = Encoding.UTF8.GetBytes("[{\"a\":1}]"),
        [".txt"] = Encoding.UTF8.GetBytes("hello\n"),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("powertrove-selection-").FullName;

    public ActionRegistry Registry { get; } = new();

    public SelectionFixtures()
    {
        var runner = new PowerTrove.Core.Workflows.WorkflowRunner(Registry);
        foreach (var pack in BuiltInActionPacks.Create(runner)) Registry.RegisterPack(pack.Info.Id, pack.Actions);
    }

    /// <summary>A File Explorer selection of the named files, for example "a.jpg", "b.pdf".</summary>
    public ContextSnapshot Explorer(params string[] names)
    {
        var files = names.Select(name =>
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, Signatures[Path.GetExtension(name)]);
            return new SelectedFile(path, name, Path.GetExtension(name), null, new FileInfo(path).Length, true, true, null);
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        return new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "explorer", now), ContextStatus.Available,
            new FileSelection(files), now, now.AddMinutes(5), "powertrove.provider.explorer", null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
