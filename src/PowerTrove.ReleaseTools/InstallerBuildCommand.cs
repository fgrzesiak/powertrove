using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerTrove.ReleaseTools;

/// <summary>Explicit installer build from the byte-for-byte G5 candidate payload.</summary>
public static class InstallerBuildCommand
{
    public static async Task ExecuteAsync(string root, string artifacts, string version, string iscc,
        IProcessRunner runner, CancellationToken token = default)
    {
        if (!Version.TryParse(version, out _) || version.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new ArgumentException("A numeric version is required.", nameof(version));
        if (string.IsNullOrWhiteSpace(iscc) || !File.Exists(iscc) ||
            !string.Equals(Path.GetFileName(iscc), "ISCC.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("An explicitly selected Inno Setup ISCC.exe is required.");
        root = Path.GetFullPath(root);
        artifacts = Path.GetFullPath(artifacts);
        var script = Path.Combine(root, "packaging", "windows", "PowerTrove.iss");
        if (!File.Exists(script)) throw new FileNotFoundException("Maintained installer definition is missing.");
        string name = "PowerTrove-v" + version + "-win-x64";
        string stage = Path.Combine(artifacts, name);
        string zip = Path.Combine(artifacts, name + ".zip");
        if (!Directory.Exists(stage) || !File.Exists(zip))
            throw new FileNotFoundException("Matching staged payload and ZIP must exist.");
        if (script.Contains('"') || stage.Contains('"') || artifacts.Contains('"'))
            throw new InvalidDataException("Unsafe tool argument.");
        await ValidateExactPayload(stage, zip, version, artifacts, token);
        string installer = Path.Combine(artifacts, name + "-installer.exe");
        if (File.Exists(installer) || Directory.Exists(installer))
            throw new IOException("Installer output already exists. Use a fresh candidate directory.");
        // No launch, elevation, registration or network operation is performed by this command.
        string args = "/Q /DAppVersion=" + version + " /DPayloadDir=\"" + stage + "\" /O\"" + artifacts + "\" \"" + script + "\"";
        var compiled = await runner.RunAsync(iscc, args, root);
        if (compiled.ExitCode != 0) throw new InvalidOperationException("Installer compilation failed (inspect local ISCC output).");
        if (!File.Exists(installer) || new FileInfo(installer).Length == 0)
            throw new InvalidDataException("Installer compiler did not produce the expected artifact.");
        await using var input = File.OpenRead(installer);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
        string sums = Path.Combine(artifacts, "SHA256SUMS");
        string previous = await File.ReadAllTextAsync(sums, token);
        await File.WriteAllTextAsync(sums, previous.TrimEnd('\r', '\n') + "\n" +
            hash + " *" + Path.GetFileName(installer) + "\n", token);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Staged payload contains a linked directory.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staged payload contains a reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var path in EnumerateSafeFiles(entry)) yield return path;
            }
            else yield return entry;
        }
    }

    internal static async Task ValidateExactPayload(string stage, string zip, string version, string artifacts,
        CancellationToken token = default)
    {
        var shaFile = Path.Combine(artifacts, "SHA256SUMS");
        if (!File.Exists(shaFile)) throw new InvalidDataException("Candidate SHA256SUMS is missing.");
        string hash;
        await using (var input = File.OpenRead(zip))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
        string expected = hash + " *" + Path.GetFileName(zip);
        if (!File.ReadAllLines(shaFile).Any(s => string.Equals(s.Trim(), expected, StringComparison.Ordinal)))
            throw new InvalidDataException("Candidate SHA-256 checksum mismatch.");
        using var archive = ZipFile.OpenRead(zip);
        var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/'))
            .ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Any(e => e.FullName.Contains('\\') ||
            e.FullName.StartsWith('/') || e.FullName.TrimEnd('/').Split('/').Any(s => s is "." or ".." or "")))
            throw new InvalidDataException("Unsafe ZIP entry.");
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("Candidate manifest is invalid.");
        using (var document = await JsonDocument.ParseAsync(manifestEntry.Open(), cancellationToken: token))
        {
            if (document.RootElement.GetProperty("Version").GetString() != version ||
                document.RootElement.GetProperty("Target").GetString() != "win-x64")
                throw new InvalidDataException("Candidate version or target mismatch.");
            string? revision = document.RootElement.GetProperty("GitRevision").GetString();
            if (revision is null || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
                throw new InvalidDataException("Candidate source revision is not verified.");
        }
        var files = EnumerateSafeFiles(stage).ToArray();
        if (files.Length == 0 || entries.Count != files.Length)
            throw new InvalidDataException("Stage/ZIP file inventory mismatch.");
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Payload contains a reparse point.");
            string relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
            if (!entries.TryGetValue(relative, out var entry) || entry.FullName != relative ||
                entry.Length != new FileInfo(file).Length)
                throw new InvalidDataException("Payload file inventory mismatch.");
            await using var zipStream = entry.Open();
            await using var localStream = File.OpenRead(file);
            byte[] zipHash = await SHA256.HashDataAsync(zipStream, token);
            byte[] localHash = await SHA256.HashDataAsync(localStream, token);
            if (!zipHash.AsSpan().SequenceEqual(localHash))
                throw new InvalidDataException("Staged file differs from exact ZIP candidate.");
        }
    }
}
