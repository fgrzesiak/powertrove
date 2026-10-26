using System.Text;
using PowerTrove.Core.Extensibility.PackProtocol;

namespace PowerTrove.Core.Tests.Extensibility;

public sealed class PackProtocolTests
{
    private static readonly Guid Id = Guid.Parse("6ce62269-3512-4bc4-9344-b56558e20f21");
    private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FileSelectionPayload Selection = new(new[] { new ProtocolFile(@"C:\inputs\synthetic.pdf", "synthetic.pdf", ".pdf", false, 42) });
    private static readonly IReadOnlyDictionary<string, string> Parameters = new Dictionary<string, string> { ["quality"] = "80", ["label"] = "Grüße\nsecond line" };

    public static IEnumerable<object[]> Messages()
    {
        yield return new object[] { new HelloMessage(1, "0.1.0") };
        yield return new object[] { new ReadyMessage(new[] { new WorkerPack("powertrove.pack.pdf", "1.0.0") }) };
        yield return new object[] { new PreviewMessage(Id, "powertrove.pdf.rotate", Selection, Parameters, Expiry) };
        yield return new object[] { new ExecuteMessage(Id, "powertrove.pdf.rotate", Selection, Parameters, Expiry, @"C:\staging\request") };
        yield return new object[] { PreviewResultMessage.FromPreview(Id, new ActionPreview(true, "Preview", new[] { "synthetic (copy).pdf" },
            new[] { "Warning" }, false, new[] { new RenamePreviewRow("before.pdf", "after.pdf") }, new ActionConfirmation("Confirm", "confirm", "token"))) };
        yield return new object[] { new ProgressMessage(Id, 0.25, "Processing") };
        yield return new object[] { new ResultMessage(Id,
            new ActionReceipt(true, false, new[] { "bucket/result.pdf" }, Array.Empty<ActionFailure>(), true, "Completed", "Derived text", Id, Expiry),
            new[] { new StagedOutput("bucket/result.pdf", @"C:\outputs", "result.pdf") }) };
        yield return new object[] { new CancelMessage(Id) };
        yield return new object[] { new ErrorMessage("unknownAction", "Action is not available.", Id) };
        yield return new object[] { new HeartbeatMessage(Id) };
        yield return new object[] { new ShutdownMessage() };
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void EveryMessage_RoundTripsThroughGeneratedMetadata_WithCamelCaseAndOneLf(PackMessage message)
    {
        byte[] line = PackMessageSerializer.SerializeLine(message);
        Assert.Equal((byte)'\n', line[^1]);
        Assert.Equal(1, line.Count(b => b == (byte)'\n'));
        Assert.Contains("\"type\":\"" + message.Type + "\"", Encoding.UTF8.GetString(line));
        var decoded = PackMessageSerializer.DeserializeLine(line);
        Assert.NotNull(decoded);
        Assert.Equal(message.GetType(), decoded.GetType());
        Assert.Equal(line, PackMessageSerializer.SerializeLine(decoded));
        Assert.DoesNotContain("synthetic", message.ToString());
        Assert.DoesNotContain("token", message.ToString());
        Assert.DoesNotContain("Derived text", message.ToString());
    }

    [Fact]
    public void PreviewConversion_PreservesEveryPreviewField()
    {
        var preview = new ActionPreview(true, "Summary", new[] { "Output" }, new[] { "Warning" }, true,
            new[] { new RenamePreviewRow("old", "new") }, new ActionConfirmation("Prompt", "confirm", "token"));
        Assert.Equal(preview, PreviewResultMessage.FromPreview(Id, preview).ToPreview());
    }

    [Fact]
    public void SelectionConversion_OnlyTransfersFileFields_AndRejectsTableTextAndUnavailableSnapshots()
    {
        var snapshot = Snapshot(new FileSelection(new[] { new SelectedFile(@"C:\inputs\synthetic.pdf", "synthetic.pdf", ".pdf", "private-media-marker", 42,
            true, true, new Dictionary<string, string> { ["secret"] = "private-metadata-marker" }) }));
        var selection = FileSelectionPayload.FromSnapshot(snapshot);
        string json = Encoding.UTF8.GetString(PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "pdf", selection, Parameters, Expiry)));
        Assert.Contains("synthetic.pdf", json);
        Assert.DoesNotContain("private-media-marker", json); Assert.DoesNotContain("private-metadata-marker", json);
        Assert.DoesNotContain("private-window-marker", json); Assert.DoesNotContain("private-provider-marker", json);
        foreach (var content in new ContextContent[] { new TextSelection("private-text-marker"), new TableData(new[] { new CellValue[] { new TextCell("private-cell-marker") } }, null, null), new NoContent() })
        {
            var ex = Assert.Throws<PackProtocolException>(() => FileSelectionPayload.FromSnapshot(Snapshot(content)));
            Assert.Equal(PackProtocolError.UnsupportedSelection, ex.Code);
            Assert.DoesNotContain("private", ex.ToString()); Assert.Null(ex.InnerException);
        }
        Assert.Throws<PackProtocolException>(() => FileSelectionPayload.FromSnapshot(snapshot with { Status = ContextStatus.Denied }));
        Assert.Throws<PackProtocolException>(() => FileSelectionPayload.FromSnapshot(Snapshot(new FileSelection(new[] { new SelectedFile(null, "synthetic", "", null, null, false, false, null) }))));
    }

    [Fact]
    public void FileCountLimit_Accepts10000_AndRejects10001BeforeEncoding()
    {
        var files = Enumerable.Repeat(Selection.Files[0], PackProtocolLimits.MaximumFiles).ToArray();
        var message = new PreviewMessage(Id, "pdf", new FileSelectionPayload(files), new Dictionary<string, string>(), Expiry);
        Assert.IsType<PreviewMessage>(PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(message)));
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(message with { Selection = new FileSelectionPayload(files.Append(files[0]).ToArray()) })).Code);
    }

    [Fact]
    public void UnknownMessages_AreIgnored_ButTheirJsonMustStillBeWellFormed()
    {
        Assert.Null(Read("{\"type\":\"futureMessage\",\"payload\":{\"newField\":42}}"));
        Assert.Throws<PackProtocolException>(() => Read("{\"type\":\"futureMessage\",\"payload\":"));
        Assert.Throws<PackProtocolException>(() => Read("{\"type\":\"futureMessage\",\"payload\":{\"x\":1,\"x\":2}}"));
        Assert.IsType<ShutdownMessage>(Read("{\"type\":\"shutdown\",\"futureField\":true}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"hello\",\"hostVersion\":\"0.1.0\"}")]
    [InlineData("{\"type\":\"hello\",\"protocolVersion\":2,\"hostVersion\":\"0.1.0\"}")]
    [InlineData("{\"type\":\"hello\",\"protocolVersion\":1,\"hostVersion\":\"invalid\"}")]
    [InlineData("{\"type\":\"hello\",\"protocolVersion\":1,\"protocolVersion\":1,\"hostVersion\":\"0.1.0\"}")]
    [InlineData("{\"type\":\"cancel\",\"requestId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"type\":\"progress\",\"requestId\":\"6ce62269-3512-4bc4-9344-b56558e20f21\",\"fraction\":1.01}")]
    [InlineData("{\"type\":\"progress\",\"requestId\":\"6ce62269-3512-4bc4-9344-b56558e20f21\"}")]
    [InlineData("{\"type\":\"error\",\"code\":\"futureCode\",\"message\":\"safe\"}")]
    [InlineData("{\"type\":\"ready\",\"packs\":[null]}")]
    [InlineData("{\"type\":\"shutdown\"}\n{\"type\":\"shutdown\"}")]
    public void MalformedOrInvalidFields_ReturnTypedContentFreeErrors(string json)
    {
        var ex = Assert.Throws<PackProtocolException>(() => Read(json));
        Assert.Equal("Pack protocol message rejected.", ex.Message); Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Utf8DepthAndByteLimits_AreEnforced_EvenForUnknownTypes()
    {
        Assert.Equal(PackProtocolError.MalformedJson, Assert.Throws<PackProtocolException>(() => PackMessageSerializer.DeserializeLine(new byte[] { 0xFF })).Code);
        string nested = "{\"type\":\"future\",\"x\":" + new string('[', 40) + "0" + new string(']', 40) + "}";
        Assert.Throws<PackProtocolException>(() => Read(nested));
        const string unknown = "{\"type\":\"future\"}";
        var exact = Encoding.UTF8.GetBytes(unknown + new string(' ', PackProtocolLimits.MaximumLineBytes - unknown.Length));
        Assert.Null(PackMessageSerializer.DeserializeLine(exact));
        Assert.Equal(PackProtocolError.LineTooLarge, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.DeserializeLine(exact.Append((byte)' ').ToArray())).Code);
        // String lengths pass individually; the encoded message still exceeds 4 MiB.
        var parameters = Enumerable.Range(0, 900).ToDictionary(i => "p" + i, _ => new string('é', 3000));
        Assert.Equal(PackProtocolError.LineTooLarge, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "pdf", Selection, parameters, Expiry))).Code);
        // Raw UTF-8 fits, but JSON escaping alone pushes the encoded line over the limit.
        parameters = Enumerable.Range(0, 400).ToDictionary(i => "p" + i, _ => new string('\u0001', 3000));
        Assert.Equal(PackProtocolError.LineTooLarge, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "pdf", Selection, parameters, Expiry))).Code);
    }

    [Theory]
    [InlineData("unknownAction")]
    [InlineData("invalidRequest")]
    [InlineData("internal")]
    public void EveryErrorCode_AllowsAnUncorrelatedSessionError(string code)
    {
        var error = Assert.IsType<ErrorMessage>(PackMessageSerializer.DeserializeLine(
            PackMessageSerializer.SerializeLine(new ErrorMessage(code, "Safe message"))));
        Assert.Equal(code, error.Code); Assert.Null(error.RequestId);
    }

    [Fact]
    public void ProducerValidation_RejectsInvalidValuesAndRequiredNullsWithTypedErrors()
    {
        var request = new PreviewMessage(Id, "pdf", Selection, Parameters, Expiry);
        var invalid = new PackMessage[]
        {
            request with { RequestId = Guid.Empty }, request with { ActionId = " " },
            request with { Selection = null! }, request with { Parameters = null! }, request with { ExpiresAt = default },
            request with { Selection = new FileSelectionPayload(Array.Empty<ProtocolFile>()) },
            request with { Selection = new FileSelectionPayload(new[] { Selection.Files[0] with { SizeBytes = -1 } }) },
            request with { Selection = new FileSelectionPayload(new[] { Selection.Files[0] with { Path = "relative.pdf" } }) },
            new ExecuteMessage(Id, "pdf", Selection, Parameters, Expiry, "relative"),
            new ReadyMessage(new[] { new WorkerPack("pdf", "1.0"), new WorkerPack("PDF", "1.0") }),
            new ProgressMessage(Id, double.NaN), new ProgressMessage(Id, -0.1),
            new ErrorMessage("internal", " "), new HelloMessage(2, "0.1.0")
        };
        foreach (var message in invalid)
            Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message)).Code);
    }

    [Theory]
    [InlineData("../result.pdf")]
    [InlineData("/result.pdf")]
    [InlineData("C:/result.pdf")]
    [InlineData("bucket\\result.pdf")]
    [InlineData("bucket/./result.pdf")]
    [InlineData("bucket//result.pdf")]
    [InlineData("bucket/CON.pdf")]
    [InlineData("bucket/result.pdf:stream")]
    public void ResultPaths_CannotEscapeStagingOrUseAmbiguousNames(string path)
    {
        var result = Result(path);
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(result)).Code);
        string json = Encoding.UTF8.GetString(PackMessageSerializer.SerializeLine(Result("bucket/result.pdf")));
        // Exercise the untrusted decoding path as well as the producer validation.
        Assert.Throws<PackProtocolException>(() => Read(json.Replace("bucket/result.pdf", path.Replace("\\", "\\\\"))));
    }

    [Fact]
    public void ResultMappings_MustMatchReceipt_AndHaveUniqueTargets()
    {
        var result = Result("bucket/result.pdf");
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(result with
        {
            Receipt = result.Receipt with { OutputPaths = new[] { "different.pdf" } }
        }));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(result with
        {
            Receipt = result.Receipt with { OutputPaths = new[] { "a.pdf", "b.pdf" } },
            Outputs = new[] { new StagedOutput("a.pdf", @"C:\outputs", "same.pdf"), new StagedOutput("b.pdf", @"C:\outputs", "SAME.pdf") }
        }));
        string json = Encoding.UTF8.GetString(PackMessageSerializer.SerializeLine(result));
        Assert.Throws<PackProtocolException>(() => Read(json.Replace("\"isUndoable\":false,", "")));
    }

    [Fact]
    public void MutationSamples_AreAlwaysTypedOrAcceptedAsValidMessages()
    {
        var valid = PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "pdf", Selection, Parameters, Expiry));
        for (int i = 1; i < valid.Length - 1; i += 11)
        {
            Assert.Throws<PackProtocolException>(() => PackMessageSerializer.DeserializeLine(valid.AsSpan(0, i)));
            var mutated = valid.ToArray(); mutated[i] = 0xFF;
            var ex = Assert.Throws<PackProtocolException>(() => PackMessageSerializer.DeserializeLine(mutated));
            Assert.Equal(PackProtocolError.MalformedJson, ex.Code);
        }
        var random = new Random(159);
        for (int i = 0; i < 100; i++)
        {
            var bytes = new byte[random.Next(1, 256)]; random.NextBytes(bytes);
            try { _ = PackMessageSerializer.DeserializeLine(bytes); }
            catch (PackProtocolException ex) { Assert.Null(ex.InnerException); }
        }
    }

    private static PackMessage? Read(string json) => PackMessageSerializer.DeserializeLine(Encoding.UTF8.GetBytes(json));
    private static ContextSnapshot Snapshot(ContextContent content) => new(Id, new SourceWindowIdentity(0, 0, "test", Expiry, "private-window-marker"),
        ContextStatus.Available, content, Expiry.AddMinutes(-5), Expiry, "private-provider-marker", null);
    private static ResultMessage Result(string path) => new(Id, new ActionReceipt(true, false, new[] { path }, Array.Empty<ActionFailure>(), false, "Completed"),
        new[] { new StagedOutput(path, @"C:\outputs", "result.pdf") });
}
