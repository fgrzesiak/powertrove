using PowerTrove.Abstractions;
using PowerTrove.Core.Extensibility.PackProtocol;
using PowerTrove.PackHost;

namespace PowerTrove.PackHost.Tests;

public sealed class WorkerBrowserBinaryPendingTests
{
    [Fact]
    public async Task OrderedChunksProduceCompleteBinaryPageContent()
    {
        var id = Guid.NewGuid();
        using var pending = new WorkerBrowserBinaryPending(id,"image",
            BrowserPageOperation.CaptureVisiblePng);
        pending.Append(new BrowserServiceBinaryChunkMessage(Guid.NewGuid(),id,0,false,
            "image",Convert.ToBase64String(new byte[] {137,80,78,71})));
        pending.Append(new BrowserServiceBinaryChunkMessage(Guid.NewGuid(),id,1,true,
            "image",Convert.ToBase64String(new byte[] {13,10,26,10})));
        var completed = await pending.Result;
        Assert.Equal(BrowserPageOperation.CaptureVisiblePng,completed!.Operation);
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10},completed.Binary);
    }

    [Fact]
    public async Task OutOfOrderChunksFailWithoutPartialContent()
    {
        var id = Guid.NewGuid();
        using var pending = new WorkerBrowserBinaryPending(id,"image",
            BrowserPageOperation.CaptureFullPng);
        pending.Append(new BrowserServiceBinaryChunkMessage(Guid.NewGuid(),id,1,true,
            "image","AQ=="));
        Assert.Null(await pending.Result);
    }

    [Fact]
    public async Task WrongCaptureKindFailsClosed()
    {
        var id = Guid.NewGuid();
        using var pending = new WorkerBrowserBinaryPending(id,"image",
            BrowserPageOperation.CaptureVisiblePng);
        pending.Append(new BrowserServiceBinaryChunkMessage(Guid.NewGuid(),id,0,true,
            "mhtml","AQ=="));
        Assert.Null(await pending.Result);
    }

    [Fact]
    public async Task ExplicitFailureRejectsIncompleteBytes()
    {
        var id=Guid.NewGuid();
        using var pending = new WorkerBrowserBinaryPending(id,"mhtml",
            BrowserPageOperation.CaptureMhtml);
        pending.Append(new BrowserServiceBinaryChunkMessage(Guid.NewGuid(),id,0,false,
            "mhtml","AQ=="));
        pending.Fail();
        Assert.Null(await pending.Result);
    }
}
