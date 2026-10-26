using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Core;
using ContextBar.SampleAdapters.Providers;
using ContextBar.Abstractions.Testing;
using Xunit;

namespace ContextBar.SampleAdapters.Tests;

public class SampleTextProviderContractTests : ProviderContractTestsBase
{
    protected override IContextProvider CreateProvider() => new SampleTextProvider();

    protected override SourceWindowIdentity CreateSupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow);

    protected override SourceWindowIdentity CreateUnsupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "winword.exe", DateTimeOffset.UtcNow);
}

public class SampleTableProviderContractTests : ProviderContractTestsBase
{
    protected override IContextProvider CreateProvider() => new SampleTableProvider();

    protected override SourceWindowIdentity CreateSupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "excel.exe", DateTimeOffset.UtcNow);

    protected override SourceWindowIdentity CreateUnsupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow);
}

public class SampleFileProviderContractTests : ProviderContractTestsBase
{
    protected override IContextProvider CreateProvider() => new SampleFileProvider();

    protected override SourceWindowIdentity CreateSupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "explorer.exe", DateTimeOffset.UtcNow);

    protected override SourceWindowIdentity CreateUnsupportedSource()
        => new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow);
}

public class SampleProvidersTests
{
    [Fact]
    public async Task SampleTextProvider_CanCapture_Notepad()
    {
        var provider = new SampleTextProvider();
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow);

        var canCapture = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.True(canCapture);
    }

    [Fact]
    public async Task SampleTextProvider_CannotCapture_OtherApp()
    {
        var provider = new SampleTextProvider();
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "winword.exe", DateTimeOffset.UtcNow);

        var canCapture = await provider.CanCaptureAsync(source, CancellationToken.None);

        Assert.False(canCapture);
    }

    [Fact]
    public async Task SampleTextProvider_CaptureAsync_ReturnsTextSelection()
    {
        var provider = new SampleTextProvider();
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "notepad.exe", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var textContent = Assert.IsType<TextSelection>(snapshot.Content);
        Assert.Contains("Notepad", textContent.Text);
    }

    [Fact]
    public async Task SampleTableProvider_CaptureAsync_ReturnsTableData()
    {
        var provider = new SampleTableProvider();
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "excel.exe", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var tableContent = Assert.IsType<TableData>(snapshot.Content);
        Assert.Equal(4, tableContent.Rows.Count);
        Assert.Equal("Alice", ((TextCell)tableContent.Rows[1][0]).Value);
    }

    [Fact]
    public async Task SampleFileProvider_CaptureAsync_ReturnsFileSelection()
    {
        var provider = new SampleFileProvider();
        var source = new SourceWindowIdentity(IntPtr.Zero, 1, "explorer.exe", DateTimeOffset.UtcNow);
        var invocationId = Guid.NewGuid();

        var snapshot = await provider.CaptureAsync(invocationId, source, CancellationToken.None);

        Assert.Equal(ContextStatus.Available, snapshot.Status);
        var fileContent = Assert.IsType<FileSelection>(snapshot.Content);
        Assert.Equal(2, fileContent.Files.Count);
        Assert.Equal("document.pdf", fileContent.Files[0].Name);
    }
}
