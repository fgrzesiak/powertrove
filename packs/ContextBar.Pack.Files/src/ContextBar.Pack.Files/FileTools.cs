using System.Security.Cryptography;
using System.Text;

namespace ContextBar.Pack.Files;

internal static class FileTools
{
    public static ActionParameterDefinition Choice(string name, string fallback, params string[] choices) => ActionParameterDefinition.Choice(name, SR.GetString(name), fallback, choices.Select(c => new ActionParameterChoice(c, SR.GetString(c))).ToArray());
    public static FileInputFilter Any(bool directories = false) => new(SR.GetString("FileItems"), null, null, acceptDirectories: directories);
    public static ActionDescriptor Descriptor(string id, string title, bool changes = true, IReadOnlyList<ActionParameterDefinition>? parameters = null, SelectionRequirements? selection = null) =>
        new("contextbar.files." + id, SR.GetString(title), SR.GetString("ToolsDescription"), new HashSet<ContentKind> { ContentKind.Files }, new[] { id, "files", "tools" }, changes, changes, Parameters: parameters, Selection: selection ?? SelectionRequirements.Any());
    public static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public static Task WriteText(FileOutputWriter output, string directory, string name, string extension, string text, CancellationToken ct, bool bom = false) =>
        output.WriteFileAsync(directory, name, extension, async (stream, token) =>
        { await using var writer = new StreamWriter(stream, new UTF8Encoding(bom), leaveOpen: true); await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false); }, ct, allowEmpty: true);
    public static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    public static async Task<byte[]> Hash(string path, bool partial, CancellationToken ct, bool sha512 = false)
    {
        if (IsLink(path)) throw new FileProcessingException(SR.GetString("LinksUnsupported"));
        await using var stream = File.OpenRead(path);
        if (partial)
        {
            byte[] bytes = new byte[65536]; int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
            return SHA256.HashData(bytes.AsSpan(0, count));
        }
        return sha512 ? await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false) : await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }
}
