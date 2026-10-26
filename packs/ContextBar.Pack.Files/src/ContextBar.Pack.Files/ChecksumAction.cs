using System.Security.Cryptography;
using System.Text;

namespace ContextBar.Pack.Files;

public sealed record ChecksumOptions(string Algorithm);

/// <summary>
/// Writes a checksum file in the common "&lt;hash&gt; *&lt;name&gt;" format (sha256sum compatible)
/// for the selected files, so downloads and copies can be verified later.
/// </summary>
public sealed class ChecksumAction : CombineFilesAction<ChecksumOptions>
{
    private static IReadOnlyList<ActionParameterDefinition> ParameterDefinitions => new[]
    {
        ActionParameterDefinition.Choice("Algorithm", SR.GetString("ChecksumAction_ParamTitle_Algorithm"), "sha256", new[]
        {
            new ActionParameterChoice("sha256", "SHA-256"),
            new ActionParameterChoice("sha512", "SHA-512")
        })
    };

    private static FileInputFilter Filter => new(SR.GetString("ChecksumAction_FilterDescription"), extensions: null, formats: null);

    public ChecksumAction(FileActionServices services) : base(services)
    {
        Descriptor = new ActionDescriptor(
            Id: "contextbar.files.checksum",
            Title: SR.GetString("ChecksumAction_Title"),
            Description: SR.GetString("ChecksumAction_Description"),
            Accepts: new HashSet<ContentKind> { ContentKind.Files },
            SearchTerms: new[] { "checksum", "hash", "sha256", "sha512", "verify", "integrity" },
            RequiresPreview: true,
            ChangesFiles: true,
            Parameters: ParameterDefinitions,
            Selection: SelectionRequirements.Any());
    }

    public override ActionDescriptor Descriptor { get; }
    protected override FileInputFilter Input => Filter;
    protected override int MinimumInputs => 1;

    protected override ChecksumOptions ReadOptions(ActionParameterValues values) => new(values.GetChoice("Algorithm"));

    protected override string DescribeOperation(int inputCount, ChecksumOptions options) =>
        SR.Format("ChecksumAction_Operation_Description", options.Algorithm == "sha512" ? "SHA-512" : "SHA-256", Plural(inputCount, SR.GetString("ChecksumAction_Operation_File"), SR.GetString("ChecksumAction_Operation_Files")));

    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, ChecksumOptions options) =>
        inputs.Count == 1
            ? new PlannedOutput(inputs[0].Name, "." + options.Algorithm)
            : new PlannedOutput("checksums", "." + options.Algorithm);

    protected override Task ProcessAsync(IReadOnlyList<FileWorkItem> items, ChecksumOptions options, FileOutputWriter output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var planned = PlanOutput(items.Select(i => i.File).ToList(), options);
        return output.WriteFileAsync(items[0].Directory, planned.BaseName, planned.Extension, async (stream, ct) =>
        {
            var lines = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                await using var input = Services.FileSystem.OpenRead(items[i].Path);
                byte[] hash = options.Algorithm == "sha512"
                    ? await SHA512.HashDataAsync(input, ct).ConfigureAwait(false)
                    : await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
                // Names are relative to the checksum file, which sits in the first item's folder.
                string name = string.Equals(items[i].Directory, items[0].Directory, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(items[i].Path)
                    : items[i].Path;
                lines.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append(" *").Append(name).Append('\n');
                progress?.Report((double)(i + 1) / items.Count);
            }
            byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(lines.ToString());
            await stream.WriteAsync(content, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}
