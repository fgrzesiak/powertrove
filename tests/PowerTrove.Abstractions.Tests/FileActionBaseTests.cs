using System.Text;

namespace PowerTrove.Abstractions.Tests;

public sealed class FileActionBaseTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileActionServices Services(IFileTypeVerifier? verifier = null)
    {
        var fs = new PhysicalOutputFileSystem();
        return new FileActionServices(fs, new OutputPlanner(fs), verifier ?? new ExtensionOnlyTypeVerifier());
    }

    [Theory]
    [InlineData("en-US", 1, 1, true)]
    [InlineData("en-US", 2, 1, false)]
    [InlineData("en-US", 1, 2, false)]
    [InlineData("en-US", 0, 1, false)]
    [InlineData("en-US", 1, 0, false)]
    [InlineData("de-DE", 2, 1, false)]
    [InlineData("de-DE", 1, 0, false)]
    public async Task FamilyLimits_AgreeAcrossRankingAvailabilityPreviewAndExecution(string culture, int pdfs, int images, bool available)
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
            var paths = Enumerable.Range(0, pdfs).Select(i => _dir.Write($"p{i}.pdf", "test"))
                .Concat(Enumerable.Range(0, images).Select(i => _dir.Write($"i{i}.png", "test"))).ToArray();
            var snapshot = Snapshots.ForPaths(paths);
            var action = new RoleLimitedAction(Services());

            var compatibility = ActionContextCompatibility.Check(snapshot, action.Descriptor);
            var match = await action.MatchAsync(snapshot, CancellationToken.None);
            var preview = await action.PreviewAsync(snapshot, new EmptyParameters(), CancellationToken.None);
            var receipt = await action.ExecuteAsync(snapshot, new EmptyParameters(), null, CancellationToken.None);

            Assert.Equal(available, SelectionRequirements.Evaluate(action.Descriptor.Selection!, snapshot) != SelectionFit.None);
            Assert.Equal(available, compatibility.IsAvailable);
            Assert.Equal(available, match.IsAvailable);
            Assert.Equal(!available, preview.IsBlocked);
            Assert.Equal(available, receipt.IsSuccess);
            Assert.Equal(available, action.DidRun);
            Assert.Equal(available, action.DidPlan);
            Assert.Empty(_dir.NewFiles(paths));
            if (!available)
            {
                string expected = pdfs > 1
                    ? Resources.SdkStrings.RequiresMaxItems(1, "PDF")
                    : images > 1
                        ? Resources.SdkStrings.RequiresMaxItems(1, "IMAGE")
                        : Resources.SdkStrings.RequiresMinItems(1, pdfs == 0 ? "PDF" : "IMAGE");
                Assert.Equal(expected, compatibility.Reason);
                Assert.Equal(expected, match.Reason);
                Assert.Equal(expected, preview.PreviewText);
                Assert.Equal(expected, Assert.Single(receipt.Failures).ErrorMessage);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private sealed class RoleLimitedAction : FileActionBase<int>
    {
        public RoleLimitedAction(FileActionServices services) : base(services) { }

        public bool DidRun { get; private set; }
        public bool DidPlan { get; private set; }
        public override ActionDescriptor Descriptor { get; } = new(
            "test.roles", "Roles", "Roles", new HashSet<ContentKind> { ContentKind.Files },
            Array.Empty<string>(), true, false,
            Selection: new SelectionRequirements(new HashSet<string> { "pdf", "image" },
                FamilyLimits: new Dictionary<string, FamilyLimit>
                {
                    ["PDF"] = new(1, 1),
                    ["image"] = new(1, 1)
                }));
        protected override FileInputFilter Input { get; } = new("files", new[] { ".pdf", ".png" }, null);
        protected override int ReadOptions(ActionParameterValues values) => 0;
        protected override string DescribeOperation(int inputCount, int options) => "Test roles.";
        protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, int options)
        {
            DidPlan = true;
            return Array.Empty<string>();
        }
        protected override Task RunAsync(IReadOnlyList<SelectedFile> inputs, int options, FileOutputWriter output,
            List<ActionFailure> failures, ExecutionStatistics statistics, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            DidRun = true;
            statistics.CompletedWithoutOutput = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Copies text files and upper-cases them; configurable failure and delay for tests.</summary>
    private sealed class UpperCaseAction : PerFileAction<UpperCaseAction.Options>
    {
        public sealed record Options(string Suffix);

        private static readonly FileInputFilter Filter = new("text files", new[] { ".txt" }, new[] { "text" });

        public Func<FileWorkItem, CancellationToken, Task>? BeforeWrite { get; set; }
        public bool ProduceNothing { get; set; }

        public UpperCaseAction(FileActionServices services) : base(services)
        {
            Descriptor = new ActionDescriptor("test.upper", "Upper", "Upper-cases text files.",
                new HashSet<ContentKind> { ContentKind.Files }, new[] { "upper" }, RequiresPreview: true, ChangesFiles: true,
                Parameters: new[] { ActionParameterDefinition.Choice("Suffix", "Suffix", "upper", new[] { new ActionParameterChoice("upper", "upper"), new ActionParameterChoice("big", "big") }) });
        }

        public override ActionDescriptor Descriptor { get; }
        protected override FileInputFilter Input => Filter;
        protected override bool ReportSizeChange => true;
        protected override Options ReadOptions(ActionParameterValues values) => new(values.GetChoice("Suffix"));
        protected override string DescribeOperation(int inputCount, Options options) => $"Will upper-case {inputCount} file(s).";

        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, Options options)
        {
            yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name) + "." + options.Suffix, ".txt");
        }

        protected override async Task ProcessAsync(FileWorkItem item, Options options, FileOutputWriter output, CancellationToken cancellationToken)
        {
            if (BeforeWrite != null) await BeforeWrite(item, cancellationToken);
            string text = await File.ReadAllTextAsync(item.Path, cancellationToken);
            if (text.Contains("FAIL")) throw new FileProcessingException("Content rejected.");
            if (text.Contains("SKIP")) throw new FileSkippedException();
            if (text.Contains("CRASH")) throw new InvalidOperationException("secret internal detail C:\\private\\path");
            await output.WriteFileAsync(item.Directory, item.BaseName + "." + options.Suffix, ".txt", async (stream, ct) =>
            {
                if (ProduceNothing) return;
                var bytes = Encoding.UTF8.GetBytes(text.ToUpperInvariant());
                await stream.WriteAsync(bytes, ct);
            }, cancellationToken);
        }
    }

    private sealed class WatermarkAction : PerFileAction<string>
    {
        private static readonly FileInputFilter Filter = new("image files", new[] { ".png", ".jpg" }, new[] { "image" });

        public WatermarkAction(FileActionServices services) : base(services)
        {
            Descriptor = new ActionDescriptor("test.watermark", "Watermark", "Adds watermark.",
                new HashSet<ContentKind> { ContentKind.Files }, new[] { "watermark" }, RequiresPreview: true, ChangesFiles: true,
                Parameters: new[] { ActionParameterDefinition.FilePath("Image", "Watermark Image", new[] { ".png", ".jpg" }, mustExist: true) });
        }

        public override ActionDescriptor Descriptor { get; }
        protected override FileInputFilter Input => Filter;
        protected override string ReadOptions(ActionParameterValues values) => values.GetFilePath("Image");
        protected override string DescribeOperation(int inputCount, string options) => $"Will watermark {inputCount} file(s).";
        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, string options)
        {
            yield return new(Path.GetFileNameWithoutExtension(input.Name) + " (watermarked)", input.Extension);
        }

        protected override Task ProcessAsync(FileWorkItem item, string options, FileOutputWriter output, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ExposeByFamilyAction : CombineFilesAction<string>
    {
        public ExposeByFamilyAction(FileActionServices services) : base(services)
        {
            Descriptor = new ActionDescriptor("test.byfamily", "Test", "Test.", new HashSet<ContentKind>(), Array.Empty<string>(), false, false);
        }

        public override ActionDescriptor Descriptor { get; }
        protected override FileInputFilter Input => new("all", new[] { ".txt", ".png", ".pdf" }, Array.Empty<string>());
        protected override string ReadOptions(ActionParameterValues values) => "";
        protected override string DescribeOperation(int inputCount, string options) => "";
        protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, string options) => new("test", ".txt");
        protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, string options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public ILookup<string, FileWorkItem> ExposeByFamily(IReadOnlyList<FileWorkItem> items) => ByFamily(items);
    }

    [Fact]
    public void ByFamily_GroupsCorrectly()
    {
        var services = Services();
        var action = new ExposeByFamilyAction(services);

        var files = new[]
        {
            new FileWorkItem(new SelectedFile(null, "a", ".txt", null, null, true, true, null, false), "C:\\a.txt", "C:\\", "a", null, 0, 4),
            new FileWorkItem(new SelectedFile(null, "b", ".png", null, null, true, true, null, false), "C:\\b.png", "C:\\", "b", null, 1, 4),
            new FileWorkItem(new SelectedFile(null, "c", ".jpg", null, null, true, true, null, false), "C:\\c.jpg", "C:\\", "c", null, 2, 4),
            new FileWorkItem(new SelectedFile(null, "d", ".pdf", null, null, true, true, null, false), "C:\\d.pdf", "C:\\", "d", null, 3, 4)
        };

        var grouped = action.ExposeByFamily(files);

        Assert.Single(grouped["text"]);
        Assert.Equal("a", grouped["text"].First().BaseName);

        Assert.Equal(2, grouped["image"].Count());
        Assert.Contains(grouped["image"], x => x.BaseName == "b");
        Assert.Contains(grouped["image"], x => x.BaseName == "c");

        Assert.Single(grouped["pdf"]);
        Assert.Equal("d", grouped["pdf"].First().BaseName);
    }

    [Fact]
    public async Task FilePathValidation_MissingOrInvalid_BlocksPreview()
    {
        var services = Services();
        var action = new WatermarkAction(services);
        var file = _dir.Write("test.png", "image");
        var context = Snapshots.ForPaths(file);

        // Missing file
        var p1 = await action.PreviewAsync(context, new EmptyParameters(), CancellationToken.None);
        Assert.True(p1.IsBlocked);
        Assert.Equal("Choose an existing PNG or JPG file.", p1.PreviewText);

        // Wrong extension
        var wrongExt = _dir.Write("watermark.txt", "text");
        var p2 = await action.PreviewAsync(context, Snapshots.Parameters(("Image", wrongExt)), CancellationToken.None);
        Assert.True(p2.IsBlocked);
        Assert.Equal("Choose an existing PNG or JPG file.", p2.PreviewText);

        // Does not exist
        var missingFile = Path.Combine(_dir.Path, "missing.png");
        var p3 = await action.PreviewAsync(context, Snapshots.Parameters(("Image", missingFile)), CancellationToken.None);
        Assert.True(p3.IsBlocked);
        Assert.Equal("Choose an existing PNG or JPG file.", p3.PreviewText);

        // Valid file
        var valid = _dir.Write("watermark.png", "image");
        var p4 = await action.PreviewAsync(context, Snapshots.Parameters(("Image", valid)), CancellationToken.None);
        Assert.False(p4.IsBlocked);
        Assert.StartsWith("Will watermark", p4.PreviewText);
    }

    [Fact]
    public async Task Execute_CreatesOutputsNextToSources_AndNeverTouchesOriginals()
    {
        string a = _dir.Write("a.txt", "hello");
        string b = _dir.Write("b.txt", "world");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(new[] { _dir.File("a.upper.txt"), _dir.File("b.upper.txt") }, receipt.OutputPaths);
        Assert.Equal("HELLO", File.ReadAllText(_dir.File("a.upper.txt")));
        Assert.Equal("hello", File.ReadAllText(a));
        Assert.DoesNotContain(_dir.NewFiles(a, b), p => Path.GetFileName(p).StartsWith(".~cb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_ExistingOutputName_GetsCollisionSuffix()
    {
        string a = _dir.Write("a.txt", "hello");
        _dir.Write("a.upper.txt", "keep me");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a), new EmptyParameters(), null, CancellationToken.None);

        Assert.Equal(_dir.File("a.upper (1).txt"), Assert.Single(receipt.OutputPaths));
        Assert.Equal("keep me", File.ReadAllText(_dir.File("a.upper.txt")));
    }

    [Fact]
    public async Task Execute_NameTakenDuringProcessing_DoesNotOverwrite()
    {
        string a = _dir.Write("a.txt", "hello");
        var action = new UpperCaseAction(Services())
        {
            // Another program creates the planned name while we work.
            BeforeWrite = (_, _) => { File.WriteAllText(_dir.File("a.upper.txt"), "racer"); return Task.CompletedTask; }
        };

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Equal("racer", File.ReadAllText(_dir.File("a.upper.txt")));
        Assert.Equal(_dir.File("a.upper (1).txt"), Assert.Single(receipt.OutputPaths));
    }

    [Fact]
    public async Task Execute_PartialFailure_ReportsPerItemWithSafeMessages()
    {
        string ok = _dir.Write("ok.txt", "fine");
        string rejected = _dir.Write("rejected.txt", "FAIL");
        string crash = _dir.Write("crash.txt", "CRASH");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(ok, rejected, crash), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        Assert.Equal(2, receipt.Failures.Count);
        Assert.Contains(receipt.Failures, f => f.ErrorMessage == "Content rejected.");
        Assert.Contains(receipt.Failures, f => f.ErrorMessage == "Processing failed unexpectedly.");
        Assert.DoesNotContain(receipt.Failures, f => f.ErrorMessage.Contains("private"));
        Assert.Contains("1 output", receipt.Message);
    }

    [Fact]
    public async Task Execute_AllSkipped_ReturnsSuccessAndSkipMessage()
    {
        string a = _dir.Write("a.txt", "SKIP");
        string b = _dir.Write("b.txt", "SKIP");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Empty(receipt.Failures);
        Assert.Contains("2 item(s) needed no changes.", receipt.Message);
        Assert.Empty(_dir.NewFiles(a, b));
    }

    [Fact]
    public async Task Execute_MixedOutputsAndSkips_ReturnsSuccess()
    {
        string a = _dir.Write("a.txt", "hello");
        string b = _dir.Write("b.txt", "SKIP");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Single(receipt.OutputPaths);
        Assert.Empty(receipt.Failures);
        Assert.Contains("Created 1 output.", receipt.Message);
        Assert.Contains("1 item(s) needed no changes.", receipt.Message);
    }

    [Fact]
    public async Task Execute_SkipPlusFailure_ReturnsNotSuccess()
    {
        string a = _dir.Write("a.txt", "SKIP");
        string b = _dir.Write("b.txt", "FAIL");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(receipt.OutputPaths);
        Assert.Single(receipt.Failures);
        Assert.Contains("1 item(s) needed no changes.", receipt.Message);
    }

    [Fact]
    public async Task Execute_EmptyEngineOutput_IsAFailureAndLeavesNoFile()
    {
        string a = _dir.Write("a.txt", "hello");
        var action = new UpperCaseAction(Services()) { ProduceNothing = true };

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("The processing engine produced no output.", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.Empty(_dir.NewFiles(a));
    }

    [Fact]
    public async Task Execute_Cancellation_ReportsCompletedOutputsAndCleansTemporaryFiles()
    {
        string a = _dir.Write("a.txt", "first");
        string b = _dir.Write("b.txt", "second");
        using var cts = new CancellationTokenSource();
        var action = new UpperCaseAction(Services())
        {
            BeforeWrite = (item, ct) =>
            {
                if (item.Index == 1) cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), null, cts.Token);

        Assert.True(receipt.IsCanceled);
        Assert.False(receipt.IsSuccess);
        Assert.Equal(_dir.File("a.upper.txt"), Assert.Single(receipt.OutputPaths));
        Assert.Equal(new[] { _dir.File("a.upper.txt") }, _dir.NewFiles(a, b));
    }

    [Fact]
    public async Task Execute_StaleSelection_IsRejectedWithoutWriting()
    {
        string a = _dir.Write("a.txt", "hello");
        var action = new UpperCaseAction(Services());

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a).Stale(), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Empty(_dir.NewFiles(a));
    }

    [Fact]
    public async Task Execute_FileDeletedAfterCapture_IsReported()
    {
        string a = _dir.Write("a.txt", "hello");
        var snapshot = Snapshots.ForPaths(a);
        File.Delete(a);

        var receipt = await new UpperCaseAction(Services()).ExecuteAsync(snapshot, new EmptyParameters(), null, CancellationToken.None);

        Assert.Equal("The file no longer exists.", Assert.Single(receipt.Failures).ErrorMessage);
    }

    [Fact]
    public async Task Execute_ContentVerificationFailure_SkipsFile()
    {
        string a = _dir.Write("a.txt", "hello");
        var action = new UpperCaseAction(Services(new RejectingVerifier()));

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal("Not really text.", Assert.Single(receipt.Failures).ErrorMessage);
        Assert.Empty(_dir.NewFiles(a));
    }

    [Fact]
    public async Task Match_UsesExtensionsOnly_AndExplainsMixedSelections()
    {
        string a = _dir.Write("a.txt", "x");
        string b = _dir.Write("b.png", "x");
        var action = new UpperCaseAction(Services(new RejectingVerifier()));

        var mixed = await action.MatchAsync(Snapshots.ForPaths(a, b), CancellationToken.None);
        var none = await action.MatchAsync(Snapshots.ForPaths(b), CancellationToken.None);

        Assert.True(mixed.IsAvailable);
        Assert.Contains("Applies to 1 of 2 items.", mixed.Reason);
        Assert.False(none.IsAvailable);
    }

    [Fact]
    public async Task Preview_WarnsAboutSkippedItems_WhenMixed()
    {
        string a = _dir.Write("a.txt", "x");
        string b = _dir.Write("b.png", "x");

        // Use an action that does not throw an exception on Preview or TryGetBlockingProblem
        var action = new UpperCaseAction(Services());

        var preview = await action.PreviewAsync(Snapshots.ForPaths(a, b), new EmptyParameters(), CancellationToken.None);

        Assert.False(preview.IsBlocked);
        Assert.Contains("Applies to 1 of 2 items.", preview.Warnings);
    }

    [Fact]
    public async Task Preview_PlansDistinctNamesAndUsesOptions()
    {
        string a = _dir.Write("a.txt", "x");
        Directory.CreateDirectory(_dir.File("sub"));
        string b = _dir.Write(Path.Combine("sub", "a.txt"), "y");
        _dir.Write("a.big.txt", "existing");
        var action = new UpperCaseAction(Services());

        var preview = await action.PreviewAsync(Snapshots.ForPaths(a, b), Snapshots.Parameters(("Suffix", "big")), CancellationToken.None);

        Assert.False(preview.IsBlocked);
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal(new[] { _dir.File("a.big (1).txt"), Path.Combine(_dir.Path, "sub", "a.big.txt") }, preview.ExpectedOutputs);
        Assert.Contains("originals are not changed", preview.PreviewText);
    }

    [Fact]
    public async Task Preview_StaleSelection_IsBlocked()
    {
        string a = _dir.Write("a.txt", "x");
        var preview = await new UpperCaseAction(Services()).PreviewAsync(Snapshots.ForPaths(a).Stale(), new EmptyParameters(), CancellationToken.None);
        Assert.True(preview.IsBlocked);
    }

    [Fact]
    public async Task Execute_ReportsSizeChange()
    {
        string a = _dir.Write("a.txt", "hello");
        var receipt = await new UpperCaseAction(Services()).ExecuteAsync(Snapshots.ForPaths(a), new EmptyParameters(), null, CancellationToken.None);
        Assert.Contains("(5 B -> 5 B)", receipt.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionMessage_ContainsNoParameterValues()
    {
        var action = new PasswordEchoAction(Services());
        string a = _dir.Write("a.txt", "hello");
        var parameters = Snapshots.Parameters(("Pwd", "my_super_secret_password"));

        var preview = await action.PreviewAsync(Snapshots.ForPaths(a), parameters, CancellationToken.None);
        Assert.DoesNotContain("my_super_secret_password", preview.PreviewText ?? string.Empty);

        var receipt = await action.ExecuteAsync(Snapshots.ForPaths(a), parameters, null, CancellationToken.None);
        Assert.DoesNotContain("my_super_secret_password", receipt.Message ?? string.Empty);
        foreach (var failure in receipt.Failures)
        {
            Assert.DoesNotContain("my_super_secret_password", failure.ErrorMessage);
        }
    }

    private sealed class PasswordEchoAction : PerFileAction<string>
    {
        public PasswordEchoAction(FileActionServices services) : base(services)
        {
            Descriptor = new ActionDescriptor("test.pwd", "Pwd", "Desc",
                new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), RequiresPreview: true, ChangesFiles: true,
                Parameters: new[] { ActionParameterDefinition.Password("Pwd", "Pwd") });
        }

        public override ActionDescriptor Descriptor { get; }
        protected override FileInputFilter Input => new("text files", new[] { ".txt" }, new[] { "text" });

        protected override string ReadOptions(ActionParameterValues values) => values.GetPassword("Pwd");

        protected override string DescribeOperation(int inputCount, string options) => $"Processing with password";

        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, string options)
        {
            yield return new PlannedOutput("out", ".txt");
        }

        protected override Task ProcessAsync(FileWorkItem item, string options, FileOutputWriter output, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"Failed with {options}");
        }
    }

    private sealed class RejectingVerifier : IFileTypeVerifier
    {
        public ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken cancellationToken) =>
            new(FileTypeVerification.Rejected("png", "Not really text."));
    }
}

public class UpperCaseContractTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();

    protected override IAction CreateAction() => new CopyAction();

    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(_dir.Write("a.txt", "x"));

    public void Dispose() => _dir.Dispose();

    private sealed class CopyAction : PerFileAction<int>
    {
        public CopyAction() : base(new FileActionServices(new PhysicalOutputFileSystem(), new OutputPlanner(new PhysicalOutputFileSystem()), new ExtensionOnlyTypeVerifier()))
        {
        }

        public override ActionDescriptor Descriptor { get; } = new("test.copy", "Copy", "Copies", new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), true, true);
        protected override FileInputFilter Input { get; } = new("text files", new[] { ".txt" }, null);
        protected override int ReadOptions(ActionParameterValues values) => 0;
        protected override string DescribeOperation(int inputCount, int options) => "Copy.";
        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, int options) { yield return new("copy", ".txt"); }
        protected override Task ProcessAsync(FileWorkItem item, int options, FileOutputWriter output, CancellationToken cancellationToken) =>
            output.WriteFileAsync(item.Directory, "copy", ".txt", (s, ct) => s.WriteAsync(new byte[] { 1 }, ct).AsTask(), cancellationToken);
    }
}
