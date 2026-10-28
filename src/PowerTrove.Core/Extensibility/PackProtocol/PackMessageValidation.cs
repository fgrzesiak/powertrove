using System.Text;
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
                Request(m.RequestId, m.ActionId, m.Selection, m.Table, m.Parameters, m.ExpiresAt); break;
            case ExecuteMessage m:
                Request(m.RequestId, m.ActionId, m.Selection, m.Table, m.Parameters, m.ExpiresAt); AbsolutePath(m.StagingDirectory); break;
            case PreviewResultMessage m:
                Id(m.RequestId); Text(m.PreviewText, optional: true);
                Texts(m.ExpectedOutputs); Texts(m.Warnings);
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
            case ShutdownMessage:
                break;
            default:
                throw Invalid();
        }
    }

    private void Request(Guid id, string actionId, FileSelectionPayload selection, TableSelectionPayload? table,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset expiresAt)
    {
        Id(id); Text(actionId, 256); Require(expiresAt != default);
        if (table is null) Selection(selection);
        else
        {
            // A table request never also authorizes file access.
            Require(selection?.Files is { Count: 0 }); Table(table);
        }
        Count(parameters, PackProtocolLimits.MaximumParameters);
        foreach (var parameter in parameters)
        {
            Text(parameter.Key, 256); Text(parameter.Value, allowEmpty: true);
        }
    }

    private void Selection(FileSelectionPayload selection)
    {
        Require(selection is not null);
        Count(selection.Files, PackProtocolLimits.MaximumFiles);
        Require(selection.Files.Count > 0);
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
        Text(receipt.ClipboardText, optional: true, allowEmpty: true);
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
