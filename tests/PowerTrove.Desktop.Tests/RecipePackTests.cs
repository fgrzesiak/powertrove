using PowerTrove.Abstractions.Files;
using PowerTrove.Core.Workflows;
using PowerTrove.Desktop;

namespace PowerTrove.Desktop.Tests;

public sealed class RecipePackTests
{
    [Fact]
    public async Task MixedEmail_RegisteredRecipeUsesSizedImageBranchPdfBranchAndFinalZip()
    {
        var runner = new RecordingRunner();
        var services = FileActionServices.CreatePhysical(new UnusedVerifier());
        var recipe = new RecipePack(runner, services).Actions.Single(a => a.Descriptor.Id == "contextbar.recipe.mixed-for-email");
        var now = DateTimeOffset.UtcNow;
        var files = new[] { "image.jpg", "document.pdf", "other.txt" }.Select(name =>
            new SelectedFile(Path.Combine(Path.GetTempPath(), name), name, Path.GetExtension(name), null, 100, true, true, null)).ToArray();
        var context = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", now),
            ContextStatus.Available, new FileSelection(files), now, now.AddMinutes(5), "test", null);

        var preview = await recipe.PreviewAsync(context, new EmptyParameters(), CancellationToken.None);

        Assert.False(preview.IsBlocked);
        Assert.Equal(new[] { "contextbar.image.compress", "contextbar.pdf.compress", "contextbar.files.zip" }, runner.Steps.Select(s => s.ActionId));
        var image = Assert.IsType<DictionaryParameters>(runner.Steps[0].Parameters).Values;
        Assert.Equal("1600", image["MaxDimension"]);
        Assert.Equal("75", image["Quality"]);
        Assert.Equal("balanced", Assert.IsType<DictionaryParameters>(runner.Steps[1].Parameters).Values["Level"]);
        Assert.Equal(new[] { 1, 1, 3 }, runner.InputCounts);
    }

    private sealed class RecordingRunner : IWorkflowRunner
    {
        public List<WorkflowStep> Steps { get; } = new();
        public List<int> InputCounts { get; } = new();

        public ValueTask<ActionPreview> PreviewWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default)
        {
            Steps.Add(Assert.Single(workflow.Steps));
            InputCounts.Add(((FileSelection)context.Content).Files.Count);
            return new(new ActionPreview(true, null, Array.Empty<string>(), Array.Empty<string>()));
        }

        public ValueTask<ActionAvailability> ValidateWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ActionReceipt> ExecuteWorkflowAsync(WorkflowDefinition workflow, ContextSnapshot context, IProgress<ActionProgress>? progress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedVerifier : IFileTypeVerifier
    {
        public ValueTask<FileTypeVerification> VerifyAsync(string path, FileInputFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
