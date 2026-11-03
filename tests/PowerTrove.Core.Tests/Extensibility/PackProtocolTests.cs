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
        yield return new object[] { new PreviewMessage(Id, "powertrove.text.trim", new FileSelectionPayload([]), new Dictionary<string, string>(), Expiry, TextInput: " selected text ") };
        yield return new object[] { new ExecuteMessage(Id, "powertrove.text.trim", new FileSelectionPayload([]), new Dictionary<string, string>(), Expiry, @"C:\staging\request", TextInput: " selected text ") };
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
    public void BrowserWorkerRequestsAndRepliesAreStrictlyTyped()
    {
        var id=Guid.NewGuid();
        var operation=Guid.NewGuid();
        var request=new BrowserServiceRequestMessage(id,operation,
            BrowserPageOperation.ArticleMarkdown);
        var decoded=Assert.IsType<BrowserServiceRequestMessage>(
            PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(request)));
        Assert.Equal(request.Operation,decoded.Operation);

        var reply=new BrowserServiceResultMessage(id,operation,
            new BrowserPageContent(BrowserPageOperation.ArticleMarkdown,Markdown:"# Page"));
        var roundTrip=Assert.IsType<BrowserServiceResultMessage>(
            PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(reply)));
        Assert.Equal("# Page",roundTrip.Content?.Markdown);
        Assert.DoesNotContain("Page",reply.ToString());
        Assert.Throws<PackProtocolException>(()=>PackMessageSerializer.SerializeLine(
            new BrowserServiceRequestMessage(Guid.Empty,operation,BrowserPageOperation.ArticleMarkdown)));
    }

    [Fact]
    public void BrowserMetadataRoundTripsAndDoesNotLeakThroughDiagnostics()
    {
        var browser = new BrowserTextContextPayload("https://example.org/docs", "PRIVATE-TITLE");
        var request = new PreviewMessage(Id, "powertrove.browser.copy-page-link",
            new FileSelectionPayload([]), new Dictionary<string, string>(), Expiry,
            TextInput: "PRIVATE-SELECTION", BrowserContext: browser);
        var decoded = Assert.IsType<PreviewMessage>(PackMessageSerializer.DeserializeLine(
            PackMessageSerializer.SerializeLine(request)));
        Assert.Equal(browser, decoded.BrowserContext);
        Assert.DoesNotContain("PRIVATE-TITLE", browser.ToString());
        Assert.DoesNotContain("PRIVATE-SELECTION", request.ToString());
        Assert.Equal(PackMessageSerializer.SerializeLine(request), PackMessageSerializer.SerializeLine(decoded));
    }

    [Fact]
    public void BrowserMetadataCannotElevateFilesOrOtherTextSources()
    {
        var metadata = new BrowserTextContextPayload("https://example.org/docs", "Docs");
        var request = new PreviewMessage(Id, "powertrove.browser.copy-selection",
            new FileSelectionPayload([]), new Dictionary<string, string>(), Expiry,
            TextInput: "sample", BrowserContext: metadata);
        var invalid = new[]
        {
            request with { TextInput = null },
            request with { Selection = Selection },
            request with { BrowserContext = metadata with { Url = "file:///private" } },
            request with { BrowserContext = metadata with { Url = "https://user:pass@example.org" } },
            request with { BrowserContext = metadata with { Title = new string('x', 301) } },
            request with { BrowserContext = metadata with { Url = new string('x', 2049) } }
        };
        foreach (var message in invalid)
            Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message));
    }

    [Fact]
    public void PreviewConversion_PreservesEveryPreviewField()
    {
        var preview = new ActionPreview(true, "Summary", new[] { "Output" }, new[] { "Warning" }, true,
            new[] { new RenamePreviewRow("old", "new") }, new ActionConfirmation("Prompt", "confirm", "token"),
            new[] { new ActionParameterChoiceSet("Columns", "flat", new[] { new ActionParameterChoice("customer.name", "Name") }) });
        Assert.Equal(preview, PreviewResultMessage.FromPreview(Id, preview).ToPreview());
        var decoded = Assert.IsType<PreviewResultMessage>(PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(PreviewResultMessage.FromPreview(Id, preview))));
        Assert.Equal("customer.name", Assert.Single(Assert.Single(decoded.ToPreview().ParameterChoices!).Choices).Value);
        Assert.Null(ReadPreviewWithoutChoices().ParameterChoices);
    }

    private static ActionPreview ReadPreviewWithoutChoices() => Assert.IsType<PreviewResultMessage>(Read(
        "{\"type\":\"previewResult\",\"requestId\":\"6ce62269-3512-4bc4-9344-b56558e20f21\",\"requiresConfirmation\":false,\"expectedOutputs\":[],\"warnings\":[],\"isBlocked\":false}")).ToPreview();

    [Fact]
    public void DynamicChoicesRejectDuplicateOrOversizedPayloadsWithoutLoggingNames()
    {
        var choice = new ActionParameterChoice("synthetic-field", "Synthetic field");
        var set = new ActionParameterChoiceSet("Columns", "flat", new[] { choice });
        Assert.DoesNotContain("synthetic", set.ToString());
        var message = PreviewResultMessage.FromPreview(Id, new ActionPreview(false, "Summary", [], [], ParameterChoices: new[] { set }));
        foreach (var invalid in new[] {
            set with { Choices = new[] { choice, choice } },
            set with { Choices = new[] { choice with { Value = new string('x', ActionParameterChoiceSet.MaximumChoiceLength + 1) } } },
            set with { Choices = Enumerable.Range(0, ActionParameterChoiceSet.MaximumChoices + 1).Select(i => choice with { Value = i.ToString() }).ToArray() }
        }) Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message with { ParameterChoices = new[] { invalid } }));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(message with { ParameterChoices = new[] { set, set } }));
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
    public void ExplicitTextPayload_ValidatesLengthAndRejectsMixedFileContent()
    {
        var emptyFiles = new FileSelectionPayload([]);
        var valid = new PreviewMessage(Id, "powertrove.text.trim", emptyFiles, new Dictionary<string, string>(), Expiry, TextInput: new string('x', 5000));
        var decoded = Assert.IsType<PreviewMessage>(PackMessageSerializer.DeserializeLine(PackMessageSerializer.SerializeLine(valid)));
        Assert.Equal(5000, decoded.TextInput?.Length);
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(valid with { Selection = Selection })).Code);
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(valid with { TextInput = "" })).Code);
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(valid with { TextInput = new string('x', PackProtocolLimits.MaximumTextInputLength + 1) })).Code);
        Assert.Equal(PackProtocolError.InvalidMessage, Assert.Throws<PackProtocolException>(() =>
            PackMessageSerializer.SerializeLine(valid with { TextInput = "a\0b" })).Code);
        var receipt = new ResultMessage(Id, new ActionReceipt(true, false, [], [], false,
            ClipboardText: new string('x', 5000)), []);
        Assert.Equal(5000, Assert.IsType<ResultMessage>(PackMessageSerializer.DeserializeLine(
            PackMessageSerializer.SerializeLine(receipt))).Receipt.ClipboardText?.Length);
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

    [Fact]
    public void TablePayload_RoundTripsEveryCellKind_WithoutContentInDiagnostics()
    {
        var date = new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified);
        var cells = new CellValue[] { new EmptyCell(), new TextCell("private-cell-marker", "fmt"), new TextCell(""), new NumberCell(1.5, "1,5"),
            new BooleanCell(true), new DateCell(date, "28.10.2026"), new ErrorCell("#N/A") };
        var table = TableSelectionPayload.FromSnapshot(Snapshot(new TableData(new[] { cells }, "Excel", "A1:G1")), @"C:\exports");
        var message = new ExecuteMessage(Id, "table", new FileSelectionPayload([]), Parameters, Expiry, @"C:\staging\x", table);
        var read = Assert.IsType<ExecuteMessage>(Read(Encoding.UTF8.GetString(PackMessageSerializer.SerializeLine(message))));
        var data = read.Table!.ToTableData();
        Assert.Equal(cells, data.Rows.Single());
        Assert.Equal(("Excel", "A1:G1"), (data.SourceApplication, data.SourceRange));
        Assert.Equal(@"C:\exports", read.Table.OutputDirectory);
        Assert.DoesNotContain("private", read.ToString() + read.Table + read.Table.Rows[0][1]);
    }

    [Fact]
    public void TableRequests_RejectFileAccess_InconsistentCells_AndOversizedRanges()
    {
        var table = new TableSelectionPayload([[new ProtocolCell("text", Text: "a")]], @"C:\exports");
        var file = new ProtocolFile(@"C:\inputs\a.csv", "a.csv", ".csv", false);
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "t", new FileSelectionPayload([file]), Parameters, Expiry, table)));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "t", new FileSelectionPayload([]), Parameters, Expiry)));
        foreach (var cell in new[] { new ProtocolCell("number", Number: double.NaN), new ProtocolCell("text", Text: "a", Number: 1), new ProtocolCell("text"),
            new ProtocolCell("formula", Text: "=1"), new ProtocolCell("empty", Text: "x") })
            Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "t", new FileSelectionPayload([]), Parameters, Expiry,
                table with { Rows = [[cell]] })));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "t", new FileSelectionPayload([]), Parameters, Expiry,
            table with { OutputDirectory = "relative" })));
        var huge = Enumerable.Range(0, 300_000).Select(_ => (IReadOnlyList<CellValue>)new CellValue[] { new TextCell("0123456789") }).ToArray();
        var ex = Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(new PreviewMessage(Id, "t", new FileSelectionPayload([]), Parameters, Expiry,
            TableSelectionPayload.FromSnapshot(Snapshot(new TableData(huge, null, null)), @"C:\exports"))));
        Assert.Equal(PackProtocolError.LineTooLarge, ex.Code);
        Assert.Throws<PackProtocolException>(() => TableSelectionPayload.FromSnapshot(Snapshot(new FileSelection([])), @"C:\exports"));
    }

    private static PackMessage? Read(string json) => PackMessageSerializer.DeserializeLine(Encoding.UTF8.GetBytes(json));
    private static ContextSnapshot Snapshot(ContextContent content) => new(Id, new SourceWindowIdentity(0, 0, "test", Expiry, "private-window-marker"),
        ContextStatus.Available, content, Expiry.AddMinutes(-5), Expiry, "private-provider-marker", null);
    private static ResultMessage Result(string path) => new(Id, new ActionReceipt(true, false, new[] { path }, Array.Empty<ActionFailure>(), false, "Completed"),
        new[] { new StagedOutput(path, @"C:\outputs", "result.pdf") });
    [Fact]
    public void BrowserBinaryChunksAreTypedBoundedAndCanonical()
    {
        var operation = Guid.NewGuid();
        var bytes = "%PDF-1.7\\n%%EOF"u8.ToArray();
        var chunk = new BrowserServiceBinaryChunkMessage(Id, operation, 0, true,
            "pdf", Convert.ToBase64String(bytes));
        var encoded = PackMessageSerializer.SerializeLine(chunk);
        var decoded = Assert.IsType<BrowserServiceBinaryChunkMessage>(
            PackMessageSerializer.DeserializeLine(encoded));
        Assert.Equal(operation, decoded.OperationId);
        Assert.Equal(chunk.Base64, decoded.Base64);
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(
            chunk with { Kind = "executable" }));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(
            chunk with { Sequence = 2048 }));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(
            chunk with { Base64 = "!!!" }));
        Assert.Throws<PackProtocolException>(() => PackMessageSerializer.SerializeLine(
            chunk with { Base64 = Convert.ToBase64String(new byte[128 * 1024 + 1]) }));
    }

}
