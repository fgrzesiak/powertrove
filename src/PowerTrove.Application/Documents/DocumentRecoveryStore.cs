using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerTrove.Application.Documents;

/// <summary>Unsaved editor text kept for one document after a crash or forced exit.</summary>
public sealed record RecoveredDraft(string Text, DateTimeOffset SavedAt)
{
    public override string ToString() => $"RecoveredDraft {{ SavedAt = {SavedAt:O} }}";
}

/// <summary>
/// Keeps the editor's unsaved text in the user's own PowerTrove folder (one file per document, named by a hash
/// of its path) until it is saved or discarded. Nothing is uploaded, logged or shown elsewhere. Drafts older
/// than <see cref="Retention"/> are removed.
/// </summary>
public sealed class DocumentRecoveryStore(string directory, TimeProvider? clock = null)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public const int MaximumTextLength = 2 * 1024 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task SaveAsync(string documentPath, string text, CancellationToken cancellationToken = default)
    {
        if (text.Length > MaximumTextLength) return;
        System.IO.Directory.CreateDirectory(Directory);
        string target = FileFor(documentPath);
        string temporary = target + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + ".tmp";
        var record = new DraftRecord(1, Normalize(documentPath), _clock.GetUtcNow(), text);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, record, RecoveryJson.Default.DraftRecord, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<RecoveredDraft?> LoadAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        string file = FileFor(documentPath);
        if (!File.Exists(file)) return null;
        try
        {
            if (new FileInfo(file).Length > MaximumTextLength * 7L) return null;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var record = await JsonSerializer.DeserializeAsync(stream, RecoveryJson.Default.DraftRecord, cancellationToken).ConfigureAwait(false);
            // A hash collision or a moved store must never show another document's text.
            if (record is not { Version: 1, Text: not null } || !string.Equals(record.Path, Normalize(documentPath), StringComparison.OrdinalIgnoreCase)
                || record.Text.Length > MaximumTextLength || _clock.GetUtcNow() - record.SavedAt > Retention) return null;
            return new RecoveredDraft(record.Text, record.SavedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    public void Delete(string documentPath)
    {
        try { File.Delete(FileFor(documentPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes expired and unreadable leftovers; called once per editor session.</summary>
    public void Prune()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Take(10_000))
                if (_clock.GetUtcNow() - File.GetLastWriteTimeUtc(file) > Retention) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string FileFor(string documentPath) =>
        Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(documentPath).ToUpperInvariant())))[..32].ToLowerInvariant() + ".json");

    private static string Normalize(string path) => Path.GetFullPath(path);

    internal sealed record DraftRecord(int Version, string Path, DateTimeOffset SavedAt, string Text);
}

[JsonSerializable(typeof(DocumentRecoveryStore.DraftRecord))]
internal sealed partial class RecoveryJson : JsonSerializerContext;
