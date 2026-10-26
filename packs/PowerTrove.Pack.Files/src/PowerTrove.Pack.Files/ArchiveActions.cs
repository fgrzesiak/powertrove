using System.IO.Compression;
using FileKit.Archive;

namespace PowerTrove.Pack.Files;

internal static class ArchiveErrors
{
    public static FileProcessingException Wrap(ArchiveException exception) => new(SR.GetString(exception switch
    {
        ArchiveLimitExceededException => "ArchiveLimitExceeded",
        UnsafeArchiveEntryException => "ArchiveUnsafeEntry",
        InvalidArchiveException => "ArchiveInvalid",
        _ => "ArchiveError"
    }), exception);
}

public sealed record ZipOptions(CompressionLevel Level);

/// <summary>Creates one ZIP archive from the selected files and folders.</summary>
public sealed class CompressToZipAction : CombineFilesAction<ZipOptions>
{
    private static IReadOnlyList<ActionParameterDefinition> ParameterDefinitions => new[]
    {
        ActionParameterDefinition.Choice("Compression", SR.GetString("CompressToZipAction_ParamTitle_Compression"), "optimal", new[]
        {
            new ActionParameterChoice("optimal", SR.GetString("CompressToZipAction_ParamChoice_Optimal")),
            new ActionParameterChoice("fastest", SR.GetString("CompressToZipAction_ParamChoice_Fastest")),
            new ActionParameterChoice("store", SR.GetString("CompressToZipAction_ParamChoice_Store"))
        })
    };

    private static readonly FileInputFilter Filter = new("files or folders", extensions: null, formats: null, acceptFiles: true, acceptDirectories: true);

    public CompressToZipAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            Id: "powertrove.files.zip",
            Title: SR.GetString("CompressToZipAction_Title"),
            Description: SR.GetString("CompressToZipAction_Description"),
            Accepts: new HashSet<ContentKind> { ContentKind.Files },
            SearchTerms: new[] { "zip", "compress", "archive", "pack" },
            RequiresPreview: true,
            ChangesFiles: true,
            Parameters: ParameterDefinitions,
            Selection: SelectionRequirements.Any());
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => Filter;
    protected override int MinimumInputs => 1;

    protected override ZipOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Compression") switch
    {
        "fastest" => CompressionLevel.Fastest,
        "store" => CompressionLevel.NoCompression,
        _ => CompressionLevel.Optimal
    });

    protected override string DescribeOperation(int inputCount, ZipOptions options) =>
        SR.Format("CompressToZipAction_Operation_Description", Plural(inputCount, SR.GetString("CompressToZipAction_Operation_Item"), SR.GetString("CompressToZipAction_Operation_Items")));

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, ZipOptions options)
    {
        if (inputs.Count == 1)
            return new PlannedOutput(inputs[0].IsDirectory ? inputs[0].Name : Path.GetFileNameWithoutExtension(inputs[0].Name), ".zip");
        string? folder = Path.GetFileName(Path.GetDirectoryName(inputs[0].Path!));
        return new PlannedOutput(string.IsNullOrEmpty(folder) ? "Archive" : folder, ".zip");
    }

    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, ZipOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        return output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension, async (stream, ct) =>
        {
            try
            {
                await ZipBuilder.CreateAsync(stream, items.Select(i => i.Path).ToList(),
                    new ZipCreationOptions { CompressionLevel = options.Level }, progress, ct).ConfigureAwait(false);
            }
            catch (ArchiveException ex)
            {
                throw ArchiveErrors.Wrap(ex);
            }
        }, cancellationToken);
    }
}

public sealed record ExtractOptions;

/// <summary>Extracts each selected ZIP archive into a new folder next to it.</summary>
public sealed class ExtractZipAction : PerFileAction<ExtractOptions>
{
    private static readonly FileInputFilter Filter = new("ZIP archives", new[] { ".zip" }, new[] { FileFormats.Zip });

    public ExtractZipAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            Id: "powertrove.files.unzip",
            Title: SR.GetString("ExtractZipAction_Title"),
            Description: SR.GetString("ExtractZipAction_Description"),
            Accepts: new HashSet<ContentKind> { ContentKind.Files },
            SearchTerms: new[] { "unzip", "extract", "archive", "unpack", "zip" },
            RequiresPreview: true,
            ChangesFiles: true,
            Selection: SelectionRequirements.Any("archive"));
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => Filter;
    protected override string ProgressVerb => SR.GetString("ExtractZipAction_ProgressVerb");

    protected override ExtractOptions ReadOptions(ActionParameterValues values) => new();

    protected override string DescribeOperation(int inputCount, ExtractOptions options) =>
        SR.Format("ExtractZipAction_Operation_Description", Plural(inputCount, SR.GetString("ExtractZipAction_Operation_Archive"), SR.GetString("ExtractZipAction_Operation_Archives")));

    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile input, ExtractOptions options)
    {
        yield return new PlannedOutput(Path.GetFileNameWithoutExtension(input.Name), string.Empty, IsDirectory: true);
    }

    protected override Task ProcessAsync(FileWorkItem item, ExtractOptions options, FileOutputWriter output, CancellationToken cancellationToken) =>
        output.WriteDirectoryAsync(item.Directory, item.BaseName, async (directory, ct) =>
        {
            try
            {
                await SafeZipExtractor.ExtractAsync(item.Path, directory, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (ArchiveException ex)
            {
                throw ArchiveErrors.Wrap(ex);
            }
        }, cancellationToken);
}
