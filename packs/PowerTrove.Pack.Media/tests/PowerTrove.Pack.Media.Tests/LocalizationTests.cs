using System.Globalization;

namespace PowerTrove.Pack.Media.Tests;

public class LocalizationTests
{
    private static FileActionServices Services() => FileActionServices.CreatePhysical(new ExtensionOnlyTypeVerifier());

    [Fact]
    public void EveryMediaAction_HasLocalizedMetadata()
    {
        var problems = LocalizedActionCatalog.FindProblems(() => new IActionPack[] { new MediaPack(new([new Engine()]), Services(), new Inspector()) });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task EngineErrors_NeverExposeRawTextInReceipts(int kind)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            string source = directory.Write("PRIVATE.wav", "input");
            var action = MediaAction.ConvertAudio(MediaTargets.Mp3, new([new Engine { Error = Failure(kind) }]), Services());
            var receipt = await action.ExecuteAsync(Snapshots.ForPaths(source), Snapshots.Parameters(), null, default);
            Assert.False(receipt.IsSuccess);
            Assert.Contains("Medien", Assert.Single(receipt.Failures).ErrorMessage);
            Assert.DoesNotContain("PRIVATE", receipt.Failures[0].ErrorMessage);
            Assert.Empty(receipt.OutputPaths);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task CompositeEngineAndInspectorErrors_AreMappedInPreviewAndExecution()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            string path = directory.Write("PRIVATE.mp4", "input");
            var context = Snapshots.ForPaths(path);
            var inspector = new Inspector { Error = new FileProcessingException("PRIVATE inspector output") };
            var gif = new MediaToolAction("to-gif", new([new Engine()]), inspector, Services());
            var preview = await gif.PreviewAsync(context, Snapshots.Parameters(), default);
            Assert.True(preview.IsBlocked);
            Assert.Equal("Die Medieneigenschaften konnten nicht gelesen werden oder überschreiten die unterstützten Grenzen.", preview.PreviewText);
            var infoReceipt = await new MediaInfoAction(inspector, Services()).ExecuteAsync(context, Snapshots.Parameters(), null, default);
            Assert.DoesNotContain("PRIVATE", Assert.Single(infoReceipt.Failures).ErrorMessage);
            var trim = new MediaToolAction("trim", new([new Engine { Error = Failure(0) }]), new Inspector(), Services());
            var receipt = await trim.ExecuteAsync(context, Snapshots.Parameters(), null, default);
            Assert.Equal("Die lokale Medien-Engine konnte diese Datei nicht verarbeiten.", Assert.Single(receipt.Failures).ErrorMessage);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("fatal PRIVATE selected file and metadata")]
    [InlineData("matches no streams PRIVATE selected file")]
    public async Task FfmpegFailures_UseResourcesRatherThanToolOutput(string output)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var exception = await Assert.ThrowsAsync<FileProcessingException>(() => new FFmpegMediaEngine(new Service(output))
                .ProcessAsync("PRIVATE.wav", "PRIVATE.mp3", new(MediaOperation.ConvertAudio, MediaTargets.Mp3, ".wav"), default));
            Assert.DoesNotContain("PRIVATE", exception.Message);
            Assert.True(exception.Message.StartsWith("Die ", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task ConversionPreviewAndWarnings_AreGermanAndUsePluralForms()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            using var directory = new TempDirectory();
            string first = directory.Write("first.flac", "input"), second = directory.Write("second.flac", "input");
            var action = MediaAction.ConvertAudio(MediaTargets.Mp3, new([new Engine()]), Services());
            var one = await action.PreviewAsync(Snapshots.ForPaths(first), Snapshots.Parameters(), default);
            var many = await action.PreviewAsync(Snapshots.ForPaths(first, second), Snapshots.Parameters(), default);
            Assert.Contains("1 Audiodatei", one.PreviewText);
            Assert.Contains("2 Audiodateien", many.PreviewText);
            Assert.Contains(one.Warnings, w => w.Contains("Audioqualität", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void TechnicalDisplay_UsesRegionalNumbers_WithoutChangingCommandNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Contains("29,97", MediaTechnical.Format(new(TimeSpan.FromSeconds(1), 640, 360, 29.97)));
            Assert.Equal("29.97", MediaTechnical.Number(29.97));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static Exception Failure(int kind) => kind switch
    {
        0 => new FileProcessingException("PRIVATE engine details"), 1 => new IOException("PRIVATE filesystem details"),
        2 => new InvalidOperationException("PRIVATE internal details"), 3 => new MediaEngineUnsupportedException(new Exception("PRIVATE codec details")),
        _ => new OperationCanceledException("PRIVATE unsolicited cancellation")
    };
    private sealed class Engine : IMediaEngine
    {
        public Exception? Error { get; init; }
        public string Name => "test";
        public bool IsAvailable => true;
        public bool Supports(MediaRequest request) => true;
        public Task ProcessAsync(string source, string destination, MediaRequest request, CancellationToken ct) =>
            Error is null ? Task.CompletedTask : Task.FromException(Error);
    }
    private sealed class Inspector : IMediaInspector
    {
        public Exception? Error { get; init; }
        public bool IsAvailable => true;
        public Task<MediaDetails> InspectAsync(string path, CancellationToken ct) => Error is null
            ? Task.FromResult(new MediaDetails(TimeSpan.FromSeconds(60), 640, 360, 30, "h264", "aac", HasAudio: true)) : Task.FromException<MediaDetails>(Error);
    }
    private sealed class Service(string error) : IFFmpegService
    {
        public bool IsAvailable => true;
        public string? ExecutablePath => null;
        public Task<string?> GetVersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<FFmpegResult> ExecuteAsync(IEnumerable<string> args, CancellationToken ct) => Task.FromResult(new FFmpegResult(false, "", error, 1));
    }
}
