using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerTrove.Infrastructure.Files;

namespace PowerTrove.Pack.Files.Tests;

public sealed class ExtendedFileToolsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new FileSignatureVerifier());
    private static Task<ActionReceipt> Run(IAction action, string[] paths, params (string Key, string Value)[] options) => action.ExecuteAsync(Snapshots.ForPaths(paths), Snapshots.Parameters(options), null, CancellationToken.None);
    [Fact]
    public async Task TransactionalWriterRequiresExplicitOptInForEmptyFiles()
    {
        var services = Services(); var writer = new FileOutputWriter(services.FileSystem, services.Planner);
        await Assert.ThrowsAsync<FileProcessingException>(() => writer.WriteFileAsync(_dir.Path, "empty", ".txt", (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Empty(_dir.NewFiles());
        string file = await writer.WriteFileAsync(_dir.Path, "empty", ".txt", (_, _) => Task.CompletedTask, CancellationToken.None, allowEmpty: true);
        Assert.Equal(0, new FileInfo(file).Length); Assert.Equal(file, Assert.Single(writer.Outputs));
    }
    [Fact]
    public async Task Base64WhitespaceAndSha512GeneratedManifestsAreSupported()
    {
        string encoded = _dir.Write("value.b64", " Y W\tJj\r\n");
        var decoded = await Run(new Base64Action(Services(), true), new[] { encoded }); Assert.Equal("abc", File.ReadAllText(Assert.Single(decoded.OutputPaths)));
        string input = _dir.Write("hello.txt", "hello"); var created = await Run(new ChecksumAction(Services()), new[] { input }, ("Algorithm", "sha512"));
        var verified = await Run(new VerifyChecksumsAction(Services()), new[] { Assert.Single(created.OutputPaths) }); Assert.Equal("1 OK, 0 mismatched, 0 missing.", verified.Message);
    }
    [Theory]
    [InlineData("lines")][InlineData("quoted")][InlineData("semicolon")][InlineData("json-array")]
    public async Task CopyFormattingContainsNamesOnlyOnClipboard(string format)
    {
        string p = _dir.Write("private ä.txt", "x");
        var result = await Run(new CopySelectionAction(false), new[] { p }, ("Format", format));
        Assert.True(result.IsSuccess); Assert.DoesNotContain(p, result.Message); Assert.Empty(result.OutputPaths);
        if (format == "json-array") Assert.Equal(p, JsonSerializer.Deserialize<string[]>(result.ClipboardText!)![0]);
        else if (format == "quoted") Assert.Equal("\"" + p + "\"", result.ClipboardText);
        else Assert.Equal(p, result.ClipboardText);
        var names = await Run(new CopySelectionAction(true), new[] { p }); Assert.Equal("private ä.txt", names.ClipboardText);
    }
    [Fact]
    public async Task CopyEscapesQuotesAndRejectsOversizedSelection()
    {
        var selected = new SelectedFile("C:\\a\"b\\", "a\"b", ".txt", null, 0, true, true, null);
        var context = Snapshots.ForPaths("unused") with { Content = new FileSelection(new[] { selected }) };
        var action = new CopySelectionAction(false);
        var result = await action.ExecuteAsync(context, Snapshots.Parameters(("Format", "quoted")), null, CancellationToken.None);
        Assert.Equal("\"C:\\a\\\"b\\\\\"", result.ClipboardText);
        var huge = context with { Content = new FileSelection(Enumerable.Repeat(selected, 10001).ToArray()) };
        Assert.False((await action.MatchAsync(huge, CancellationToken.None)).IsAvailable);
    }
    [Fact]
    public async Task DuplicateReportGroupsNestedAndEmptyFilesWithoutDeletingAnything()
    {
        string folder = Directory.CreateDirectory(_dir.File("source")).FullName;
        _dir.Write("source/a.txt", "same"); _dir.Write("source/nested/b.txt", "same"); _dir.Write("source/unique.txt", "different");
        _dir.Write("source/empty-a.txt", ""); _dir.Write("source/empty-b.txt", "");
        var receipt = await Run(new ScanReportAction(Services(), true), new[] { folder }); Assert.True(receipt.IsSuccess);
        string report = File.ReadAllText(Assert.Single(receipt.OutputPaths)); Assert.Contains("No files have been deleted", report); Assert.DoesNotContain("unique.txt", report); Assert.Contains("2 duplicate groups", receipt.Message); Assert.DoesNotContain("a.txt", receipt.Message);
        Assert.Equal(5, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(receipt.OutputPaths[0])[..3]);
    }
    [Fact]
    public async Task FolderReportTotalsAndFamiliesMatchTree()
    {
        string folder = Directory.CreateDirectory(_dir.File("source")).FullName; _dir.Write("source/a.txt", "abc"); _dir.Write("source/deep/a.csv", "xy");
        var receipt = await Run(new ScanReportAction(Services(), false), new[] { folder }); string report = File.ReadAllText(Assert.Single(receipt.OutputPaths));
        Assert.Contains("Total bytes,5", report); Assert.Contains("\"text\",1,3", report); Assert.Contains("\"spreadsheet\",1,2", report); Assert.Contains("2 files, 5 bytes", receipt.Message); Assert.DoesNotContain(folder, receipt.Message);
    }
    private sealed class LimitedScanner : IDirectoryScanner
    {
        private readonly string _path; public LimitedScanner(string path) => _path = path;
        public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false) => new(new[] { _path }, true, 1) { SkippedDirectories = 2 };
    }
    [Fact]
    public async Task ReportDisclosesPartialScanAndSkippedFolders()
    {
        string p = _dir.Write("source/a.txt", "abc"); var result = await Run(new ScanReportAction(Services(), false, new LimitedScanner(p)), new[] { Path.GetDirectoryName(p)! });
        Assert.Contains("partial", result.Message); Assert.Contains("2 skipped", result.Message);
    }
    private sealed class CancelScanner(CancellationTokenSource cts) : IDirectoryScanner
    {
        public DirectoryScanResult EnumerateFiles(string path, int maxDepth, int maxItems, bool followSymlinks = false) { cts.Cancel(); return new(Array.Empty<string>(), false, 0); }
    }
    [Fact]
    public async Task CanceledReportLeavesNoOutput()
    {
        using var cts = new CancellationTokenSource();
        var action = new ScanReportAction(Services(), true, new CancelScanner(cts));
        var result = await action.ExecuteAsync(Snapshots.ForPaths(_dir.Path), new EmptyParameters(), null, cts.Token);
        Assert.True(result.IsCanceled); Assert.Empty(result.OutputPaths); Assert.Empty(_dir.NewFiles());
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(1048575)][InlineData(1048576)][InlineData(1048577)]
    public async Task SplitJoinRoundTripsBoundaries(int size)
    {
        byte[] data = Enumerable.Range(0, size).Select(i => (byte)(i * 17)).ToArray(); string p = _dir.Write("payload.bin", data);
        var split = await Run(new SplitFileAction(Services()), new[] { p }, ("PartSizeMb", "1")); string directory = Assert.Single(split.OutputPaths);
        var joined = await Run(new JoinFileAction(Services()), new[] { Path.Combine(directory, "payload.bin.001") }); Assert.True(joined.IsSuccess); Assert.Equal(data, File.ReadAllBytes(Assert.Single(joined.OutputPaths))); Assert.Equal(data, File.ReadAllBytes(p));
    }
    [Fact]
    public async Task MissingPartsAndBadHashNeverCommitOutput()
    {
        string p = _dir.Write("source.bin", new byte[30]); var split = await Run(new SplitFileAction(Services()), new[] { p }, ("Mode", "parts"), ("Parts", "3")); string folder = Assert.Single(split.OutputPaths);
        File.Delete(Path.Combine(folder, "source.bin.002"));
        var missing = await Run(new JoinFileAction(Services()), new[] { Path.Combine(folder, "source.bin.001") }); Assert.False(missing.IsSuccess); Assert.Empty(missing.OutputPaths);
        File.WriteAllBytes(Path.Combine(folder, "source.bin.002"), new byte[10]); File.WriteAllText(Path.Combine(folder, "source.bin.sha256"), new string('0', 64) + " *source.bin\n");
        var bad = await Run(new JoinFileAction(Services()), new[] { Path.Combine(folder, "source.bin.001") }); Assert.False(bad.IsSuccess); Assert.Empty(bad.OutputPaths); Assert.False(File.Exists(Path.Combine(folder, "source.bin")));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(57)][InlineData(58)][InlineData(1000)]
    public async Task Base64RoundTripUses76ColumnLines(int size)
    {
        byte[] data = Enumerable.Range(0, size).Select(i => (byte)i).ToArray(); string p = _dir.Write("data.bin", data);
        var encoded = await Run(new Base64Action(Services(), false), new[] { p }); string file = Assert.Single(encoded.OutputPaths);
        Assert.All(File.ReadAllLines(file), line => Assert.InRange(line.Length, 1, 76));
        var decoded = await Run(new Base64Action(Services(), true), new[] { file }); Assert.Equal(data, File.ReadAllBytes(Assert.Single(decoded.OutputPaths)));
    }
    [Theory]
    [InlineData("!!!!")][InlineData("YQ==Yg==")][InlineData("YQ=")][InlineData("AA=A")]
    public async Task InvalidBase64DoesNotLeaveOutput(string value)
    {
        string p = _dir.Write("a.b64", value); var result = await Run(new Base64Action(Services(), true), new[] { p }); Assert.False(result.IsSuccess); Assert.Empty(result.OutputPaths); Assert.Empty(_dir.NewFiles(p));
    }
    [Fact]
    public async Task VerificationCountsRelativeMissingAndMismatchedFilesWithoutLeakingPaths()
    {
        string a = _dir.Write("a.txt", "a"), b = _dir.Write("b.txt", "b");
        string good = Convert.ToHexString(SHA256.HashData("a"u8));
        string manifest = _dir.Write("test.sha256", good + " *a.txt\n" + new string('0', 64) + " *b.txt\n" + good + " *missing.txt\n");
        var receipt = await Run(new VerifyChecksumsAction(Services()), new[] { manifest }); Assert.True(receipt.IsSuccess); Assert.Equal("1 OK, 1 mismatched, 1 missing.", receipt.Message); Assert.Empty(receipt.OutputPaths); Assert.DoesNotContain(a, receipt.Message);
        var file = await Run(new VerifyChecksumsAction(Services()), new[] { manifest }, ("Output", "file")); string report = File.ReadAllText(Assert.Single(file.OutputPaths)); Assert.Contains("a.txt", report); Assert.Contains("b.txt", report);
    }
    [Fact]
    public async Task TarActionsRoundTripFolderAndEmptyDirectory()
    {
        string root = Directory.CreateDirectory(_dir.File("source")).FullName; Directory.CreateDirectory(Path.Combine(root, "empty")); File.WriteAllText(Path.Combine(root, "note.txt"), "hello");
        var archive = await Run(new CompressToTarAction(Services()), new[] { root }); var extracted = await Run(new ExtractTarAction(Services()), new[] { Assert.Single(archive.OutputPaths) });
        Assert.True(extracted.IsSuccess); string folder = Assert.Single(extracted.OutputPaths); Assert.Equal("hello", File.ReadAllText(Path.Combine(folder, "source", "note.txt"))); Assert.True(Directory.Exists(Path.Combine(folder, "source", "empty")));
    }
}

public abstract class ExtendedFileContract : ActionContractTestsBase
{
    protected abstract string Id { get; }
    protected override IAction CreateAction() => new FilesPack(FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier())).Actions.Single(a => a.Descriptor.Id == "contextbar.files." + Id);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(Path.Combine(Path.GetTempPath(), "fixture.txt"));
}
public sealed class CopyContract : ExtendedFileContract { protected override string Id => "copy-paths"; }
public sealed class ReportContract : ExtendedFileContract { protected override string Id => "folder-report"; }
public sealed class DuplicateContract : ExtendedFileContract { protected override string Id => "find-duplicates"; }
public sealed class SplitContract : ExtendedFileContract { protected override string Id => "split"; }
public sealed class JoinContract : ExtendedFileContract { protected override string Id => "join"; }
public sealed class Base64Contract : ExtendedFileContract { protected override string Id => "base64-encode"; }
public sealed class VerifyContract : ExtendedFileContract { protected override string Id => "verify-checksums"; }
public sealed class TarContract : ExtendedFileContract { protected override string Id => "tar"; }
public sealed class UntarContract : ExtendedFileContract { protected override string Id => "untar"; }
