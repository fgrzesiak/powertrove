using System.Text;
using ContextBar.Core.Extensibility.PackProtocol;

namespace ContextBar.Core.Tests.Extensibility;

public sealed class PackLineDecoderTests
{
    [Fact]
    public void EverySplitBoundary_IncludingInsideUtf8AndCrlf_ProducesOneMessage()
    {
        byte[] line = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"code\":\"internal\",\"message\":\"Grüße\"}\r\n");
        for (int split = 0; split <= line.Length; split++)
        {
            var decoder = new PackLineDecoder();
            var messages = decoder.Append(line.AsSpan(0, split)).Concat(decoder.Append(line.AsSpan(split))).ToArray();
            Assert.Equal("Grüße", Assert.IsType<ErrorMessage>(Assert.Single(messages)).Message);
            Assert.Null(decoder.Complete());
        }
    }

    [Fact]
    public void MultipleLines_PreserveOrderAndIgnoreUnknownTypes()
    {
        var decoder = new PackLineDecoder();
        byte[] lines = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"protocolVersion\":1,\"hostVersion\":\"0.1.0\"}\n{\"type\":\"future\"}\n{\"type\":\"shutdown\"}\n");
        var messages = decoder.Append(lines);
        Assert.Equal(new[] { "hello", "shutdown" }, messages.Select(m => m.Type));
        Assert.Null(decoder.Complete());
        Assert.Equal(PackProtocolError.SessionClosed, Assert.Throws<PackProtocolException>(() => decoder.Append(lines)).Code);
    }

    [Fact]
    public void FatalLine_PermanentlyClosesSession_InsteadOfResynchronizing()
    {
        var decoder = new PackLineDecoder();
        Assert.Throws<PackProtocolException>(() => decoder.Append(Encoding.UTF8.GetBytes("broken\n{\"type\":\"shutdown\"}\n")));
        Assert.Equal(PackProtocolError.SessionClosed, Assert.Throws<PackProtocolException>(() => decoder.Append(Encoding.UTF8.GetBytes("{\"type\":\"shutdown\"}\n"))).Code);
        Assert.Equal(PackProtocolError.SessionClosed, Assert.Throws<PackProtocolException>(() => decoder.Complete()).Code);
    }

    [Fact]
    public void OversizedUnterminatedLine_IsRejectedDuringAppend_NotOnlyAtEof()
    {
        var decoder = new PackLineDecoder();
        var chunk = Enumerable.Repeat((byte)' ', 65536).ToArray();
        for (int i = 0; i < PackProtocolLimits.MaximumLineBytes / chunk.Length; i++) Assert.Empty(decoder.Append(chunk));
        Assert.Equal(PackProtocolError.LineTooLarge, Assert.Throws<PackProtocolException>(() => decoder.Append(new byte[] { (byte)' ' })).Code);
        Assert.Equal(PackProtocolError.SessionClosed, Assert.Throws<PackProtocolException>(() => decoder.Complete()).Code);
    }

    [Fact]
    public void MaximumPayload_AllowsSplitCrlfWithoutCountingItAgainstTheLimit()
    {
        const string json = "{\"type\":\"future\"}";
        byte[] payload = Encoding.UTF8.GetBytes(json + new string(' ', PackProtocolLimits.MaximumLineBytes - json.Length));
        var decoder = new PackLineDecoder();
        Assert.Empty(decoder.Append(payload));
        Assert.Empty(decoder.Append(new byte[] { (byte)'\r' }));
        Assert.Empty(decoder.Append(new byte[] { (byte)'\n' }));
        Assert.Null(decoder.Complete());
    }

    [Fact]
    public void Eof_AcceptsCompleteFinalJson_ButRejectsTruncationAndBlankLines()
    {
        var decoder = new PackLineDecoder();
        Assert.Empty(decoder.Append(Encoding.UTF8.GetBytes("{\"type\":\"shutdown\"}")));
        Assert.IsType<ShutdownMessage>(decoder.Complete());
        decoder = new PackLineDecoder();
        decoder.Append(Encoding.UTF8.GetBytes("{\"type\":\"shutdown\""));
        Assert.Equal(PackProtocolError.MalformedJson, Assert.Throws<PackProtocolException>(() => decoder.Complete()).Code);
        Assert.Equal(PackProtocolError.SessionClosed, Assert.Throws<PackProtocolException>(() => decoder.Complete()).Code);
        Assert.Throws<PackProtocolException>(() => new PackLineDecoder().Append(new byte[] { (byte)'\n' }));
    }
}
