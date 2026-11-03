using System;
using PowerTrove.Platform.Windows.Capture;
using Xunit;

namespace PowerTrove.Platform.Windows.Tests.Capture;

public class ExplorerTabsTests
{
    [Fact]
    public void TheActiveTabWinsOverEarlierTabsOfTheSameWindow() =>
        Assert.Equal(2, ExplorerTabs.Choose([0x101, 0x102, 0x103], activeTab: 0x103));

    [Fact]
    public void WithoutTabsTheOnlyWindowIsRead() =>
        Assert.Equal(0, ExplorerTabs.Choose([0], activeTab: 0));

    [Fact]
    public void AnUnknownActiveTabFallsBackToTheFirstWindow() =>
        Assert.Equal(0, ExplorerTabs.Choose([0x101, 0], activeTab: 0x999));

    [Fact]
    public void AnEmptyListIsRejected() =>
        Assert.Throws<ArgumentException>(() => ExplorerTabs.Choose([], activeTab: 0));
}
