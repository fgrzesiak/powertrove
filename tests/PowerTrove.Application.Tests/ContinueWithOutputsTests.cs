using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using PowerTrove.Abstractions;

namespace PowerTrove.Application.Tests;

public class ContinueWithOutputsTests
{
    private sealed class OutputAction(string path) : IAction
    {
        public ActionDescriptor Descriptor { get; } = new("powertrove.test.export",
            "Save", "", new HashSet<ContentKind> { ContentKind.Text }, ["save"], false, true);
        public ValueTask<ActionAvailability> MatchAsync(ContextSnapshot context, CancellationToken token) =>
            ValueTask.FromResult(new ActionAvailability(true));
        public ValueTask<ActionPreview> PreviewAsync(ContextSnapshot context,
            ActionParameters parameters, CancellationToken token) =>
            ValueTask.FromResult(new ActionPreview(false,null,[],[]));
        public Task<ActionReceipt> ExecuteAsync(ContextSnapshot context, ActionParameters parameters,
            IProgress<ActionProgress>? progress, CancellationToken token) =>
            Task.FromResult(new ActionReceipt(true,false,[path],[],false));
    }

    [Fact]
    public async Task CommittedFileCanBeUsedAsNewSelection()
    {
        string path=Path.Combine(Path.GetTempPath(),"powertrove-followup-"+Guid.NewGuid().ToString("N")+".md");
        File.WriteAllText(path,"# Follow-up");
        try
        {
            var registry=new ActionRegistry();
            registry.Register(new OutputAction(path));
            var engine=new ActionSearchEngine(registry);
            var vm=new PaletteViewModel();
            var now=DateTimeOffset.UtcNow;
            var snapshot=new ContextSnapshot(Guid.NewGuid(),
                new SourceWindowIdentity(IntPtr.Zero,0,"chrome",now),
                ContextStatus.Available,new TextSelection("selection"),
                now,now.AddMinutes(2),"test",null);
            vm.Initialize(snapshot,registry,engine);
            await vm.SearchTask;
            vm.SelectedAction=vm.FilteredActions.First(x=>x.Action.Descriptor.Id=="powertrove.test.export");
            await vm.ExecuteSelectedActionAsync();
            Assert.True(vm.CanContinueWithOutputs);
            vm.ContinueWithOutputs();
            var files=Assert.IsType<FileSelection>(vm.CurrentSnapshot?.Content);
            Assert.Single(files.Files);
            Assert.Equal(path,files.Files[0].Path);
            Assert.Equal("powertrove.provider.completed-output",vm.CurrentSnapshot?.ProviderId);
            Assert.False(vm.HasExecutionResult);
        }
        finally {File.Delete(path);}
    }

    [Fact]
    public void NoResultMeansNoFollowUp()
    {
        var vm=new PaletteViewModel();
        vm.ContinueWithOutputs();
        Assert.Null(vm.CurrentSnapshot);
    }
}
