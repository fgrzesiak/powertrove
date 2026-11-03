using System.Buffers.Binary;
using System.Text;
using PowerTrove.BrowserHost;

namespace PowerTrove.BrowserHost.Tests;

public sealed class NativeMessagingTests
{
    [Fact]
    public async Task ValidSelectedTextRoundTripsFraming()
    {
        var json = Encoding.UTF8.GetBytes("{\"type\":\"openSelection\",\"text\":\"Grüße 🌍\",\"url\":\"https://example.org/test\",\"title\":\"Example\"}");
        using var bytes = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        bytes.Write(header);
        bytes.Write(json);
        bytes.Position = 0;
        var selection = await NativeMessagingProtocol.ReadAsync(bytes, default);
        Assert.NotNull(selection);
        Assert.Equal("Grüße 🌍", selection.Text);
        using var response = new MemoryStream();
        await NativeMessagingProtocol.WriteResponseAsync(response, true, default);
        var buffer = response.ToArray();
        Assert.Equal(buffer.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4)));
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(buffer.AsSpan(4)));
    }

    [Theory]
    [InlineData("{\"type\":\"openSelection\",\"text\":\"ok\",\"url\":\"file:///secret\",\"title\":\"\"}")]
    [InlineData("{\"type\":\"openSelection\",\"text\":\"ok\",\"url\":\"https://example.org\",\"title\":\"x\",\"other\":\"a\"}")]
    [InlineData("{\"type\":\"openSelection\",\"text\":\"ok\",\"text\":\"bad\",\"url\":\"https://example.org\",\"title\":\"\"}")]
    [InlineData("{\"type\":\"openSelection\",\"text\":\"\",\"url\":\"https://example.org\",\"title\":\"\"}")]
    public void InvalidNativeRequestsAreRejected(string json) =>
        Assert.Null(NativeMessagingProtocol.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task InvalidFrameLengthIsRejectedBeforeAllocation()
    {
        using var input = new MemoryStream([255, 255, 255, 127]);
        Assert.Null(await NativeMessagingProtocol.ReadAsync(input, default));
    }

    [Fact]
    public void NativeTableRequestsAreValidatedTwice()
    {
        var json = "{\"type\":\"openTable\",\"url\":\"https://example.org/\",\"title\":\"Docs\",\"rows\":[[\"Name\",\"Value\"],[\"A\",\"1\"]]}";
        var parsed = Assert.IsType<PowerTrove.Core.Activation.BrowserTableMessage>(
            NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("A", parsed.Rows[1][0]);
        Assert.Null(NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(
            json.Replace("https://example.org/","file:///secret"))));
        Assert.Null(NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(
            json.Replace("\"rows\":","\"filePaths\":[],\"rows\":"))));
    }

    [Fact]
    public void ExplicitLastDownloadUsesNormalPathValidation()
    {
        string json = "{\"type\":\"openDownload\",\"path\":\"C:\\\\Downloads\\\\example.pdf\"}";
        var accepted = Assert.IsType<PowerTrove.Core.Activation.ExplicitSelectionMessage>(
            NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(json)));
        Assert.Single(accepted.FilePaths);
        Assert.Equal(@"C:\Downloads\example.pdf", accepted.FilePaths[0]);
        Assert.Null(NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(
            json.Replace("example.pdf","..\\\\secret.pdf"))));
        Assert.Null(NativeMessagingProtocol.DecodeActivation(Encoding.UTF8.GetBytes(
            json.Replace("C:\\\\Downloads\\\\example.pdf","\\\\\\\\server\\\\share\\\\secret.pdf"))));
    }

    [Fact]
    public void ExtensionOriginMustHaveExactId()
    {
        Assert.True(Program.IsExtensionOrigin("chrome-extension://" + new string('a', 32) + "/"));
        Assert.True(Program.IsExtensionOrigin("chrome-extension://" + new string('a', 32)));
        Assert.False(Program.IsExtensionOrigin("https://" + new string('a', 32) + "/"));
        Assert.False(Program.IsExtensionOrigin("chrome-extension://../../"));
        Assert.False(Program.IsExtensionOrigin("chrome-extension://" + new string('z', 32) + "/"));
    }
}
