using ContextBar.Abstractions.Packs;

[assembly: ActionPackFactory(typeof(ContextBar.PackHost.TestPack.TinyPackFactory))]

namespace ContextBar.PackHost.TestPack;

public sealed class TinyPackFactory : IPackFactory
{
    public ActionPackInfo Info => new("contextbar.fixture", "Generated fixture pack", "0.1.0", "ContextBar tests");
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
        public override ActionDescriptor Descriptor { get; } = new(wait ? "contextbar.fixture.wait" : "contextbar.fixture.copy",
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
