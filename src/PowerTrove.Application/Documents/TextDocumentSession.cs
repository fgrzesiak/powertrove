using System.Security.Cryptography;
using System.Text;
using FileKit.Text;

namespace PowerTrove.Application.Documents;

public enum TextDocumentLoadFailure { None, TooLarge, NotText, Unreadable }
public enum TextDocumentSaveResult { Saved, SavedAsUtf8, ChangedOnDisk, ReadOnly, Failed }

/// <summary>
/// The host editor's one open text file. Text is held with "\n" line breaks; saving restores the file's
/// line-break style, encoding and byte order mark. Saving never happens implicitly: it writes a temporary
/// file in the same folder and replaces the original only when nothing else changed it since loading.
/// Contents and paths are user data: never log them.
/// </summary>
public sealed class TextDocumentSession
{
    /// <summary>Bytes of a file the editor opens.</summary>
    public const long MaximumFileBytes = 2L * 1024 * 1024;

    private Encoding _encoding;
    private byte[] _preamble;
    private string _newLine;
    private FileStamp _stamp;

    private TextDocumentSession(string path, string text, Encoding encoding, byte[] preamble, string newLine, FileStamp stamp)
    {
        Path = path; SavedText = text; _encoding = encoding; _preamble = preamble; _newLine = newLine; _stamp = stamp;
    }

    public string Path { get; private set; }
    /// <summary>The text as last loaded or saved, with "\n" line breaks.</summary>
    public string SavedText { get; private set; }
    public string NewLine => _newLine;
    public string EncodingName => _encoding.WebName + (_preamble.Length > 0 ? " (BOM)" : string.Empty);

    public static async Task<(TextDocumentSession? Session, TextDocumentLoadFailure Failure)> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            byte[] bytes;
            await using (var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous))
            {
                if (input.Length > MaximumFileBytes) return (null, TextDocumentLoadFailure.TooLarge);
                using var memory = new MemoryStream();
                await input.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                if (memory.Length > MaximumFileBytes) return (null, TextDocumentLoadFailure.TooLarge);
                bytes = memory.ToArray();
            }
            DecodedText decoded;
            try { decoded = TextDecoding.Decode(bytes); }
            catch (DecoderFallbackException) { return (null, TextDocumentLoadFailure.NotText); }
            if (decoded.Text.Contains('\0')) return (null, TextDocumentLoadFailure.NotText);
            string newLine = DetectNewLine(decoded.Text);
            byte[] preamble = decoded.HasBom ? decoded.Encoding.GetPreamble() : [];
            return (new TextDocumentSession(full, NormalizeNewLines(decoded.Text), WithoutPreamble(decoded.Encoding), preamble, newLine, FileStamp.Of(full, bytes)), TextDocumentLoadFailure.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, TextDocumentLoadFailure.Unreadable);
        }
    }

    /// <summary>"\r\n" and lone "\r" (as a WinUI TextBox produces) become "\n".</summary>
    public static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>True when the file on disk differs from the version this session loaded or saved.</summary>
    public bool HasChangedOnDisk()
    {
        try { return !_stamp.Matches(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Saves to the current file. <paramref name="overwriteExternalChanges"/> is the user's explicit choice.</summary>
    public Task<TextDocumentSaveResult> SaveAsync(string text, bool overwriteExternalChanges = false, CancellationToken cancellationToken = default)
    {
        if (!overwriteExternalChanges && HasChangedOnDisk()) return Task.FromResult(TextDocumentSaveResult.ChangedOnDisk);
        return WriteAsync(Path, text, cancellationToken);
    }

    /// <summary>Saves to a path the user chose in a save dialog (which already confirmed replacing it) and continues there.</summary>
    public Task<TextDocumentSaveResult> SaveAsAsync(string path, string text, CancellationToken cancellationToken = default) =>
        WriteAsync(System.IO.Path.GetFullPath(path), text, cancellationToken);

    private async Task<TextDocumentSaveResult> WriteAsync(string target, string text, CancellationToken cancellationToken)
    {
        string normalized = NormalizeNewLines(text);
        string content = _newLine == "\n" ? normalized : normalized.Replace("\n", _newLine, StringComparison.Ordinal);
        var encoding = _encoding;
        var result = TextDocumentSaveResult.Saved;
        byte[] body;
        try { body = encoding.GetBytes(content); }
        catch (EncoderFallbackException)
        {
            // A legacy code page cannot hold every character the user typed: keep the text, switch to UTF-8.
            encoding = new UTF8Encoding(false); body = encoding.GetBytes(content); result = TextDocumentSaveResult.SavedAsUtf8;
        }
        byte[] preamble = result == TextDocumentSaveResult.Saved ? _preamble : [];
        byte[] bytes = [.. preamble, .. body];
        string directory = System.IO.Path.GetDirectoryName(target)!;
        string temporary = System.IO.Path.Combine(directory, ".~pt" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".tmp");
        try
        {
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) return TextDocumentSaveResult.ReadOnly;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            // Replace keeps the original's attributes and permissions; a new file is moved without overwriting.
            if (File.Exists(target)) File.Replace(temporary, target, null, ignoreMetadataErrors: true);
            else File.Move(temporary, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (ex is OperationCanceledException) throw;
            return ex is UnauthorizedAccessException ? TextDocumentSaveResult.ReadOnly : TextDocumentSaveResult.Failed;
        }
        Path = target; SavedText = normalized; _encoding = WithoutPreamble(encoding); _preamble = preamble;
        _stamp = FileStamp.Of(target, bytes);
        return result;
    }

    private static string DetectNewLine(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf || crlf == 0 && lf == 0 ? "\r\n" : "\n";
    }

    private static Encoding WithoutPreamble(Encoding encoding) => encoding switch
    {
        UTF8Encoding => new UTF8Encoding(false),
        UnicodeEncoding u => new UnicodeEncoding(u.GetPreamble() is [0xFE, 0xFF], false),
        UTF32Encoding u => new UTF32Encoding(u.GetPreamble() is [0, 0, 0xFE, 0xFF], false),
        _ => encoding
    };

    private sealed record FileStamp(long Length, DateTime LastWriteUtc, byte[] Hash)
    {
        public static FileStamp Of(string path, byte[] bytes) => new(bytes.Length, File.GetLastWriteTimeUtc(path), SHA256.HashData(bytes));

        public bool Matches(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != Length) return false;
            if (info.LastWriteTimeUtc == LastWriteUtc) return true;
            // Timestamps can change without content changes (sync tools); compare the content then.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(Hash);
        }
    }
}
