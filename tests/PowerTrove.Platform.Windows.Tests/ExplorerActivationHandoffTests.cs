using PowerTrove.Application;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Activation;
using PowerTrove.Core.Search;
using PowerTrove.Platform.Windows.Activation;

namespace PowerTrove.Platform.Windows.Tests;

public class ExplorerActivationHandoffTests
{
    [Fact]
    public async Task ExplicitCommandOpensExactlyItsFileWithoutForegroundCaptureOrActionExecution()
    {
        const string path = @"C:\Generated fixtures\Grüße & one.pdf";
        var message = Assert.IsType<ExplicitSelectionMessage>(ActivationProtocol.ParseArguments(["--open-selection", path]));
        var snapshot = new ExplicitSelectionValidator(new Probe()).CreateSnapshot(message);
        Assert.NotNull(snapshot);
        var action = new FileAction();
        var registry = new ActionRegistry();
        registry.Register(action);
        var search = new ActionSearchEngine(registry);
        var palette = new PaletteViewModel();
        var coordinator = new InvocationCoordinator(null, new ForbiddenForeground(),
            new ContextResolver(Array.Empty<IContextProvider>()), palette, registry, search);

        Assert.True(await coordinator.ActivateSnapshotAsync(snapshot));

        var file = Assert.Single(Assert.IsType<FileSelection>(palette.CurrentSnapshot!.Content).Files);
        Assert.Equal(path, file.Path);
        Assert.Equal(IntPtr.Zero, palette.CurrentSnapshot.Source.WindowHandle);
        Assert.Contains(palette.FilteredActions, result => result.Action.Descriptor.Id == action.Descriptor.Id && result.IsCompatible);
        Assert.Equal(0, action.Executions);
        Assert.False(palette.IsExecuting);
    }

    private sealed class Probe : IActivationFileProbe
    {
        public SelectedFile? ReadLocalFile(string path) => new(path, "generated.pdf", ".pdf", "application/pdf", 10, true, true, null);
    }

    private sealed class ForbiddenForeground : IForegroundWindowCapture
    {
        public ValueTask<ForegroundCaptureResult> CaptureForegroundAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Explicit selection must not recapture another window.");
    }

    private sealed class FileAction : IAction
    {
        public int Executions { get; private set; }
        public ActionDescriptor Descriptor { get; } = new("powertrove.test.explicit-file", "Fixture action", "Fixture description",
            new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), RequiresPreview: true, ChangesFiles: true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) => new(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken) =>
            new(new ActionPreview(false, null, Array.Empty<string>(), Array.Empty<string>()));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken)
        {
            Executions++;
            throw new InvalidOperationException("Activation must not execute actions.");
        }
    }
}
