using System.Security.Cryptography;
using System.Text;

namespace PowerTrove.Pack.Files;

public sealed record SplitOptions(string Mode, int PartSizeMb, int Parts);

public sealed class SplitFileAction : PerFileAction<SplitOptions>
{
    public SplitFileAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => FileTools.Descriptor("split", "SplitFile", parameters: new[]
    {
        FileTools.Choice("Mode", "size", "size", "parts"),
        ActionParameterDefinition.Integer("PartSizeMb", SR.GetString("PartSizeMb"), 100, 1, 100000).VisibleWhen("Mode", "size"),
        ActionParameterDefinition.Integer("Parts", SR.GetString("PartsCount"), 2, 2, 1000).VisibleWhen("Mode", "parts")
    });
    protected override FileInputFilter Input => FileTools.Any();
    protected override SplitOptions ReadOptions(ActionParameterValues v) => new(v.GetChoice("Mode"), v.GetInt32("PartSizeMb"), v.GetInt32("Parts"));
    protected override string DescribeOperation(int n, SplitOptions o) => SR.Format("ProcessItems", n);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, SplitOptions o) { yield return new(file.Name + " (parts)", "", true); }
    protected override Task ProcessAsync(FileWorkItem item, SplitOptions options, FileOutputWriter output, CancellationToken ct) => output.WriteDirectoryAsync(item.Directory, item.File.Name + " (parts)", async (directory, token) =>
    {
        await using var source = Services.FileSystem.OpenRead(item.Path);
        long length = source.Length;
        long size = options.Mode == "parts" ? Math.Max(1, length / options.Parts + (length % options.Parts == 0 ? 0 : 1)) : options.PartSizeMb * 1024L * 1024L;
        long count = options.Mode == "parts" ? options.Parts : Math.Max(1, length / size + (length % size == 0 ? 0 : 1));
        if (count > 1000) throw new FileProcessingException(SR.GetString("PartLimit"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[81920]; long copied = 0;
        for (int part = 1; part <= count; part++)
        {
            token.ThrowIfCancellationRequested();
            await using var target = new FileStream(Path.Combine(directory, item.File.Name + "." + part.ToString("000", System.Globalization.CultureInfo.InvariantCulture)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long remaining = Math.Min(size, length - copied);
            while (remaining > 0)
            {
                int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (n == 0) throw new FileProcessingException(SR.GetString("InputChanged"));
                hash.AppendData(buffer, 0, n); await target.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false); remaining -= n; copied += n;
            }
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw new FileProcessingException(SR.GetString("InputChanged"));
        await File.WriteAllTextAsync(Path.Combine(directory, item.File.Name + ".sha256"), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() + " *" + item.File.Name + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
    }, ct);
}

public sealed class JoinFileAction : CombineFilesAction<bool>
{
    public JoinFileAction(FileActionServices services) : base(services) { }
    public override ActionDescriptor Descriptor => FileTools.Descriptor("join", "JoinFiles");
    protected override int MinimumInputs => 1;
    protected override FileInputFilter Input => new(SR.GetString("FileItems"), Enumerable.Range(1, 1000).Select(n => "." + n.ToString("000", System.Globalization.CultureInfo.InvariantCulture)), null);
    protected override bool ReadOptions(ActionParameterValues v) => true;
    protected override string DescribeOperation(int n, bool o) => SR.GetString("JoinDescription");
    protected override PlannedOutput PlanOutput(IReadOnlyList<SelectedFile> inputs, bool o)
    {
        string name = Path.GetFileNameWithoutExtension(inputs[0].Name);
        return new(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
    }
    protected override async Task ProcessAsync(IReadOnlyList<FileWorkItem> items, bool o, FileOutputWriter output, IProgress<double>? progress, CancellationToken ct)
    {
        string directory = items[0].Directory, name = Path.GetFileNameWithoutExtension(items[0].File.Name);
        if (items.Any(i => i.Directory != directory || Path.GetFileNameWithoutExtension(i.File.Name) != name)) throw new FileProcessingException(SR.GetString("PartMismatch"));
        var parts = new SortedDictionary<int, string>();
        foreach (string path in Directory.EnumerateFiles(directory, name + ".*"))
        {
            ct.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).TrimStart('.');
            if (extension.Length is < 3 or > 4 || !extension.All(char.IsAsciiDigit)) continue;
            if (!int.TryParse(extension, out int number) || number < 1 || number > 1000 || !parts.TryAdd(number, path)) throw new FileProcessingException(SR.GetString("PartMismatch"));
        }
        if (parts.Count == 0 || parts.Keys.First() != 1 || parts.Keys.Last() != parts.Count) throw new FileProcessingException(SR.GetString("MissingPart"));
        string? expected = null, checksum = Path.Combine(directory, name + ".sha256");
        if (File.Exists(checksum))
        {
            if (new FileInfo(checksum).Length > 16384 || FileTools.IsLink(checksum)) throw new FileProcessingException(SR.GetString("InvalidChecksums"));
            string line = (await File.ReadAllTextAsync(checksum, ct).ConfigureAwait(false)).Trim();
            if (line.Length < 67 || line[64] != ' ' || !line[..64].All(Uri.IsHexDigit)) throw new FileProcessingException(SR.GetString("InvalidChecksums"));
            expected = line[..64];
        }
        var plan = PlanOutput(items.Select(i => i.File).ToArray(), o);
        await output.WriteFileAsync(directory, plan.BaseName, plan.Extension, async (target, token) =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[81920]; int completed = 0;
            foreach (string path in parts.Values)
            {
                if (FileTools.IsLink(path)) throw new FileProcessingException(SR.GetString("LinksUnsupported"));
                await using var source = Services.FileSystem.OpenRead(path); int n;
                while ((n = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) { hash.AppendData(buffer, 0, n); await target.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false); }
                progress?.Report((double)++completed / parts.Count);
            }
            if (expected is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new FileProcessingException(SR.GetString("HashMismatch"));
        }, ct, allowEmpty: true).ConfigureAwait(false);
    }
}
