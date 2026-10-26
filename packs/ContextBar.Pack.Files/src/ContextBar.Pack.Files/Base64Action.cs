using System.Security.Cryptography;
using System.Text;

namespace ContextBar.Pack.Files;

public sealed class Base64Action : PerFileAction<bool>
{
    private readonly bool _decode;
    public Base64Action(FileActionServices services, bool decode) : base(services) => _decode = decode;
    public override ActionDescriptor Descriptor => FileTools.Descriptor(_decode ? "base64-decode" : "base64-encode", _decode ? "Base64Decode" : "Base64Encode");
    protected override FileInputFilter Input => _decode ? new(SR.GetString("FileItems"), new[] { ".b64", ".txt" }, null) : FileTools.Any();
    protected override bool ReadOptions(ActionParameterValues v) => _decode;
    protected override string DescribeOperation(int n, bool o) => SR.Format("ProcessItems", n);
    protected override IEnumerable<PlannedOutput> PlanOutputs(SelectedFile file, bool o)
    {
        if (!o) yield return new(file.Name, ".b64");
        else if (file.Extension.Equals(".b64", StringComparison.OrdinalIgnoreCase))
        { string name = Path.GetFileNameWithoutExtension(file.Name); yield return new(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name)); }
        else yield return new(Path.GetFileNameWithoutExtension(file.Name), ".bin");
    }
    protected override Task ProcessAsync(FileWorkItem item, bool decode, FileOutputWriter output, CancellationToken ct)
    {
        var plan = PlanOutputs(item.File, decode).Single();
        return output.WriteFileAsync(item.Directory, plan.BaseName, plan.Extension, async (destination, token) =>
        {
            await using var source = Services.FileSystem.OpenRead(item.Path);
            if (!decode)
            {
                byte[] buffer = new byte[57];
                while (true)
                {
                    int n = await source.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false); if (n == 0) break;
                    byte[] line = Encoding.ASCII.GetBytes(Convert.ToBase64String(buffer, 0, n) + "\r\n"); await destination.WriteAsync(line, token).ConfigureAwait(false);
                }
            }
            else
            {
                // Validate the alphabet and padding before the transactional output is committed.
                byte[] buffer = new byte[81920]; char[] quartet = new char[4]; int used = 0; bool ended = false; int n; byte[] decodedBuffer = new byte[81920]; int decodedLength = 0;
                while ((n = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buffer[i]; if (b is 9 or 10 or 13 or 32) continue;
                        if (ended || !(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'=')) throw new FileProcessingException(SR.GetString("InvalidBase64"));
                        quartet[used++] = (char)b;
                        if (used == 4)
                        {
                            if (!Convert.TryFromBase64Chars(quartet, decodedBuffer.AsSpan(decodedLength), out int count)) throw new FileProcessingException(SR.GetString("InvalidBase64"));
                            decodedLength += count; ended = quartet.Contains('='); used = 0;
                            if (decodedLength > decodedBuffer.Length - 3) { await destination.WriteAsync(decodedBuffer.AsMemory(0, decodedLength), token).ConfigureAwait(false); decodedLength = 0; }
                        }
                    }
                if (used != 0) throw new FileProcessingException(SR.GetString("InvalidBase64"));
                if (decodedLength > 0) await destination.WriteAsync(decodedBuffer.AsMemory(0, decodedLength), token).ConfigureAwait(false);
            }
        }, ct, allowEmpty: true);
    }
}
