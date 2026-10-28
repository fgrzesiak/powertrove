using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace PowerTrove.ReleaseTools;

public static class PackPlanBinding
{
    public static async Task<PackReleasePlan> ReadAsync(string file, CancellationToken token = default)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Pack plan is too large.");
        var plan = JsonSerializer.Deserialize<PackReleasePlan>(await File.ReadAllTextAsync(file, token), PackReleasePlanner.Json)
            ?? throw new InvalidDataException("Missing pack plan.");
        if (plan.SchemaVersion != 1 || plan.Runtime != "win-x64" || plan.SourceRevision.Length != 40 || plan.SourceRevision.Any(c => !Uri.IsHexDigit(c)) ||
            plan.Packs.Length is < 1 or > 64 || plan.Packs.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Packs.Length)
            throw new InvalidDataException("Invalid pack plan.");
        foreach (var pack in plan.Packs)
            if (!Version.TryParse(pack.Version, out _) || !Version.TryParse(pack.MinimumHostVersion, out _) ||
                pack.Fingerprint.Length != 64 || pack.Fingerprint.Any(c => !Uri.IsHexDigit(c)) ||
                !System.Text.RegularExpressions.Regex.IsMatch(pack.AssemblyName, @"\APowerTrove\.Pack\.[A-Za-z0-9]+\z"))
                throw new InvalidDataException("Invalid planned pack identity.");
        string props = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "pack-versions.props");
        if (new FileInfo(props).Length > 32768) throw new InvalidDataException("Pack version properties are too large.");
        var expected = new XElement("Project", new XElement("ItemGroup", plan.Packs.Select(p =>
            new XElement("AssemblyMetadata", new XAttribute("Include", "PowerTrove.PackVersion"), new XAttribute("Value", p.Version),
                new XAttribute("Condition", "'$(MSBuildProjectName)' == '" + p.AssemblyName + "'")))));
        if (!XNode.DeepEquals(expected, XDocument.Load(props).Root)) throw new InvalidDataException("Pack version properties differ from the frozen plan.");
        return plan;
    }
    public static async Task VerifyCandidateAsync(string zip, string planFile, CancellationToken token = default)
    {
        await ReadAsync(planFile, token);
        using var archive = ZipFile.OpenRead(zip);
        foreach (string name in new[] { "pack-plan.json", "pack-versions.props" })
        {
            var entry = archive.GetEntry("packs/" + name) ?? throw new InvalidDataException("Desktop candidate is missing its frozen pack plan.");
            if (entry.Length > 1024 * 1024) throw new InvalidDataException("Embedded pack plan is too large.");
            await using var input = entry.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output, token);
            byte[] local = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planFile))!, name), token);
            if (!output.ToArray().AsSpan().SequenceEqual(local)) throw new InvalidDataException("Pack plan changed after desktop packaging.");
        }
    }
}
