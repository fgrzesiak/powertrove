using System;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Core;
using PowerTrove.Platform.Windows.Excel;
using Xunit;
using Moq;

namespace PowerTrove.Platform.Windows.Tests.Excel;

public class ExcelSelectionProviderTests
{
    private readonly Mock<IExcelInterop> _interopMock;
    private readonly ExcelSelectionProvider _provider;

    public ExcelSelectionProviderTests()
    {
        _interopMock = new Mock<IExcelInterop>();
        _provider = new ExcelSelectionProvider(_interopMock.Object);
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsTrue_ForExcelProcess()
    {
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1234, "excel", DateTimeOffset.UtcNow);
        var result = await _provider.CanCaptureAsync(identity, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsTrue_ForExcelProcess_CaseInsensitive()
    {
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1234, "EXCEL", DateTimeOffset.UtcNow);
        var result = await _provider.CanCaptureAsync(identity, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task CanCaptureAsync_ReturnsFalse_ForOtherProcess()
    {
        var identity = new SourceWindowIdentity(IntPtr.Zero, 1234, "notepad", DateTimeOffset.UtcNow);
        var result = await _provider.CanCaptureAsync(identity, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupported_WhenExcelNotAvailable()
    {
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Unsupported, Message: "Excel not available."));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal("Excel not available.", snapshot.UserMessage);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupported_WhenWindowHandleMismatch()
    {

        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Unsupported, Message: "Excel window changed."));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.Equal("Excel window changed.", snapshot.UserMessage);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsEmpty_WhenNoSelection()
    {

        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Empty));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Empty, snapshot.Status);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupported_WhenNotARange()
    {

        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(false, 0, 0, 0, null, null)));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.Contains("not a range", snapshot.UserMessage);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsUnsupported_WhenMultipleAreas()
    {

        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 2, 1, 1, null, "$A$1,$B$2")));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.Contains("Disjoint selections", snapshot.UserMessage);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsAvailable_WithTableData_ForSingleCell()
    {

        var values = new object?[1, 1] { { "Hello" } };
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, 1, 1, values, "$A$1")));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var tableData = Assert.IsType<TableData>(snapshot.Content);
        Assert.Single(tableData.Rows);
        Assert.Single(tableData.Rows[0]);
        Assert.IsType<TextCell>(tableData.Rows[0][0]);
        Assert.Equal("Hello", ((TextCell)tableData.Rows[0][0]).Value);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsAvailable_WithTableData_ForMultiCellWithMixedTypes()
    {

        var values = new object?[2, 2]
        {
            { "Text", 123.45 },
            { true, null }
        };
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>())).Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, 2, 2, values, "$A$1:$B$2")));

        var identity = new SourceWindowIdentity(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
        var snapshot = await _provider.CaptureAsync(Guid.NewGuid(), identity, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var tableData = Assert.IsType<TableData>(snapshot.Content);

        Assert.Equal(2, tableData.Rows.Count);
        Assert.Equal(2, tableData.Rows[0].Count);
        Assert.Equal(2, tableData.Rows[1].Count);

        Assert.IsType<TextCell>(tableData.Rows[0][0]);
        Assert.Equal("Text", ((TextCell)tableData.Rows[0][0]).Value);

        Assert.IsType<NumberCell>(tableData.Rows[0][1]);
        Assert.Equal(123.45, ((NumberCell)tableData.Rows[0][1]).Value);

        Assert.IsType<BooleanCell>(tableData.Rows[1][0]);
        Assert.True(((BooleanCell)tableData.Rows[1][0]).Value);

        Assert.IsType<EmptyCell>(tableData.Rows[1][1]);
    }

    private static SourceWindowIdentity Source() => new(new IntPtr(100), 1234, "excel", DateTimeOffset.UtcNow);
    private static ExcelCaptureResult SingleCell() => new(ContextStatus.Available,
        new ExcelSelectionInfo(true, 1, 1, 1, new object?[,] { { 42.0 } }, null));

    [Theory]
    [InlineData(ContextStatus.Failed)]
    [InlineData(ContextStatus.Denied)]
    [InlineData(ContextStatus.Unsupported)]
    public async Task CaptureAsync_PreservesTypedFailures(ContextStatus status)
    {
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(new ExcelCaptureResult(status, Message: "Selection unavailable."));
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), CancellationToken.None);
        Assert.Equal(status, result.Status);
        Assert.IsType<NoContent>(result.Content);
    }

    [Fact]
    public async Task CaptureAsync_ReturnsFailed_WhenInteropThrows()
    {
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Throws(new System.Runtime.InteropServices.COMException());
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), CancellationToken.None);
        Assert.Equal(ContextStatus.Failed, result.Status);
        Assert.IsType<NoContent>(result.Content);
    }

    [Theory]
    [InlineData(1_000_001, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(0, 1)]
    public async Task CaptureAsync_RejectsInvalidOrOversizedDimensions(int rows, int columns)
    {
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, rows, columns, null, null)));
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), CancellationToken.None);
        Assert.Equal(ContextStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task CaptureAsync_PreservesOneBasedArrayValues()
    {
        var values = (object[,])Array.CreateInstance(typeof(object), new[] { 2, 2 }, new[] { 1, 1 });
        values[1, 1] = 42.0;
        values[2, 2] = true;
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, 2, 2, values, null)));
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), CancellationToken.None);
        var table = Assert.IsType<TableData>(result.Content);
        Assert.Equal(42.0, Assert.IsType<NumberCell>(table.Rows[0][0]).Value);
        Assert.IsType<EmptyCell>(table.Rows[0][1]);
        Assert.True(Assert.IsType<BooleanCell>(table.Rows[1][1]).Value);
    }

    [Fact]
    public async Task CaptureAsync_RejectsInconsistentArrayDimensions()
    {
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(new ExcelCaptureResult(ContextStatus.Available, new ExcelSelectionInfo(true, 1, 2, 2, new object?[1, 1], null)));
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), CancellationToken.None);
        Assert.Equal(ContextStatus.Failed, result.Status);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotCallInterop_WhenAlreadyCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), cancellation.Token);
        Assert.Equal(ContextStatus.Canceled, result.Status);
        _interopMock.Verify(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class CancelDuringConversion(CancellationTokenSource cancellation)
    {
        public override string ToString()
        {
            cancellation.Cancel();
            return "";
        }
    }

    [Fact]
    public async Task CaptureAsync_RejectsCancellationDuringConversion()
    {
        using var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        _interopMock.Setup(x => x.Capture(It.IsAny<SourceWindowIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                finished.TrySetResult(Thread.CurrentThread);
                return new ExcelCaptureResult(ContextStatus.Available,
                    new ExcelSelectionInfo(true, 1, 1, 1, new object?[,] { { new CancelDuringConversion(cancellation) } }, null));
            });
        var result = await _provider.CaptureAsync(Guid.NewGuid(), Source(), cancellation.Token);
        var worker = await finished.Task;
        Assert.True(await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))));
        Assert.Equal(ContextStatus.Canceled, result.Status);
        Assert.IsType<NoContent>(result.Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureAsync_RejectsLateResults_AndBoundsBlockedWorkers(bool cancel)
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source();
        _interopMock.Setup(x => x.Capture(source, It.IsAny<CancellationToken>()))
            .Returns((SourceWindowIdentity identity, CancellationToken token) =>
            {
                entered.TrySetResult(Thread.CurrentThread);
                release.Wait(TimeSpan.FromSeconds(10)); // Simulate a COM server ignoring cancellation.
                return SingleCell();
            });
        var provider = new ExcelSelectionProvider(_interopMock.Object,
            cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250));
        Task<ContextSnapshot> capture = provider.CaptureAsync(Guid.NewGuid(), source, cancellation.Token).AsTask();
        Thread? worker = null;
        try
        {
            worker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, worker.ManagedThreadId);
            if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.STA, worker.GetApartmentState());
            if (cancel) cancellation.Cancel();
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? ContextStatus.Canceled : ContextStatus.TimedOut, result.Status);
            Assert.IsType<NoContent>(result.Content);
            var busy = await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None);
            Assert.Equal(ContextStatus.TimedOut, busy.Status);
            _interopMock.Verify(x => x.Capture(source, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            release.Set();
            worker ??= await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))));
        }
        Assert.Equal(cancel ? ContextStatus.Canceled : ContextStatus.TimedOut, (await capture).Status);
        // The slot is reusable only after the abandoned worker has completed cleanup.
        _interopMock.Setup(x => x.Capture(source, It.IsAny<CancellationToken>())).Returns(SingleCell());
        Assert.Equal(ContextStatus.Available, (await provider.CaptureAsync(Guid.NewGuid(), source, CancellationToken.None)).Status);
    }
}
