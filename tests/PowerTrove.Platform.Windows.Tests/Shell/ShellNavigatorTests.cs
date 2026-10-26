using System.Globalization;
using PowerTrove.Platform.Windows.Shell;

namespace PowerTrove.Platform.Windows.Tests.Shell;

public sealed class ShellNavigatorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(unchecked((int)0x80070005), false)]
    public void HResultMapping_DoesNotReportFailureAsSuccess(int hresult, bool success)
    {
        var result = ShellNavigationResult.FromHResult(hresult);
        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(success ? ShellNavigationError.None : ShellNavigationError.OpenFailed, result.Error);
    }

    [Fact]
    public async Task FailedShellCall_ReturnsFailure_AndFreesFolderAndEveryItem()
    {
        var native = new FakeInterop { HResult = unchecked((int)0x80004005) };
        var result = await new ShellNavigator(native).OpenFolderAndSelect(Paths(3));
        Assert.False(result.IsSuccess); Assert.Equal(ShellNavigationError.OpenFailed, result.Error);
        Assert.Equal(native.Allocated.OrderBy(p => p.ToInt64()), native.Freed.OrderBy(p => p.ToInt64()));
        Assert.Equal(4, native.Freed.Count); Assert.Equal(3, Assert.Single(native.OpenCalls).Count);
    }

    [Fact]
    public async Task FailedItemAllocation_IsSkipped_ButOtherItemsAreSelected_AndFailureIsReported()
    {
        var native = new FakeInterop { FailAllocation = 3 };
        var result = await new ShellNavigator(native).OpenFolderAndSelect(Paths(3));
        Assert.Equal(ShellNavigationError.PidlCreationFailed, result.Error);
        Assert.Equal(2, Assert.Single(native.OpenCalls).Count);
        Assert.DoesNotContain(IntPtr.Zero, native.OpenCalls[0]);
        Assert.Equal(native.Allocated.OrderBy(p => p.ToInt64()), native.Freed.OrderBy(p => p.ToInt64()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedFolderOrOnlyItemAllocation_DoesNotCallShellWithEmptyPointers(int allocation)
    {
        var native = new FakeInterop { FailAllocation = allocation };
        Assert.False((await new ShellNavigator(native).OpenFolderAndSelect(Paths(1))).IsSuccess);
        Assert.Empty(native.OpenCalls);
        Assert.Equal(native.Allocated, native.Freed);
    }

    [Fact]
    public async Task GroupsAndChunks_UseAtMost50AbsoluteItemPidlsPerCall()
    {
        var native = new FakeInterop();
        var paths = Paths(51).Concat(new[] { Path.Combine(Path.GetTempPath(), "PowerTrove-shell-tests", "other", "synthetic.pdf") }).ToArray();
        Assert.True((await new ShellNavigator(native).OpenFolderAndSelect(paths)).IsSuccess);
        Assert.Equal(new[] { 50, 1, 1 }, native.OpenCalls.Select(c => c.Count));
        Assert.Equal(native.Allocated.OrderBy(p => p.ToInt64()), native.Freed.OrderBy(p => p.ToInt64()));
    }

    [Fact]
    public async Task UnsupportedInteropAndEmptySelection_ReturnTypedFailuresWithoutNativeCalls()
    {
        var native = new FakeInterop { IsSupported = false };
        Assert.Equal(ShellNavigationError.UnsupportedPlatform, (await new ShellNavigator(native).OpenFolderAndSelect(Paths(1))).Error);
        Assert.Equal(ShellNavigationError.InvalidSelection, (await new ShellNavigator(native).OpenFolderAndSelect(Array.Empty<string>())).Error);
        Assert.Empty(native.Allocated); Assert.Empty(native.OpenCalls);
    }

    [Fact]
    public async Task CancellationDuringAllocation_StillFreesAllOwnedPidls()
    {
        using var cts = new CancellationTokenSource();
        var native = new FakeInterop { OnAllocation = number => { if (number == 3) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ShellNavigator(native).OpenFolderAndSelect(Paths(3), cts.Token));
        Assert.Equal(native.Allocated.OrderBy(p => p.ToInt64()), native.Freed.OrderBy(p => p.ToInt64()));
        Assert.Empty(native.OpenCalls);
    }

    [Fact]
    public async Task FailureMessages_AreLocalized_AndNeverIncludeNativeDetails()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            string english = ShellNavigationResult.FromHResult(-1).Message!;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            string german = ShellNavigationResult.FromHResult(-1).Message!;
            Assert.NotEqual(english, german); Assert.Contains("Explorer", german);
            var native = new FakeInterop { ThrowOnOpen = true };
            var result = await new ShellNavigator(native).OpenFolderAndSelect(Paths(1));
            Assert.False(result.IsSuccess); Assert.DoesNotContain("sensitive", result.Message!);
            Assert.Equal(native.Allocated.OrderBy(p => p.ToInt64()), native.Freed.OrderBy(p => p.ToInt64()));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    private static string[] Paths(int count) => Enumerable.Range(0, count).Select(i => Path.Combine(Path.GetTempPath(), "PowerTrove-shell-tests", "one", "synthetic" + i + ".pdf")).ToArray();
    private sealed class FakeInterop : IShellSelectionInterop
    {
        public bool IsSupported { get; set; } = true;
        public int HResult { get; set; }
        public int FailAllocation { get; set; }
        public bool ThrowOnOpen { get; set; }
        public Action<int>? OnAllocation { get; set; }
        public List<IntPtr> Allocated { get; } = new();
        public List<IntPtr> Freed { get; } = new();
        public List<IReadOnlyList<IntPtr>> OpenCalls { get; } = new();
        private int _allocations;
        public IntPtr CreatePidl(string path)
        {
            int number = ++_allocations;
            var pointer = number == FailAllocation ? IntPtr.Zero : new IntPtr(number);
            if (pointer != IntPtr.Zero) Allocated.Add(pointer);
            OnAllocation?.Invoke(number); return pointer;
        }
        public int OpenFolderAndSelect(IntPtr folder, IReadOnlyList<IntPtr> items)
        {
            OpenCalls.Add(items.ToArray());
            if (ThrowOnOpen) throw new InvalidOperationException("sensitive native details");
            return HResult;
        }
        public void FreePidl(IntPtr pointer) => Freed.Add(pointer);
    }
}
