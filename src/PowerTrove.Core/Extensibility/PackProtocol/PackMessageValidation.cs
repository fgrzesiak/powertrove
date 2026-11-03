using System.Text;
using PowerTrove.Abstractions;
using System.Diagnostics.CodeAnalysis;

namespace PowerTrove.Core.Extensibility.PackProtocol;

internal sealed class PackMessageValidation
{
    private long _textBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static void Validate(PackMessage message) => new PackMessageValidation().Message(message);
    public static void ValidateTable(TableSelectionPayload table) => new PackMessageValidation().Table(table);
    public static void ValidateSelection(FileSelectionPayload selection) => new PackMessageValidation().Selection(selection);

    private void Message(PackMessage message)
    {
        switch (message)
        {
            case HelloMessage m:
                Require(m.ProtocolVersion == PackProtocolLimits.Version); VersionText(m.HostVersion); break;
            case ReadyMessage m:
                Count(m.Packs, PackProtocolLimits.MaximumPacks);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pack in m.Packs)
                {
                    Require(pack is not null); Text(pack.Id, 256); VersionText(pack.Version); Require(ids.Add(pack.Id));
                }
                break;
            case PreviewMessage m:
                Request(m.RequestId, m.ActionId, m.Selection, m.Table, m.TextInput, m.BrowserContext,
                    m.Parameters, m.ExpiresAt, preview: true); break;
            case ExecuteMessage m:
                Request(m.RequestId, m.ActionId, m.Selection, m.Table, m.TextInput, m.BrowserContext,
                    m.Parameters, m.ExpiresAt, preview: false); AbsolutePath(m.StagingDirectory); break;
            case PreviewResultMessage m:
                Id(m.RequestId); Text(m.PreviewText, optional: true);
                Texts(m.ExpectedOutputs); Texts(m.Warnings);
                if (m.ImagePreview is { } image)
                {
                    Text(image.PngBase64, ImagePreviewData.MaximumEncodedLength);
                    Require(image.TryDecodePng(out _));
                }
                if (m.DocumentPreview is { } document)
                {
                    Require(document.IsValid);
                    Text(document.Html, DocumentPreviewData.MaximumHtmlLength); Text(document.Title, DocumentPreviewData.MaximumTitleLength, optional: true);
                }
                if (m.RenameRows is not null)
                {
                    Count(m.RenameRows, PackProtocolLimits.MaximumFiles);
                    foreach (var row in m.RenameRows)
                    {
                        Require(row is not null); Leaf(row.OldName); Leaf(row.NewName);
                    }
                }
                if (m.Confirmation is { } confirmation)
                {
                    Text(confirmation.Prompt); Text(confirmation.ParameterName, 256); Text(confirmation.Token);
                }
                if (m.ParameterChoices is { } sets)
                {
                    Count(sets, 32);
                    var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var set in sets)
                    {
                        Require(set is not null); Text(set.ParameterName, 256); Require(parameters.Add(set.ParameterName));
                        Text(set.ContextKey, 128); Text(set.Description, optional: true);
                        Count(set.Choices, ActionParameterChoiceSet.MaximumChoices);
                        var choices = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var choice in set.Choices)
                        {
                            Require(choice is not null); Text(choice.Value, ActionParameterChoiceSet.MaximumChoiceLength);
                            Text(choice.Title, ActionParameterChoiceSet.MaximumChoiceLength); Require(choices.Add(choice.Value));
                        }
                    }
                }
                break;
            case ProgressMessage m:
                Id(m.RequestId); Require(double.IsFinite(m.Fraction) && m.Fraction >= 0 && m.Fraction <= 1);
                Text(m.Message, optional: true); break;
            case ResultMessage m:
                Result(m); break;
            case CancelMessage m:
                Id(m.RequestId); break;
            case ErrorMessage m:
                if (m.RequestId.HasValue) Id(m.RequestId.Value);
                Require(m.Code is "unknownAction" or "invalidRequest" or "internal"); Text(m.Message); break;
            case HeartbeatMessage m:
                Id(m.RequestId); break;
            case BrowserServiceRequestMessage m:
                Id(m.RequestId); Id(m.OperationId);
                Require(Enum.IsDefined(m.Operation));
                break;
            case BrowserServiceResultMessage m:
                Id(m.RequestId); Id(m.OperationId);
                if (m.Content is not null) BrowserResponse(m.Content);
                break;
            case BrowserServiceBinaryChunkMessage m:
                Id(m.RequestId); Id(m.OperationId);
                Require(m.Sequence is >= 0 and <= 1024);
                Require(m.Kind is "image" or "mhtml" or "pdf");
                Text(m.Base64, 180000);
                try
                {
                    byte[] bytes = Convert.FromBase64String(m.Base64);
                    Require(bytes.Length is > 0 and <= 128 * 1024 &&
                        Convert.ToBase64String(bytes) == m.Base64);
                }
                catch (FormatException) { throw Invalid(); }
                break;
            case ShutdownMessage:
                break;
            default:
                throw Invalid();
        }
    }

    private void Request(Guid id, string actionId, FileSelectionPayload selection, TableSelectionPayload? table, string? textInput,
        BrowserTextContextPayload? browserContext,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset expiresAt, bool preview)
    {
        Id(id); Text(actionId, 256); Require(expiresAt != default);
        // Exactly one input kind: a table or text request never also authorizes file access.
        Require(table is null || textInput is null);
        Require(browserContext is null || (textInput is not null && table is null));
        if (browserContext is not null)
        {
            if (browserContext.Tab is { } tab)
            {
                Id(tab.SessionId);
                Require(tab.WindowId > 0 && tab.TabId >= 0);
                Text(tab.DocumentId, 128);
            }
            Text(browserContext.Url, 2048);
            Text(browserContext.Title, 300, allowEmpty: true);
            Require(Uri.TryCreate(browserContext.Url, UriKind.Absolute, out var parsed) &&
                parsed.Scheme is ("https" or "http") && parsed.UserInfo.Length == 0);
        }
        if (table is null && textInput is null) Selection(selection);
        else if (table is not null)
        {
            Require(selection?.Files is { Count: 0 }); Table(table);
        }
        else
        {
            Require(selection?.Files is { Count: 0 } && textInput!.Length > 0); TextPayload(textInput);
        }
        Count(parameters, PackProtocolLimits.MaximumParameters);
        foreach (var parameter in parameters)
        {
            // Only a preview may carry an editor's unsaved text; it never authorizes writing it anywhere.
            bool draft = parameter.Key == DocumentPreviewRequest.DraftParameterName;
            Require(!draft || preview);
            Text(parameter.Key, 256); Text(parameter.Value, draft ? DocumentPreviewRequest.MaximumDraftLength : PackProtocolLimits.MaximumTextLength, allowEmpty: true);
        }
    }

    private void Selection(FileSelectionPayload selection, bool allowEmpty = false)
    {
        Require(selection is not null);
        Count(selection.Files, PackProtocolLimits.MaximumFiles);
        Require(allowEmpty || selection.Files.Count > 0);
        foreach (var file in selection.Files)
        {
            Require(file is not null); AbsolutePath(file.Path); Leaf(file.Name);
            Text(file.Extension, 32, allowEmpty: true);
            Require(file.Extension.Length == 0 || file.Extension.StartsWith('.') &&
                !file.Extension.Any(c => c < ' ' || ":/\\<>\"|?*".Contains(c)));
            Require(!file.SizeBytes.HasValue || file.SizeBytes.Value >= 0);
        }
    }

    private void Table(TableSelectionPayload table)
    {
        Require(table is not null);
        Count(table.Rows, PackProtocolLimits.MaximumTableCells);
        AbsolutePath(table.OutputDirectory);
        Text(table.SourceApplication, 256, optional: true); Text(table.SourceRange, 256, optional: true);
        long cells = 0;
        foreach (var row in table.Rows)
        {
            Require(row is not null);
            cells += row.Count;
            Require(cells <= PackProtocolLimits.MaximumTableCells);
            foreach (var cell in row)
            {
                Require(cell is not null);
                Text(cell.Formatted, PackProtocolLimits.MaximumCellTextLength, optional: true, allowEmpty: true);
                Require(cell.Kind switch
                {
                    "empty" => cell.Text is null && cell.Number is null && cell.Boolean is null && cell.Date is null,
                    "text" or "error" => cell.Text is not null && cell.Number is null && cell.Boolean is null && cell.Date is null,
                    "number" => cell.Number is { } n && double.IsFinite(n) && cell.Text is null && cell.Boolean is null && cell.Date is null,
                    "boolean" => cell.Boolean is not null && cell.Text is null && cell.Number is null && cell.Date is null,
                    "date" => cell.Date is not null && cell.Text is null && cell.Number is null && cell.Boolean is null,
                    _ => false
                });
                Text(cell.Text, cell.Kind == "error" ? 64 : PackProtocolLimits.MaximumCellTextLength, optional: true, allowEmpty: cell.Kind == "text");
            }
        }
    }

    private void Result(ResultMessage message)
    {
        Id(message.RequestId);
        var receipt = message.Receipt;
        Require(receipt is not null);
        Require(!(receipt.IsSuccess && receipt.IsCanceled));
        Text(receipt.Message, optional: true);
        if (receipt.ClipboardText is not null) TextPayload(receipt.ClipboardText, allowEmpty: true);
        if (receipt.UndoOperationId.HasValue) Id(receipt.UndoOperationId.Value);
        Require(!receipt.UndoUntil.HasValue || receipt.UndoUntil.Value != default);
        Count(receipt.Failures, PackProtocolLimits.MaximumFiles);
        foreach (var failure in receipt.Failures)
        {
            Require(failure is not null); Text(failure.Item, optional: true); Text(failure.ErrorMessage);
        }
        Count(receipt.OutputPaths, PackProtocolLimits.MaximumFiles);
        Count(message.Outputs, PackProtocolLimits.MaximumFiles);
        Require(receipt.OutputPaths.Count == message.Outputs.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < message.Outputs.Count; i++)
        {
            var output = message.Outputs[i];
            Require(output is not null); RelativePath(output.Path);
            Require(paths.Add(output.Path) && receipt.OutputPaths[i] == output.Path);
            AbsolutePath(output.IntendedDirectory); Leaf(output.IntendedName);
            Require(destinations.Add(output.IntendedDirectory.Replace('\\', '/').TrimEnd('/') + "/" + output.IntendedName));
        }
    }

    private void BrowserResponse(BrowserPageContent value)
    {
        Require(Enum.IsDefined(value.Operation));
        if (value.Markdown is { } markdown) Text(markdown, 250000);
        if (value.Tables is { } tables)
        {
            Count(tables, 30);
            long cells = 0;
            foreach (var table in tables)
            {
                Require(table is not null);
                Count(table.Rows, 5000);
                foreach (var row in table.Rows)
                {
                    Count(row, 200);
                    cells += row.Count;
                    Require(cells <= 100000);
                    foreach (var cell in row) Text(cell, 4096, allowEmpty: true);
                }
            }
        }
        if (value.Links is { } links)
        {
            Count(links, 200);
            foreach (var link in links)
            {
                Text(link.Title, 150, allowEmpty: true);
                Text(link.Url, 2048);
            }
        }
        if (value.Images is { } images)
        {
            Count(images, 100);
            foreach (var image in images)
            {
                Text(image.Url, 2048); Text(image.Alt, 120, allowEmpty: true);
                Require(image.Width > 0 && image.Height > 0);
            }
        }
    }

    private void TextPayload(string text, bool allowEmpty = false)
    {
        Require(text.Length <= PackProtocolLimits.MaximumTextInputLength && (allowEmpty || text.Length > 0) && !text.Contains('\0'));
        try { _textBytes += StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Invalid(); }
        if (_textBytes > PackProtocolLimits.MaximumLineBytes) throw new PackProtocolException(PackProtocolError.LineTooLarge);
    }

    private void Texts(IReadOnlyList<string> values)
    {
        Count(values, PackProtocolLimits.MaximumFiles);
        foreach (string value in values) Text(value);
    }

    private void Text(string? value, int maximum = PackProtocolLimits.MaximumTextLength, bool optional = false, bool allowEmpty = false)
    {
        if (optional && value is null) return;
        Require(value is not null && value.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Contains('\0'));
        try { _textBytes += StrictUtf8.GetByteCount(value!); }
        catch (EncoderFallbackException) { throw Invalid(); }
        if (_textBytes > PackProtocolLimits.MaximumLineBytes) throw new PackProtocolException(PackProtocolError.LineTooLarge);
    }

    private void VersionText(string value)
    {
        Text(value, 64); Require(Version.TryParse(value, out _));
    }

    // Lexical checks only, independent of the current OS. They do not authorize file access.
    private void AbsolutePath(string value)
    {
        Text(value);
        bool drive = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\';
        bool unix = value.StartsWith('/');
        bool unc = value.StartsWith("\\\\", StringComparison.Ordinal) && value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2 &&
            !value.StartsWith("\\\\?\\", StringComparison.Ordinal) && !value.StartsWith("\\\\.\\", StringComparison.Ordinal);
        Require(drive || unix || unc);
        Require(!value.Any(c => c < ' '));
        Require(!value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Any(p => p is "." or ".."));
    }

    private void RelativePath(string value)
    {
        Text(value);
        Require(!value.StartsWith('/') && !value.Contains('\\'));
        foreach (string segment in value.Split('/')) Leaf(segment);
    }

    private void Leaf(string value)
    {
        Text(value, 255);
        Require(value is not ("." or "..") && !value.EndsWith('.') && !value.EndsWith(' ') &&
            !value.Any(c => c < ' ' || ":/\\<>\"|?*".Contains(c)));
        string stem = value.Split('.')[0].ToUpperInvariant();
        Require(stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'));
    }

    private static void Id(Guid id) => Require(id != Guid.Empty);
    private static void Count<T>(IReadOnlyCollection<T>? values, int maximum) => Require(values is not null && values.Count <= maximum);
    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw Invalid();
    }
    private static PackProtocolException Invalid() => new(PackProtocolError.InvalidMessage);
}
