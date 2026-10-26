using ContextBar.Core.Activation;
using ContextBar.Platform.Windows.Activation;

namespace ContextBar.Platform.Windows.Tests.Activation;

public class ExplicitSelectionValidatorTests
{
    [Fact]
    public void ProducesAnExplicitSnapshotWithoutForegroundIdentityAndDeduplicates()
    {
        var validator = new ExplicitSelectionValidator(new Probe());
        var snapshot = validator.CreateSnapshot(new ExplicitSelectionMessage([@"C:\one.pdf", @"C:\ONE.pdf"]));
        Assert.NotNull(snapshot);
        Assert.Equal(ContextStatus.Available, snapshot.Status);
        Assert.Equal("explicit-selection", snapshot.ProviderId);
        Assert.Single(Assert.IsType<FileSelection>(snapshot.Content).Files);
        Assert.Equal(IntPtr.Zero, snapshot.Source.WindowHandle);
    }

    [Fact]
    public void OneUnavailableItemRejectsTheEntireSelection()
    {
        var validator = new ExplicitSelectionValidator(new Probe());
        Assert.Null(validator.CreateSnapshot(new ExplicitSelectionMessage([@"C:\one.pdf", @"C:\missing.pdf"])));
        Assert.Null(validator.CreateSnapshot(new ExplicitSelectionMessage([@"\\server\share\one.pdf"])));
    }

    [Fact]
    public void CancellationIsPreserved()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ExplicitSelectionValidator(new Probe())
            .CreateSnapshot(new ExplicitSelectionMessage([@"C:\one.pdf"]), cancelled.Token));
    }

    private sealed class Probe : IActivationFileProbe
    {
        public SelectedFile? ReadLocalFile(string path) => path.Contains("missing", StringComparison.Ordinal)
            ? null : new SelectedFile(path, "fixture.pdf", ".pdf", "application/pdf", 10, true, true, null);
    }
}
