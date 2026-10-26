using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using PowerTrove.Core;
using PowerTrove.Core.Workflows;
using System.IO;

namespace PowerTrove.Core.Tests.Workflows;

public class WorkflowRunnerTests
{
    private readonly Mock<IActionRegistry> _registryMock;

    public WorkflowRunnerTests()
    {
        _registryMock = new Mock<IActionRegistry>();
    }

    /// <summary>Deterministic file-to-file step: previews "&lt;input&gt;&lt;suffix&gt;" for each input.</summary>
    private sealed class SuffixAction : IAction, IWorkflowStepPlanner
    {
        private readonly string _suffix;
        private readonly string _verb;

        public SuffixAction(string id, string suffix, string verb)
        {
            _suffix = suffix;
            _verb = verb;
            Descriptor = new ActionDescriptor(id, id, id, new HashSet<ContentKind> { ContentKind.Files }, Array.Empty<string>(), true, true);
        }

        public ActionDescriptor Descriptor { get; }

        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken cancellationToken) =>
            new(new ActionAvailability(context.Content is FileSelection));

        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context, ActionParameters parameters, CancellationToken cancellationToken)
        {
            var files = ((FileSelection)context.Content).Files;
            return new(new ActionPreview(true, $"{_verb} {files.Count} file(s).", files.Select(f => f.Path + _suffix).ToList(), Array.Empty<string>()));
        }

        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters, IProgress<ActionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionReceipt(true, false, Array.Empty<string>(), Array.Empty<ActionFailure>(), false));

        public ValueTask<ActionPreview> PlanAsync(ContextSnapshot projectedContext, ActionParameters parameters, CancellationToken cancellationToken) =>
            PreviewAsync(projectedContext, parameters, cancellationToken);
    }

    [Fact]
    public async Task ValidateWorkflow_EmptySteps_ReturnsUnavailable()
    {
        var runner = new WorkflowRunner(_registryMock.Object);
        var workflow = new WorkflowDefinition("test1", "Test", "Test", new List<WorkflowStep>());
        var context = CreateFileContext();

        var result = await runner.ValidateWorkflowAsync(workflow, context);

        Assert.False(result.IsAvailable);
        Assert.Contains("no steps", result.Reason);
    }

    [Fact]
    public async Task ValidateWorkflow_MissingAction_ReturnsUnavailable()
    {
        var runner = new WorkflowRunner(_registryMock.Object);
        var workflow = new WorkflowDefinition("test1", "Test", "Test", new List<WorkflowStep>
        {
            new WorkflowStep("missing.action", new EmptyParameters())
        });
        var context = CreateFileContext();

        _registryMock.Setup(r => r.GetAction("missing.action")).Returns((IAction?)null);

        var result = await runner.ValidateWorkflowAsync(workflow, context);

        Assert.False(result.IsAvailable);
        Assert.Contains("not found", result.Reason);
    }

    [Fact]
    public async Task ValidateWorkflow_IncompatibleContext_ReturnsUnavailable()
    {
        var runner = new WorkflowRunner(_registryMock.Object);
        var actionMock = new Mock<IAction>();
        actionMock.Setup(a => a.MatchAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(false, "Incompatible context"));

        _registryMock.Setup(r => r.GetAction("test.action")).Returns(actionMock.Object);

        var workflow = new WorkflowDefinition("test1", "Test", "Test", new List<WorkflowStep>
        {
            new WorkflowStep("test.action", new EmptyParameters())
        });

        var context = CreateTextContext();
        var result = await runner.ValidateWorkflowAsync(workflow, context);

        Assert.False(result.IsAvailable);
        Assert.Contains("Incompatible context", result.Reason);
    }

    [Fact]
    public async Task ValidateWorkflow_ValidChain_ReturnsAvailable()
    {
        var runner = new WorkflowRunner(_registryMock.Object);

        _registryMock.Setup(r => r.GetAction("action.1")).Returns(new SuffixAction("action.1", ".webp", "Convert"));
        _registryMock.Setup(r => r.GetAction("action.2")).Returns(new SuffixAction("action.2", ".webp", "Resize"));

        var workflow = new WorkflowDefinition("test1", "Test", "Test", new List<WorkflowStep>
        {
            new WorkflowStep("action.1", new EmptyParameters()),
            new WorkflowStep("action.2", new EmptyParameters())
        });

        var context = CreateFileContext();
        var result = await runner.ValidateWorkflowAsync(workflow, context);

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public async Task PreviewWorkflow_ChainOutputsPassed_ReturnsCombinedPreview()
    {
        var runner = new WorkflowRunner(_registryMock.Object);

        var convertAction = new SuffixAction("test.convert", ".webp", "Will convert");
        var resizeAction = new SuffixAction("test.resize", ".resized.webp", "Will resize");

        _registryMock.Setup(r => r.GetAction(convertAction.Descriptor.Id)).Returns(convertAction);
        _registryMock.Setup(r => r.GetAction(resizeAction.Descriptor.Id)).Returns(resizeAction);

        var workflow = new WorkflowDefinition("chain1", "Convert then Resize", "Test", new List<WorkflowStep>
        {
            new WorkflowStep(convertAction.Descriptor.Id, new EmptyParameters()),
            new WorkflowStep(resizeAction.Descriptor.Id, new EmptyParameters())
        });

        var context = CreateFileContext(@"C:\test\image1.png");

        var result = await runner.PreviewWorkflowAsync(workflow, context);

        Assert.True(result.RequiresConfirmation);
        Assert.False(result.IsBlocked);
        Assert.Contains("image1.png.webp", string.Join(" ", result.ExpectedOutputs));
        Assert.Contains("Will convert", result.PreviewText);
        Assert.Contains("Will resize", result.PreviewText);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("capability")]
    [InlineData("unavailable")]
    [InlineData("invalid-options")]
    [InlineData("no-planner")]
    public async Task PreviewWorkflow_InvalidLaterStep_BlocksWithoutOpeningIntermediateFiles(string failure)
    {
        var convert = new SuffixAction("convert", ".webp", "Convert");
        var later = new Mock<IAction>(MockBehavior.Strict);
        later.SetupGet(a => a.Descriptor).Returns(new ActionDescriptor(
            "later", "Later", "", new HashSet<ContentKind>
            {
                failure == "table" ? ContentKind.Table : ContentKind.Files
            }, Array.Empty<string>(), true, true,
            RequiredCapabilities: failure == "capability" ? new HashSet<string> { "captured-only" } : null));

        var parameters = new DictionaryParameters(new Dictionary<string, string> { ["option"] = "invalid" });
        if (failure != "no-planner")
        {
            later.As<IWorkflowStepPlanner>()
                .Setup(p => p.PlanAsync(It.IsAny<ContextSnapshot>(), parameters, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActionPreview(false, null, Array.Empty<string>(), new[] { failure }, true));
        }

        _registryMock.Setup(r => r.GetAction("convert")).Returns(convert);
        _registryMock.Setup(r => r.GetAction("later")).Returns(later.Object);
        var workflow = new WorkflowDefinition("chain", "Chain", "", new[]
        {
            new WorkflowStep("convert", new EmptyParameters()),
            new WorkflowStep("later", parameters)
        });
        var context = CreateFileContext() with { ProvidedCapabilities = new HashSet<string> { "captured-only" } };
        var runner = new WorkflowRunner(_registryMock.Object);

        var preview = await runner.PreviewWorkflowAsync(workflow, context);
        var availability = await runner.ValidateWorkflowAsync(workflow, context);

        Assert.True(preview.IsBlocked);
        Assert.NotEmpty(preview.Warnings);
        Assert.False(availability.IsAvailable);
        later.Verify(a => a.MatchAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        later.Verify(a => a.PreviewAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        later.Verify(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        if (failure is "unavailable" or "invalid-options")
        {
            later.As<IWorkflowStepPlanner>().Verify(p => p.PlanAsync(
                It.Is<ContextSnapshot>(c => ((FileSelection)c.Content).Files[0].Extension == ".webp" && c.ProvidedCapabilities == null),
                parameters, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        if (failure is "table" or "capability")
        {
            later.As<IWorkflowStepPlanner>().Verify(p => p.PlanAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task PreviewWorkflow_ThirdStepReceivesPlannedSecondStepOutputs()
    {
        var first = new SuffixAction("first", ".webp", "Convert");
        var second = new SuffixAction("second", ".png", "Convert again");
        var third = new Mock<IAction>(MockBehavior.Strict);
        third.SetupGet(a => a.Descriptor).Returns(first.Descriptor with { Id = "third" });
        third.As<IWorkflowStepPlanner>().Setup(p => p.PlanAsync(
            It.Is<ContextSnapshot>(c => ((FileSelection)c.Content).Files[0].Path!.EndsWith(".webp.png")),
            It.IsAny<ActionParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionPreview(false, "Third step", Array.Empty<string>(), Array.Empty<string>()));
        _registryMock.Setup(r => r.GetAction("first")).Returns(first);
        _registryMock.Setup(r => r.GetAction("second")).Returns(second);
        _registryMock.Setup(r => r.GetAction("third")).Returns(third.Object);
        var workflow = new WorkflowDefinition("chain", "Chain", "", new[]
        {
            new WorkflowStep("first", new EmptyParameters()),
            new WorkflowStep("second", new EmptyParameters()),
            new WorkflowStep("third", new EmptyParameters())
        });

        var preview = await new WorkflowRunner(_registryMock.Object).PreviewWorkflowAsync(workflow, CreateFileContext());

        Assert.False(preview.IsBlocked);
        Assert.Contains("Third step", preview.PreviewText);
        third.As<IWorkflowStepPlanner>().VerifyAll();
    }

    [Fact]
    public async Task PreviewWorkflow_Canceled_DoesNotInspectActions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = new WorkflowDefinition("chain", "Chain", "", new[]
        {
            new WorkflowStep("first", new EmptyParameters())
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new WorkflowRunner(_registryMock.Object).PreviewWorkflowAsync(workflow, CreateFileContext(), cancellation.Token));

        _registryMock.Verify(r => r.GetAction(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteWorkflow_ChainExecutesSequentially()
    {
        var runner = new WorkflowRunner(_registryMock.Object);

        var action1Mock = new Mock<IAction>();
        var action2Mock = new Mock<IAction>();

        action1Mock.Setup(a => a.Descriptor).Returns(new ActionDescriptor("action.1", "A1", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));
        action2Mock.Setup(a => a.Descriptor).Returns(new ActionDescriptor("action.2", "A2", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));

        action1Mock.Setup(a => a.MatchAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(true));
        action2Mock.Setup(a => a.MatchAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionAvailability(true));

        action1Mock.Setup(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(true, false, new[] { "C:\\out1.txt" }, Array.Empty<ActionFailure>(), false));

        action2Mock.Setup(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(true, false, new[] { "C:\\out2.txt" }, Array.Empty<ActionFailure>(), false));

        _registryMock.Setup(r => r.GetAction("action.1")).Returns(action1Mock.Object);
        _registryMock.Setup(r => r.GetAction("action.2")).Returns(action2Mock.Object);

        var workflow = new WorkflowDefinition("w1", "Test workflow", "", new List<WorkflowStep>
        {
            new WorkflowStep("action.1", new EmptyParameters()),
            new WorkflowStep("action.2", new EmptyParameters())
        });

        var context = CreateFileContext();
        var result = await runner.ExecuteWorkflowAsync(workflow, context, null);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("C:\\out1.txt", result.OutputPaths);
        Assert.Contains("C:\\out2.txt", result.OutputPaths);

        action1Mock.Verify(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        action2Mock.Verify(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteWorkflow_StepFails_StopsExecution()
    {
        var runner = new WorkflowRunner(_registryMock.Object);

        var action1Mock = new Mock<IAction>();
        var action2Mock = new Mock<IAction>();

        action1Mock.Setup(a => a.Descriptor).Returns(new ActionDescriptor("action.1", "A1", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));
        action2Mock.Setup(a => a.Descriptor).Returns(new ActionDescriptor("action.2", "A2", "", new HashSet<ContentKind>(), Array.Empty<string>(), false, false));

        action1Mock.Setup(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionReceipt(false, false, Array.Empty<string>(), new[] { new ActionFailure(null, "Failed") }, false));

        _registryMock.Setup(r => r.GetAction("action.1")).Returns(action1Mock.Object);
        _registryMock.Setup(r => r.GetAction("action.2")).Returns(action2Mock.Object);

        var workflow = new WorkflowDefinition("w1", "Test workflow", "", new List<WorkflowStep>
        {
            new WorkflowStep("action.1", new EmptyParameters()),
            new WorkflowStep("action.2", new EmptyParameters())
        });

        var context = CreateFileContext();
        var result = await runner.ExecuteWorkflowAsync(workflow, context, null);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Failures);

        action2Mock.Verify(a => a.ExecuteAsync(It.IsAny<ContextSnapshot>(), It.IsAny<ActionParameters>(), It.IsAny<IProgress<ActionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private ContextSnapshot CreateTextContext()
    {
        return new ContextSnapshot(
            Guid.NewGuid(),
            new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now),
            ContextStatus.Available,
            new TextSelection("test text"),
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddMinutes(5),
            "test.provider",
            null
        );
    }
}
