namespace PowerTrove.Pack.Files.Tests;

public sealed class RenameTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();
    private static RenameOptions Options(string pattern = "{name}", string find = "", string replace = "", string casing = "keep", bool keep = true) => new(pattern, find, replace, casing, 7, keep, "");
    [Theory]
    [InlineData("{name}", "Photo.jpg")][InlineData("{n:000}-{name}", "007-Photo.jpg")]
    [InlineData("{n}-{ext}", "7-jpg")][InlineData("{date}", "2024-02-03.jpg")]
    [InlineData("{exifdate}", "2020-01-02.jpg")][InlineData("{name}.{ext}", "Photo.jpg")]
    public void EveryTokenAndPadding(string pattern, string expected) => Assert.Equal(expected, RenameNames.Generate("Photo.jpg", Options(pattern), 0, new(2024, 2, 3), new(2020, 1, 2)));
    [Theory]
    [InlineData("keep", "my photo.jpg")][InlineData("lower", "my photo.jpg")][InlineData("upper", "MY PHOTO.jpg")][InlineData("title", "My Photo.jpg")]
    public void FindReplacePrecedesPatternAndCase(string casing, string expected) => Assert.Equal(expected, RenameNames.Generate("my camera.jpg", Options(find: "camera", replace: "photo", casing: casing), 0, DateTime.Today));
    [Fact]
    public void ExtensionAndExifFallbackAndIndexAreExplicit()
    {
        Assert.Equal("Photo", RenameNames.Generate("Photo.jpg", Options(keep: false), 0, DateTime.Today));
        Assert.Equal("2024-02-03.jpg", RenameNames.Generate("Photo.jpg", Options("{exifdate}"), 0, new(2024, 2, 3)));
        Assert.Equal("008.jpg", RenameNames.Generate("Photo.jpg", Options("{n:000}"), 1, DateTime.Today));
        Assert.Throws<ArgumentException>(() => RenameNames.Generate("a.txt", Options("{unknown}"), 0, DateTime.Today));
    }
    [Theory]
    [InlineData("CON")][InlineData("nul.txt")][InlineData("COM1.csv")][InlineData("LPT².txt")][InlineData("CON .txt")]
    [InlineData("a:b")][InlineData("a/b")][InlineData("a\\b")][InlineData("a?")][InlineData("a.")][InlineData("a ")][InlineData("..")]
    public void InvalidWindowsNamesAreBlocked(string name) => Assert.False(FileRenameRules.IsValidName(name));
    [Fact]
    public async Task ConsentIsRequiredFreshAndNeverRemembered()
    {
        string file = _dir.Write("private.txt", "x"); var service = new CapturingRenamer(); var fs = new PhysicalOutputFileSystem();
        var action = new RenameFilesAction(new(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier()), service);
        var snapshot = Snapshots.ForPaths(file); var options = Snapshots.Parameters(("Pattern", "{n:000}"));
        var preview = await action.PreviewAsync(snapshot, options, default);
        Assert.False(preview.IsBlocked); Assert.Single(preview.RenameRows!); Assert.NotNull(preview.Confirmation); Assert.False(action.Descriptor.CanRememberParameters);
        var rejected = await action.ExecuteAsync(snapshot, options, null, default); Assert.False(rejected.IsSuccess); Assert.False(service.Called);
        var values = new Dictionary<string, string>(options.Values) { [preview.Confirmation!.ParameterName] = preview.Confirmation.Token };
        File.AppendAllText(file, "changed");
        rejected = await action.ExecuteAsync(snapshot, new DictionaryParameters(values), null, default); Assert.False(rejected.IsSuccess); Assert.False(service.Called);
        preview = await action.PreviewAsync(snapshot, options, default); values[preview.Confirmation!.ParameterName] = preview.Confirmation.Token;
        var accepted = await action.ExecuteAsync(snapshot, new DictionaryParameters(values), null, default);
        Assert.True(accepted.IsSuccess); Assert.True(accepted.IsUndoable); Assert.True(service.Called); Assert.Empty(accepted.OutputPaths);
        Assert.DoesNotContain("private", accepted.Message!); Assert.DoesNotContain(file, accepted.Message!);
    }
    [Fact]
    public async Task GeneratedJpegExifDateIsUsedWithoutReadingPixels()
    {
        byte[] tiff = new byte[64];
        using (var stream = new MemoryStream(tiff))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8u);
            writer.Write((ushort)1); writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write(1u); writer.Write(26u); writer.Write(0u);
            writer.Write((ushort)1); writer.Write((ushort)0x9003); writer.Write((ushort)2); writer.Write(20u); writer.Write(44u); writer.Write(0u);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("2020:01:02 03:04:05\0"));
        }
        using var jpeg = new MemoryStream(); jpeg.Write(new byte[] { 255, 216, 255, 225, 0, 72 }); jpeg.Write("Exif\0\0"u8); jpeg.Write(tiff); jpeg.Write(new byte[] { 255, 217 });
        string path = _dir.Write("photo.jpg", jpeg.ToArray()); var fs = new PhysicalOutputFileSystem();
        var action = new RenameFilesAction(new(fs, new OutputPlanner(fs), new ExtensionOnlyTypeVerifier()), new CapturingRenamer());
        var preview = await action.PreviewAsync(Snapshots.ForPaths(path), Snapshots.Parameters(("Pattern", "{exifdate}")), default);
        Assert.False(preview.IsBlocked); Assert.Equal("2020-01-02.jpg", Assert.Single(preview.RenameRows!).NewName);
    }
    [Fact]
    public void ValidationBlocksDuplicatesAndNonSelectedCollisions()
    {
        string a = _dir.Write("a.txt", "a"), b = _dir.Write("b.txt", "b"), occupied = _dir.Write("occupied.txt", "x"); var fs = new PhysicalOutputFileSystem();
        Assert.False(FileRenameRules.IsValid([new(a, occupied)], fs));
        Assert.False(FileRenameRules.IsValid([new(a, _dir.File("same.txt")), new(b, _dir.File("SAME.txt"))], fs));
        Assert.True(FileRenameRules.IsValid([new(a, b), new(b, a)], fs));
    }
    private sealed class CapturingRenamer : IFileRenameService
    {
        public bool Called { get; private set; }
        public Task<RenameOperation> RenameAsync(IReadOnlyList<FileRename> renames, CancellationToken ct)
        { Called = true; return Task.FromResult(new RenameOperation(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10), renames.Count)); }
    }
}
