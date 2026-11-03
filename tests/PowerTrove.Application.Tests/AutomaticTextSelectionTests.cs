using PowerTrove.Application;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;

namespace PowerTrove.Application.Tests;

public sealed class AutomaticTextSelectionTests
{
    [Fact]
    public async Task SelectionIsCapturedBeforePaletteOpens_AndBecomesTextContext()
    {
        var selection = new Probe("selected automatically");
        var (coordinator, vm) = Create("notepad", selection);
        bool opened = false;
        coordinator.PaletteRequested += (_, _) =>
        {
            opened = true;
            Assert.Equal(1, selection.Calls);
            Assert.True(selection.Completed);
        };
        await coordinator.InvokeAsync();
        Assert.True(opened);
        Assert.Equal("selected automatically", Assert.IsType<TextSelection>(vm.CurrentSnapshot!.Content).Text);
        Assert.Equal("powertrove.provider.windows.uia-text", vm.CurrentSnapshot.ProviderId);
        Assert.Equal(ContextStatus.Available, vm.CurrentSnapshot.Status);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("EXCEL")]
    public async Task SpecializedFileAndTableProvidersAreNotPreempted(string process)
    {
        var selection = new Probe("must not capture");
        var (coordinator, vm) = Create(process, selection);
        await coordinator.InvokeAsync();
        Assert.Equal(0, selection.Calls);
        Assert.IsType<NoContent>(vm.CurrentSnapshot!.Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too\0sensitive")]
    public async Task UnsupportedOrInvalidSelectionFallsBack(string? value)
    {
        var selection = new Probe(value);
        var (coordinator, vm) = Create("notepad", selection);
        await coordinator.InvokeAsync();
        Assert.Equal(1, selection.Calls);
        Assert.IsType<NoContent>(vm.CurrentSnapshot!.Content);
    }

    [Fact]
    public async Task OversizedSelectionFallsBackWithoutPublishingText()
    {
        var selection = new Probe(new string('x', ForegroundTextSelectionPolicy.MaximumCharacters + 1));
        var (coordinator, vm) = Create("notepad", selection);
        await coordinator.InvokeAsync();
        Assert.IsType<NoContent>(vm.CurrentSnapshot!.Content);
    }

    [Fact]
    public void UnknownProcessNameDoesNotPreventWindowBoundCapture()
    {
        var source = new SourceWindowIdentity((nint)789, 923, null, DateTimeOffset.UtcNow);
        Assert.True(ForegroundTextSelectionPolicy.ShouldCapture(source));
        Assert.False(ForegroundTextSelectionPolicy.ShouldCapture(source with { WindowHandle = IntPtr.Zero }));
    }

    [Fact]
    public void ValidationAcceptsEmojiButRejectsInvalidSurrogatePairs()
    {
        Assert.True(ForegroundTextSelectionPolicy.IsValid("hello 🌍"));
        Assert.False(ForegroundTextSelectionPolicy.IsValid("\ud800"));
        Assert.False(ForegroundTextSelectionPolicy.IsValid("\udc00"));
        Assert.False(ForegroundTextSelectionPolicy.IsValid(""));
    }

    private static (InvocationCoordinator, PaletteViewModel) Create(string process, Probe probe)
    {
        var registry = new ActionRegistry();
        var search = new ActionSearchEngine(registry);
        var vm = new PaletteViewModel();
        var source = new SourceWindowIdentity((nint)789, 923, process, DateTimeOffset.UtcNow);
        return (new InvocationCoordinator(null, new FixedForeground(source),
            new ContextResolver(Array.Empty<IContextProvider>()), vm, registry, search, probe), vm);
    }

    private sealed class Probe(string? text) : IForegroundTextSelectionCapture
    {
        public int Calls { get; private set; }
        public bool Completed { get; private set; }
        public ValueTask<string?> CaptureAsync(SourceWindowIdentity source, CancellationToken cancellationToken)
        {
            Calls++;
            Completed = true;
            return ValueTask.FromResult(text);
        }
    }

    private sealed class FixedForeground(SourceWindowIdentity source) : IForegroundWindowCapture
    {
        public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken token = default) =>
            ValueTask.FromResult(new ForegroundCaptureResult(source, ForegroundCaptureError.None));
    }
}
