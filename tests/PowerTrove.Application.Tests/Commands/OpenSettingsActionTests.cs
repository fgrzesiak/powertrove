using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Application.Commands;
using PowerTrove.Application.ViewModels;
using PowerTrove.Core;
using PowerTrove.Core.Search;
using Xunit;

namespace PowerTrove.Application.Tests.Commands;

public class OpenSettingsActionTests
{
    private static ContextSnapshot Snapshot(ContextStatus status, ContextContent content) => new(
        Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.UtcNow),
        status, content, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "test",
        status == ContextStatus.Available ? null : "Raw provider text that must stay hidden");

    public static IEnumerable<object[]> Contexts() => new[]
    {
        new object[] { Snapshot(ContextStatus.Available, new FileSelection(new[] { new SelectedFile(@"C:\a.pdf", "a.pdf", ".pdf", "application/pdf", 1, true, true, null, false) })) },
        new object[] { Snapshot(ContextStatus.Available, new NoContent()) },
        new object[] { Snapshot(ContextStatus.Failed, new NoContent()) },
        new object[] { Snapshot(ContextStatus.Unsupported, new NoContent()) },
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task SettingsIsListedAndAvailableForEveryContext(ContextSnapshot snapshot)
    {
        var registry = new ActionRegistry();
        registry.Register(new OpenSettingsAction(() => { }));
        var engine = new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>());

        var results = await engine.SearchAsync("settings", snapshot, CancellationToken.None);

        var settings = Assert.Single(results);
        Assert.Equal(OpenSettingsAction.ActionId, settings.Action.Descriptor.Id);
        Assert.True(settings.IsCompatible);
    }

    [Fact]
    public async Task ExecuteInvokesTheCallbackOnce()
    {
        int opened = 0;
        var action = new OpenSettingsAction(() => opened++);

        var receipt = await action.ExecuteAsync(Snapshot(ContextStatus.Failed, new NoContent()), new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(1, opened);
        Assert.False(action.Descriptor.ChangesFiles);
    }

    [Fact]
    public void TitleIsLocalized()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Einstellungen", new OpenSettingsAction(() => { }).Descriptor.Title);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Settings", new OpenSettingsAction(() => { }).Descriptor.Title);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData(ContextStatus.Failed, "The selection could not be read.")]
    [InlineData(ContextStatus.Unsupported, "This application is not supported yet.")]
    [InlineData(ContextStatus.TimedOut, "did not respond in time")]
    public void UnavailableContextShowsLocalizedNoticeInsteadOfProviderText(ContextStatus status, string expected)
    {
        var registry = new ActionRegistry();
        var vm = new PaletteViewModel();

        vm.Initialize(Snapshot(status, new NoContent()), registry, new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>()));

        Assert.Contains(expected, vm.ContextNotice);
        Assert.DoesNotContain("Raw provider text", vm.ContextSummary);
        Assert.False(vm.IsContextAvailable);
    }

    [Fact]
    public void AvailableContextHasNoNotice()
    {
        var registry = new ActionRegistry();
        var vm = new PaletteViewModel();

        vm.Initialize(Snapshot(ContextStatus.Available, new NoContent()), registry, new ActionSearchEngine(registry, new Dictionary<string, IReadOnlyList<string>>()));

        Assert.Null(vm.ContextNotice);
        Assert.True(vm.IsContextAvailable);
    }
}
