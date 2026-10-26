using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Application;
using ContextBar.Core;
using ContextBar.Core.Search;
using ContextBar.Desktop;
using ContextBar.Platform.Windows.Excel;
using Xunit;

namespace ContextBar.Desktop.Tests;

public class AppCompositionTests
{
    [Fact]
    public void App_Providers_ShouldIncludeExcelProvider()
    {
        var providers = BuiltInContextProviders.Create();

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(providers, p => p is ExcelSelectionProvider);
            Assert.Contains(providers, p => p is ContextBar.Platform.Windows.Capture.ExplorerContextProvider);
        }
        else Assert.Empty(providers);
    }

    [Fact]
    public async Task App_ActionRegistry_ShouldResolveCsvAndJsonActionsForTableContext()
    {
        var registry = new ActionRegistry();
        var workflowRunner = new ContextBar.Core.Workflows.WorkflowRunner(registry);
        foreach (var pack in BuiltInActionPacks.Create(null, workflowRunner))
        {
            foreach (var action in pack.Actions)
            {
                registry.Register(action);
            }
        }

        var rows = new List<IReadOnlyList<CellValue>>();
        var row = new List<CellValue> { new TextCell("Test"), new NumberCell(123), new BooleanCell(true), new EmptyCell(), new ErrorCell("-2146826281") };
        rows.Add(row);
        var tableData = new TableData(rows, "Microsoft Excel", "A1:E1");

        var snapshot = new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "excel", DateTimeOffset.UtcNow),
            ContextStatus.Available,
            tableData,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "contextbar.provider.excel",
            null,
            new HashSet<string> { "table.export.v1" }
        );

        var searchEngine = new ActionSearchEngine(registry);

        var results = await searchEngine.SearchAsync("", snapshot);

        Assert.Contains(results, r => r.Action.Descriptor.Id == "contextbar.table.export-csv");
        Assert.Contains(results, r => r.Action.Descriptor.Id == "contextbar.table.export-json");
    }

    [Fact]
    public async Task App_ActionRegistry_UnsupportedRangesAndTimeouts_ShouldReturnBoundedResults()
    {
        var provider = new ExcelSelectionProvider(new MockExcelInteropUnsupported(), TimeSpan.FromSeconds(1));
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "excel", DateTimeOffset.UtcNow);
        var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal("Unsupported range", snapshot.UserMessage);
    }

    private class MockExcelInteropUnsupported : IExcelInterop
    {
        public ExcelCaptureResult Capture(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            return new ExcelCaptureResult(ContextStatus.Unsupported, Message: "Unsupported range");
        }
    }

    [Fact]
    public async Task App_ActionRegistry_MissingExcel_ShouldReturnBoundedResults()
    {
        var provider = new ExcelSelectionProvider(new MockExcelInteropMissing(), TimeSpan.FromSeconds(1));
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "excel", DateTimeOffset.UtcNow);
        var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);

        Assert.Equal(ContextStatus.Failed, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
    }

    private class MockExcelInteropMissing : IExcelInterop
    {
        public ExcelCaptureResult Capture(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            throw new Exception("Excel not available");
        }
    }

    [Fact]
    public async Task App_ActionRegistry_Timeout_ShouldReturnBoundedResults()
    {
        var provider = new ExcelSelectionProvider(new MockExcelInteropTimeout(), TimeSpan.FromMilliseconds(100));
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "excel", DateTimeOffset.UtcNow);
        var snapshot = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);

        Assert.Equal(ContextStatus.TimedOut, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal(ContextBar.Platform.Windows.Resources.Strings.ExcelCaptureTimedOut, snapshot.UserMessage);

        // The provider allows one capture at a time; wait until the abandoned worker released it.
        for (int i = 0; i < 100; i++)
        {
            var probe = await new ExcelSelectionProvider(new MockExcelInteropMissing(), TimeSpan.FromSeconds(1))
                .CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);
            if (probe.UserMessage != ContextBar.Platform.Windows.Resources.Strings.ExcelCaptureBusy) break;
            await Task.Delay(20);
        }
    }

    private class MockExcelInteropTimeout : IExcelInterop
    {
        public ExcelCaptureResult Capture(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            // Longer than the 100 ms budget, but ends once the provider cancels so the
            // single capture slot is free again for the next test.
            cancellationToken.WaitHandle.WaitOne(500);
            return new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, 1, 1, new object?[1,1] {{1}}, "A1"));
        }
    }
}
