using PowerTrove.Abstractions.Files;
using Xunit;

namespace PowerTrove.Core.Tests;

public sealed class RenameUndoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cb-rename-" + Guid.NewGuid().ToString("N"));
    public RenameUndoTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string Write(string name, string content) { string path = Path.Combine(_directory, name); File.WriteAllText(path, content); return path; }
    [Fact]
    public async Task SwapsAndUndoRestoreNamesAndContent()
    {
        string a = Write("a.txt", "A"), b = Write("b.txt", "B"); var service = new UndoService(new PhysicalOutputFileSystem());
        var receipt = await service.RenameAsync([new(a, b), new(b, a)], default);
        Assert.Equal("B", File.ReadAllText(a)); Assert.Equal("A", File.ReadAllText(b));
        Assert.True(await service.CanUndoAsync(receipt.OperationId, default)); Assert.True((await service.UndoAsync(receipt.OperationId, default)).Success);
        Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b)); Assert.Empty(service.GetHistory()); Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task FailureAtEveryMoveRollsBackEntireSwap(int failAt)
    {
        string a = Write("a.txt", "A"), b = Write("b.txt", "B"); var fs = new FaultFileSystem(failAt); var service = new UndoService(fs);
        await Assert.ThrowsAsync<IOException>(() => service.RenameAsync([new(a, b), new(b, a)], default));
        Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b)); Assert.Empty(service.GetHistory()); Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
    [Fact]
    public async Task UndoRefusesModifiedContentOccupiedNamesAndExpiredJournal()
    {
        string a = Write("a.txt", "A"), b = Path.Combine(_directory, "b.txt"); var clock = new MutableClock(); var service = new UndoService(new PhysicalOutputFileSystem(), clock);
        var receipt = await service.RenameAsync([new(a, b)], default);
        File.WriteAllText(b, "changed"); Assert.False(await service.CanUndoAsync(receipt.OperationId, default));
        File.WriteAllText(b, "A"); Write("a.txt", "occupied"); Assert.False((await service.UndoAsync(receipt.OperationId, default)).Success);
        File.Delete(a); clock.Now += TimeSpan.FromMinutes(11); Assert.False(await service.CanUndoAsync(receipt.OperationId, default)); Assert.Equal("A", File.ReadAllText(b));
    }
    [Fact]
    public async Task CancellationAfterStagingStillRollsBack()
    {
        string a = Write("a.txt", "A"), b = Path.Combine(_directory, "b.txt"); using var cts = new CancellationTokenSource(); var fs = new FaultFileSystem(0, cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UndoService(fs).RenameAsync([new(a, b)], cts.Token));
        Assert.True(File.Exists(a)); Assert.False(File.Exists(b)); Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact]
    public async Task WindowsLockedInputAndCaseOnlyRename()
    {
        if (!OperatingSystem.IsWindows()) return; // Requires actual NTFS sharing semantics.
        string a = Write("a.txt", "A"), b = Write("b.txt", "B"); var service = new UndoService(new PhysicalOutputFileSystem());
        using (var locked = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => service.RenameAsync([new(a, b), new(b, a)], default));
        Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b));
        var rename = await service.RenameAsync([new(a, Path.Combine(_directory, "A.txt"))], default);
        Assert.Contains("A.txt", Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.True((await service.UndoAsync(rename.OperationId, default)).Success);
        Assert.Contains("a.txt", Directory.GetFiles(_directory).Select(Path.GetFileName));
    }
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FaultFileSystem(int failAt, CancellationTokenSource? cancel = null) : IOutputFileSystem
    {
        private readonly PhysicalOutputFileSystem _inner = new(); private int _moves;
        public bool FileExists(string p) => _inner.FileExists(p); public bool DirectoryExists(string p) => _inner.DirectoryExists(p);
        public void CreateDirectory(string p) => _inner.CreateDirectory(p); public void DeleteFile(string p) => _inner.DeleteFile(p);
        public Stream OpenRead(string p) => _inner.OpenRead(p); public Stream CreateFile(string p) => _inner.CreateFile(p); public long GetFileLength(string p) => _inner.GetFileLength(p);
        public void MoveFile(string from, string to) { if (++_moves == failAt) throw new IOException("Simulated lock."); _inner.MoveFile(from, to); if (_moves == 1) cancel?.Cancel(); }
        public void MoveDirectory(string from, string to) => _inner.MoveDirectory(from, to); public void DeleteDirectory(string p) => _inner.DeleteDirectory(p);
    }
}
