using PowerTrove.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(PowerTrove.PackHost.TestPack.TinyPackFactory))]

namespace PowerTrove.PackHost.TestPack;

public sealed class TinyPackFactory : IPackFactory
{
    public ActionPackInfo Info => new("powertrove.fixture", "Generated fixture pack", "0.1.0", "PowerTrove tests");
    public IActionPack Create(IPackHostServices host) => new TinyPack(Info, host.Files);
    private sealed class TinyPack(ActionPackInfo info, FileActionServices files) : IActionPack
    {
        public ActionPackInfo Info => info;
        public IReadOnlyList<IAction> Actions { get; } = [new WriteAction(files, false), new WriteAction(files, true)];
    }
    public static TaskCompletionSource Started { get; private set; } = NewSignal();
    public static void Reset() => Started = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WriteAction(FileActionServices files, bool wait) : PerFileAction<bool>(files)
    {
        public override ActionDescriptor Descriptor { get; } = new(wait ? "powertrove.fixture.wait" : "powertrove.fixture.copy",
            "Fixture copy", "Writes a generated copy through a path-based engine.", new HashSet<ContentKind> { ContentKind.Files },
            Array.Empty<string>(), RequiresPreview: true, ChangesFiles: true,
            Selection: new SelectionRequirements(new HashSet<string> { "text" }, MinItems: 1));
        protected override FileInputFilter Input => new("fixture text files", new[] { ".txt" }, null);
        protected override bool ReadOptions(ActionParameterValues values) => wait;
        protected override string DescribeOperation(int count, bool options) => "Generated fixture output.";
        protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, bool options) => [new("fixture-copy", ".txt")];
        protected override async Task ProcessAsync(FileWorkItem item, bool options, FileOutputWriter output, CancellationToken token)
        {
            Started.TrySetResult();
            if (options) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            await output.WriteFileFromPathAsync(item.Directory, "fixture-copy", ".txt",
                (path, ct) => File.WriteAllTextAsync(path, "generated fixture output", ct), token);
        }
    }
}
