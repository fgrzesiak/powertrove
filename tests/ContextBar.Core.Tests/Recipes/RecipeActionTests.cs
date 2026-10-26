using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Recipes;
using ContextBar.Core.Recipes;
using ContextBar.Core.Workflows;
using Moq;
using Xunit;

namespace ContextBar.Core.Tests.Recipes;

public class RecipeActionTests
{
    private readonly Mock<IWorkflowRunner> _runnerMock;
    private readonly FileActionServices _services;
    private readonly RecipeDefinition _recipe;

    public RecipeActionTests()
    {
        _runnerMock = new Mock<IWorkflowRunner>();
        _services = FileActionServices.CreatePhysical(new Mock<IFileTypeVerifier>().Object);

        _recipe = new RecipeDefinition(
            "test.recipe", "Test Recipe", "A test recipe",
            SelectionRequirements.Any(),
            new List<RecipeStep>
            {
                new RecipeStep("action.one", new Dictionary<string, string> { { "P1", "V1" } }),
                new RecipeStep("action.two", new Dictionary<string, string> { { "P2", "V2" } })
            }
        );
    }

    [Fact]
    public async Task MatchAsync_ValidatesWithRunner()
    {
        var action = new RecipeAction(_recipe, _runnerMock.Object, _services);
        var context = CreateFileContext();

        _runnerMock.Setup(r => r.ValidateWorkflowAsync(It.IsAny<WorkflowDefinition>(), context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(true));

        var result = await action.MatchAsync(context, CancellationToken.None);

        Assert.True(result.IsAvailable);
        _runnerMock.Verify(r => r.ValidateWorkflowAsync(It.Is<WorkflowDefinition>(w => w.Id == "test.recipe"), context, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PreviewAsync_DelegatesToRunner()
    {
        var action = new RecipeAction(_recipe, _runnerMock.Object, _services);
        var context = CreateFileContext();
        var expectedPreview = new ActionPreview(false, "Preview", new[] { "out.txt" }, Array.Empty<string>());

        _runnerMock.Setup(r => r.PreviewWorkflowAsync(It.IsAny<WorkflowDefinition>(), context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPreview);

        var result = await action.PreviewAsync(context, new EmptyParameters(), CancellationToken.None);

        Assert.Equal(expectedPreview, result);
    }

    [Fact]
    public async Task ExecuteAsync_SplitsWorkflowAndSetsOverride()
    {
        var action = new RecipeAction(_recipe, _runnerMock.Object, _services);
        var context = CreateFileContext();

        string? overrideDuringIntermediate = null;

        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.Is<WorkflowDefinition>(w => w.Steps.Count == 1 && w.Steps[0].ActionId == "action.one"), context, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(true, false, new[] { "temp/out1.txt" }, Array.Empty<ActionFailure>(), false))
            .Callback(() => overrideDuringIntermediate = _services.OutputDirectoryOverride.Value);

        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.Is<WorkflowDefinition>(w => w.Steps.Count == 1 && w.Steps[0].ActionId == "action.two"), It.IsAny<ContextSnapshot>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(true, false, new[] { "final/out2.txt" }, Array.Empty<ActionFailure>(), false));

        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.OutputPaths);
        Assert.Equal("final/out2.txt", result.OutputPaths[0]);

        Assert.NotNull(overrideDuringIntermediate);
        Assert.Contains("recipes", overrideDuringIntermediate);

        // Assert temp directory was cleaned up
        Assert.False(Directory.Exists(overrideDuringIntermediate));
        Assert.Null(_services.OutputDirectoryOverride.Value);
    }


    [Fact]
    public async Task ExecuteAsync_CleansUpOnFailure()
    {
        var action = new RecipeAction(_recipe, _runnerMock.Object, _services);
        var context = CreateFileContext();
        string? tempDir = null;

        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.Is<WorkflowDefinition>(w => w.Steps.Count == 1 && w.Steps[0].ActionId == "action.one"), context, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, "Failed step") }, false))
            .Callback(() => tempDir = _services.OutputDirectoryOverride.Value);

        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(tempDir);
        Assert.False(Directory.Exists(tempDir));
    }

    [Fact]
    public async Task ExecuteAsync_CleansUpOnCancellation()
    {
        var action = new RecipeAction(_recipe, _runnerMock.Object, _services);
        var context = CreateFileContext();
        string? tempDir = null;

        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.Is<WorkflowDefinition>(w => w.Steps.Count == 1 && w.Steps[0].ActionId == "action.one"), context, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(false, true, Array.Empty<string>(), Array.Empty<ActionFailure>(), false))
            .Callback(() => tempDir = _services.OutputDirectoryOverride.Value);

        var result = await action.ExecuteAsync(context, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(result.IsCanceled);
        Assert.NotNull(tempDir);
        Assert.False(Directory.Exists(tempDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FamilyRecipe_AggregatesBothBranchesAndPassthrough(bool skipPdf)
    {
        var recipe = FamilyRecipe();
        var context = MixedContext();
        var seen = new List<string>();
        string? temporaryDirectory = null;
        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.IsAny<WorkflowDefinition>(), It.IsAny<ContextSnapshot>(), null, It.IsAny<CancellationToken>()))
            .Returns((WorkflowDefinition workflow, ContextSnapshot input, IProgress<ActionProgress>? _, CancellationToken ct) =>
            {
                var step = Assert.Single(workflow.Steps);
                var files = ((FileSelection)input.Content).Files;
                seen.Add(step.ActionId);
                if (step.ActionId == "archive")
                {
                    Assert.Equal(new[] { "processed.png", skipPdf ? "input.pdf" : "processed.pdf", "other.txt" }, files.Select(f => Path.GetFileName(f.Path)));
                    Assert.Null(input.ProvidedCapabilities);
                    Assert.Equal(Path.GetDirectoryName(((FileSelection)context.Content).Files[0].Path), _services.OutputDirectoryOverride.Value);
                    return Task.FromResult(new ActionReceipt(true, false, new[] { "final.zip" }, Array.Empty<ActionFailure>(), false));
                }

                temporaryDirectory = _services.OutputDirectoryOverride.Value;
                Assert.NotNull(temporaryDirectory);
                var file = Assert.Single(files);
                Assert.Equal(step.ActionId == "images" ? "image" : "pdf", SelectionFamilies.Of(file));
                var outputs = step.ActionId == "pdfs" && skipPdf ? Array.Empty<string>() : new[] { Path.Combine(temporaryDirectory!, "processed" + file.Extension) };
                return Task.FromResult(new ActionReceipt(true, false, outputs, Array.Empty<ActionFailure>(), false));
            });

        var receipt = await new RecipeAction(recipe, _runnerMock.Object, _services).ExecuteAsync(context, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(new[] { "final.zip" }, receipt.OutputPaths);
        Assert.Equal(new[] { "images", "pdfs", "archive" }, seen);
        Assert.False(Directory.Exists(temporaryDirectory));
        Assert.Null(_services.OutputDirectoryOverride.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FamilyRecipe_PreviewsExistingFamilyInputsThenAggregation(bool blockPdf)
    {
        var context = MixedContext();
        var seen = new List<string>();
        _runnerMock.Setup(r => r.PreviewWorkflowAsync(It.IsAny<WorkflowDefinition>(), It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns((WorkflowDefinition workflow, ContextSnapshot input, CancellationToken ct) =>
            {
                var step = Assert.Single(workflow.Steps);
                seen.Add(step.ActionId);
                var files = ((FileSelection)input.Content).Files;
                if (step.ActionId == "archive") Assert.Equal(3, files.Count);
                else Assert.Equal(step.ActionId == "images" ? "image" : "pdf", SelectionFamilies.Of(Assert.Single(files)));
                Assert.All(files, f => Assert.Contains(f, ((FileSelection)context.Content).Files));
                return new ValueTask<ActionPreview>(new ActionPreview(true, step.ActionId, new[] { "planned.zip" }, Array.Empty<string>(), blockPdf && step.ActionId == "pdfs"));
            });

        var preview = await new RecipeAction(FamilyRecipe(), _runnerMock.Object, _services).PreviewAsync(context, new EmptyParameters(), CancellationToken.None);

        Assert.Equal(blockPdf, preview.IsBlocked);
        Assert.Equal(new[] { "planned.zip" }, preview.ExpectedOutputs);
        Assert.Equal(new[] { "images", "pdfs", "archive" }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FamilyRecipe_FailedOrCanceledBranchDoesNotAggregate(bool canceled)
    {
        string? temporaryDirectory = null;
        _runnerMock.Setup(r => r.ExecuteWorkflowAsync(It.IsAny<WorkflowDefinition>(), It.IsAny<ContextSnapshot>(), null, It.IsAny<CancellationToken>()))
            .Callback(() => temporaryDirectory = _services.OutputDirectoryOverride.Value)
            .ReturnsAsync(new ActionReceipt(false, canceled, new[] { "intermediate.png" },
                canceled ? Array.Empty<ActionFailure>() : new[] { new ActionFailure(null, "failed") }, false));
        var receipt = await new RecipeAction(FamilyRecipe(), _runnerMock.Object, _services).ExecuteAsync(MixedContext(), new EmptyParameters(), null, CancellationToken.None);

        Assert.False(receipt.IsSuccess);
        Assert.Equal(canceled, receipt.IsCanceled);
        Assert.Empty(receipt.OutputPaths);
        Assert.False(Directory.Exists(temporaryDirectory));
        Assert.Null(_services.OutputDirectoryOverride.Value);
        _runnerMock.Verify(r => r.ExecuteWorkflowAsync(It.IsAny<WorkflowDefinition>(), It.IsAny<ContextSnapshot>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RecipeDefinition FamilyRecipe() => new("mixed", "Mixed", "", SelectionRequirements.Any(), new[]
    {
        new RecipeStep("images", new Dictionary<string, string>(), "image"),
        new RecipeStep("pdfs", new Dictionary<string, string>(), "pdf"),
        new RecipeStep("archive", new Dictionary<string, string>())
    });

    private ContextSnapshot MixedContext()
    {
        var context = CreateFileContext();
        var directory = Path.GetDirectoryName(((FileSelection)context.Content).Files[0].Path)!;
        return context with { Content = new FileSelection(new[] { "input.png", "input.pdf", "other.txt" }
            .Select(name => new SelectedFile(Path.Combine(directory, name), name, Path.GetExtension(name), null, 100, true, true, null)).ToArray()) };
    }

    private ContextSnapshot CreateFileContext(string path = "C:\\test\\image1.png")
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new FileSelection(new[]
            {
                new SelectedFile(path, Path.GetFileName(path) ?? "unknown", Path.GetExtension(path) ?? "", "image/png", 1000, true, true, null)
            }),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(5),
            "test.provider",
            null
        );
    }
}
