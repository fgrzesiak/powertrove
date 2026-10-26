using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerTrove.Core.Search.Tests;

public class ActionSearchEngineStressTests
{
    private class SlowMockAction : IAction
    {
        private static int _inFlight;
        public static int MaxInFlight;

        public static void Reset() { _inFlight = 0; MaxInFlight = 0; }

        public ActionDescriptor Descriptor { get; }
        public bool IsAvailable { get; set; } = true;

        public SlowMockAction(string id, string title)
        {
            Descriptor = new ActionDescriptor(
                Id: id,
                Title: title,
                Description: "A mock action",
                Accepts: new HashSet<ContentKind> { ContentKind.None },
                SearchTerms: Array.Empty<string>(),
                RequiresPreview: false,
                ChangesFiles: false,
                ExplicitWeight: 0);
        }

        public async ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken)
        {
            // Simulate a slow dynamic match and record how many overlap.
            var current = Interlocked.Increment(ref _inFlight);
            int seen;
            while (current > (seen = Volatile.Read(ref MaxInFlight)) &&
                   Interlocked.CompareExchange(ref MaxInFlight, current, seen) != seen) { }
            try
            {
                await Task.Delay(20, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
            return new ActionAvailability(IsAvailable);
        }

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private ContextSnapshot CreateSnapshot(ContentKind kind)
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1234, "test", DateTimeOffset.Now, "Test"),
            ContextStatus.Available,
            new TextSelection("test"), // We just need ANY content here, so this is fine
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(5),
            "Test",
            null
        );
    }

    [Fact]
    public async Task SearchAsync_StressTest_1000Actions_IsResponsive()
    {
        SlowMockAction.Reset();
        var registry = new ActionRegistry();
        for (int i = 0; i < 1000; i++)
        {
            registry.Register(new SlowMockAction($"action.{i}", $"Test Action {i}"));
        }
        // Add one specific target we want to find
        registry.Register(new SlowMockAction("target.action", "Super specific target to find"));

        var engine = new ActionSearchEngine(registry);
        var context = CreateSnapshot(ContentKind.None);

        var sw = Stopwatch.StartNew();
        var results = await engine.SearchAsync("Super specific", context);
        sw.Stop();

        Assert.Contains(results, r => r.Action.Descriptor.Id == "target.action");
        // Matches must overlap. Asserting concurrency instead of a tight wall-clock budget
        // keeps the test stable on a loaded machine; sequential matching would take over
        // 20 s (1001 x 20 ms), far beyond the generous upper bound below.
        Assert.True(SlowMockAction.MaxInFlight > 1, "MatchAsync calls ran sequentially");
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"Search took too long: {sw.ElapsedMilliseconds} ms");
    }
}
