using System.Text;

namespace ContextBar.Pack.Files;

public sealed class VerifyChecksumsAction : FileReportAction
{
    public VerifyChecksumsAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => FileTools.Descriptor("verify-checksums", "VerifyChecksums", parameters: new[] { FileTools.Choice("Output", "summary", "summary", "file") }, selection: SelectionRequirements.Single());
    protected override FileInputFilter Input => new(SR.GetString("FileItems"), new[] { ".sha256", ".sha512" }, null);
    protected override int MaximumInputs => 1;
    protected override string ReadOptions(ActionParameterValues v) => v.GetChoice("Output");
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, string o) => new(inputs[0].Name + " (verification)", ".txt");
    protected override IReadOnlyList<string> PlanExpectedOutputs(IReadOnlyList<SelectedFile> inputs, string options) => options == "file" ? base.PlanExpectedOutputs(inputs, options) : Array.Empty<string>();
    protected override async Task<FileReport> GenerateAsync(IReadOnlyList<FileWorkItem> items, string options, IProgress<double>? progress, CancellationToken ct)
    {
        string manifest = items[0].Path;
        if (new FileInfo(manifest).Length > 8 * 1024 * 1024) throw new FileProcessingException(SR.GetString("ManifestLimit"));
        var report = new StringBuilder(); int ok = 0, mismatch = 0, missing = 0, count = 0;
        using var reader = new StreamReader(Services.FileSystem.OpenRead(manifest), new UTF8Encoding(false, true));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
        {
            ct.ThrowIfCancellationRequested(); if (line.Length == 0) continue;
            if (++count > 10000) throw new FileProcessingException(SR.GetString("ManifestLimit"));
            int digits = items[0].File.Extension.Equals(".sha512", StringComparison.OrdinalIgnoreCase) ? 128 : 64;
            if (line.Length < digits + 3 || line[digits] != ' ' || line[digits + 1] is not ('*' or ' ') || !line[..digits].All(Uri.IsHexDigit)) throw new FileProcessingException(SR.GetString("InvalidChecksums"));
            string name = line[(digits + 2)..];
            string path = Path.GetFullPath(Path.Combine(items[0].Directory, name));
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) throw new FileProcessingException(SR.GetString("RemoteUnsupported"));
            string status;
            if (!File.Exists(path)) { missing++; status = "missing"; }
            else if (Convert.ToHexString(await FileTools.Hash(path, false, ct, digits == 128).ConfigureAwait(false)).Equals(line[..digits], StringComparison.OrdinalIgnoreCase)) { ok++; status = "OK"; }
            else { mismatch++; status = "mismatched"; }
            report.Append(FileTools.Escape(name)).Append(',').Append(status).Append("\r\n");
        }
        if (count == 0) throw new FileProcessingException(SR.GetString("InvalidChecksums"));
        return new(report.ToString(), SR.Format("VerifySummary", ok, mismatch, missing), options == "file");
    }
}
