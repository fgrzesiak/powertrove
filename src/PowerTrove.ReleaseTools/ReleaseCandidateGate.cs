using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerTrove.ReleaseTools;

public sealed record ReleaseGateItem(string Id, string Status, string Detail, string? EvidenceRef);
public sealed record ReleaseGateReport(
    string Version, string Revision, string Target, string PayloadSha256,
    bool CandidateVerified, bool MandatoryChecksPass, bool PublicReady,
    IReadOnlyList<ReleaseGateItem> Checks, string? ReleaseVersion = null);

/// <summary>
/// Local read-only acceptance gate tied to an exact ZIP, revision, and evidence identity.
/// It cannot sign, tag, publish, install, browse, or generate evidence on behalf of a tester.
/// </summary>
public static class ReleaseCandidateGate
{
    private static readonly string[] MandatoryChecks =
    [
        "package-offline-clean-machine", "explorer-capture", "excel-csv", "excel-json",
        "zip-create", "pdf-merge", "image-convert", "settings-no-selection",
        "two-actions-one-session", "outputs-original-unchanged", "outputs-collision",
        "cancel-partial-results", "locked-files-permissions", "offline-core",
        "keyboard-only", "narrator-focus", "high-contrast",
        "dpi-100", "dpi-150", "dpi-200", "multi-monitor",
        "warm-invocation-p95", "query-p95", "cold-start",
        "installer-standard-user", "installer-upgrade", "installer-uninstall",
        "installer-interrupted-upgrade", "runtime-inventory",
        "license-and-notices", "beta-worksheet"
    ];
    private static readonly string[] PublicChecks =
    [
        "redistribution-clearance", "publisher-signature", "installer-signature",
        "owner-approval"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const int MaximumEvidenceBytes = 1024 * 1024;

    public sealed record EvidenceDocument(
        string Revision, string PayloadSha256, string Version,
        IReadOnlyList<EvidenceCheck> Checks, string? InstallerSha256 = null);
    public sealed record EvidenceCheck(
        string Id, string Status, string Revision, string PayloadSha256,
        string? EvidenceRef = null, string? Environment = null,
        DateTimeOffset? ObservedUtc = null, string? Defect = null);

    public static async Task<ReleaseGateReport> VerifyAsync(string zipPath, string evidencePath,
        string reportDirectory, bool publicAudience = false, string? installerPath = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Release ZIP is missing.");
        string payloadHash;
        await using (var stream = File.OpenRead(zipPath))
            payloadHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        VerifySha256Sums(zipPath, payloadHash);

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json");
        if (entry is null || entry.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Candidate manifest is missing or invalid.");
        JsonDocument manifest;
        await using (var input = entry.Open())
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, token);
            if (buffer.Length > 1024 * 1024) throw new InvalidDataException("Candidate manifest is oversized.");
            manifest = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        using (manifest)
        {
            string version = ReadText(manifest.RootElement, "Version");
            string releaseVersion = manifest.RootElement.TryGetProperty("ReleaseVersion", out _)
                ? ReadText(manifest.RootElement, "ReleaseVersion") : version;
            if (releaseVersion != version && !System.Text.RegularExpressions.Regex.IsMatch(releaseVersion,
                "\\A" + System.Text.RegularExpressions.Regex.Escape(version) + "-alpha\\.[1-9][0-9]{0,5}\\z"))
                throw new InvalidDataException("Candidate release channel is invalid.");
            string revision = ReadText(manifest.RootElement, "GitRevision");
            string target = ReadText(manifest.RootElement, "Target");
            string dateText = ReadText(manifest.RootElement, "Date");
            if (!Version.TryParse(version, out _) || !IsHex(revision, 40) || target != "win-x64" ||
                !DateTimeOffset.TryParse(dateText, out DateTimeOffset created))
                throw new InvalidDataException("Candidate identity is invalid.");
            if (!manifest.RootElement.TryGetProperty("Components", out var components) ||
                components.ValueKind != JsonValueKind.Array || components.GetArrayLength() == 0)
                throw new InvalidDataException("Candidate has no component inventory.");

            EvidenceDocument? evidence = null;
            string? evidenceError = null;
            if (!File.Exists(evidencePath))
                evidenceError = "Candidate-specific evidence file is missing.";
            else
            {
                try
                {
                    var data = await File.ReadAllBytesAsync(evidencePath, token);
                    if (data.Length is 0 or > MaximumEvidenceBytes) throw new InvalidDataException();
                    evidence = JsonSerializer.Deserialize<EvidenceDocument>(data, JsonOptions);
                    if (evidence is null || evidence.Checks is null || evidence.Checks.Count > 250)
                        throw new InvalidDataException();
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                { evidenceError = "Evidence document is invalid."; }
            }

            bool identityMatches = evidence is not null &&
                evidence.Revision == revision &&
                string.Equals(evidence.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase) &&
                evidence.Version == version;
            if (evidence is not null && !identityMatches) evidenceError = "Evidence does not match the exact candidate revision, version or ZIP hash.";
            var byId = new Dictionary<string, EvidenceCheck>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            if (identityMatches)
                foreach (var check in evidence!.Checks)
                {
                    if (check is null || string.IsNullOrWhiteSpace(check.Id)) continue;
                    if (!byId.TryAdd(check.Id, check)) duplicates.Add(check.Id);
                }

            var results = new List<ReleaseGateItem>();
            foreach (string id in MandatoryChecks.Concat(PublicChecks))
            {
                if (!byId.TryGetValue(id, out var value))
                {
                    results.Add(new(id, "Not run", evidenceError ?? "No candidate-specific observation provided.", null));
                    continue;
                }
                if (duplicates.Contains(id))
                {
                    results.Add(new(id, "Fail", "Duplicate evidence entries.", null));
                    continue;
                }
                string status = value.Status switch
                {
                    "Pass" or "Fail" or "Blocked" or "Not run" => value.Status,
                    _ => "Fail"
                };
                string detail = status == "Pass" ? "Verified evidence record." : "Observation is not passing.";
                if (status == "Pass" && !ValidObservation(value, revision, payloadHash, created))
                { status = "Blocked"; detail = "Missing, stale, placeholder or mismatched observation."; }
                results.Add(new(id, status, detail, SafeReference(value.EvidenceRef)));
            }

            if (installerPath is null || !File.Exists(installerPath))
            {
                for (int i = 0; i < results.Count; i++)
                    if (results[i].Id.StartsWith("installer-", StringComparison.Ordinal) && results[i].Status == "Pass")
                        results[i] = results[i] with { Status = "Blocked", Detail = "Exact installer artifact is unavailable." };
            }
            else
            {
                string installerHash;
                await using (var input = File.OpenRead(installerPath))
                    installerHash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (evidence is null || !string.Equals(installerHash, evidence.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    for (int i = 0; i < results.Count; i++)
                        if (results[i].Id.StartsWith("installer-", StringComparison.Ordinal) && results[i].Status == "Pass")
                            results[i] = results[i] with { Status = "Blocked", Detail = "Installer hash is not bound to evidence." };
            }

            bool mandatory = MandatoryChecks.All(id => results.Any(x => x.Id == id && x.Status == "Pass"));
            bool publicReady = mandatory && PublicChecks.All(id => results.Any(x => x.Id == id && x.Status == "Pass"));
            var report = new ReleaseGateReport(version, revision, target, payloadHash, true, mandatory, publicReady, results, releaseVersion);
            Directory.CreateDirectory(reportDirectory);
            string jsonPath = Path.Combine(reportDirectory, "release-readiness.json");
            string markdownPath = Path.Combine(reportDirectory, "release-readiness.md");
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), token);
            await File.WriteAllTextAsync(markdownPath, Markdown(report, publicAudience), token);
            return report;
        }
    }

    private static bool ValidObservation(EvidenceCheck check, string revision, string hash, DateTimeOffset created)
    {
        if (check.Revision != revision || !string.Equals(check.PayloadSha256, hash, StringComparison.OrdinalIgnoreCase) ||
            check.ObservedUtc is null || check.ObservedUtc < created || check.ObservedUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            !ValidLabel(check.Environment) || !ValidLabel(check.EvidenceRef))
            return false;
        return true;
    }

    private static bool ValidLabel(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 6 and <= 200 &&
        !new[] { "todo", "tbd", "placeholder", "not run", "pending", "n/a", "pass" }
            .Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? SafeReference(string? value) =>
        ValidLabel(value) ? value!.Replace("|", "_").Replace("\n", " ").Replace("\r", " ") : null;

    private static string ReadText(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Candidate identity is incomplete.");
        return value.GetString() ?? "";
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static void VerifySha256Sums(string zipPath, string hash)
    {
        string sumsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, "SHA256SUMS");
        if (!File.Exists(sumsPath)) throw new InvalidDataException("SHA256SUMS is missing.");
        string expectedLine = hash.ToLowerInvariant() + " *" + Path.GetFileName(zipPath);
        if (!File.ReadAllLines(sumsPath).Any(line => string.Equals(line.Trim(), expectedLine, StringComparison.Ordinal)))
            throw new InvalidDataException("ZIP SHA-256 does not match SHA256SUMS.");
    }

    private static string Markdown(ReleaseGateReport report, bool publicAudience)
    {
        var text = new StringBuilder();
        text.AppendLine("# PowerTrove release-readiness report")
            .AppendLine()
            .AppendLine("Audience: " + (publicAudience ? "Public" : "Controlled testing"))
            .AppendLine("Version: " + report.Version)
            .AppendLine("Release version: " + report.ReleaseVersion)
            .AppendLine("Revision: " + report.Revision)
            .AppendLine("ZIP SHA-256: " + report.PayloadSha256)
            .AppendLine("Mandatory acceptance: " + (report.MandatoryChecksPass ? "Pass" : "Blocked"))
            .AppendLine("Public ready: " + (report.PublicReady ? "Pass" : "Blocked"))
            .AppendLine()
            .AppendLine("| Check | Status | Reason |")
            .AppendLine("| --- | --- | --- |");
        foreach (var row in report.Checks)
            text.AppendLine("| " + row.Id + " | " + row.Status + " | " + row.Detail + " |");
        text.AppendLine().AppendLine("Missing, stale or mismatched evidence never counts as Pass. This report does not publish, sign or install anything.");
        return text.ToString();
    }
}
