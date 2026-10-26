using System.CommandLine;

namespace PowerTrove.ReleaseTools;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PowerTrove Release Tools");

        var repoRootOption = new Option<string>("--repo-root", () => Environment.CurrentDirectory, "Path to the repository root directory.");
        var outputDirOption = new Option<string>("--output-dir", () => Path.Combine(Environment.CurrentDirectory, "artifacts"), "Path to the output directory where the ZIP will be placed.");
        var versionOption = new Option<string>("--version", "Target version string (e.g., 1.0.0).") { IsRequired = true };

        var alphaSequenceOption = new Option<int?>("--alpha-sequence", "Alpha sequence (1 to 999999); omit for a stable package.");

        var packageCommand = new Command("package", "Builds and packages PowerTrove into a self-contained ZIP.")
        {
            repoRootOption,
            outputDirOption,
            versionOption,
            alphaSequenceOption
        };

        packageCommand.SetHandler(async (repoRoot, outputDir, version, alphaSequence) =>
        {
            var processRunner = new RealProcessRunner();
            await PackageCommand.ExecuteAsync(repoRoot, outputDir, version, processRunner, alphaSequence);
        }, repoRootOption, outputDirOption, versionOption, alphaSequenceOption);

        rootCommand.AddCommand(packageCommand);

        var publishedRootOption = new Option<string>("--published-root", "Folder containing individually published pack compositions.") { IsRequired = true };
        var keyFileOption = new Option<string?>("--key-file", "External PEM P-256 signing private-key file (or CONTEXTBAR_PACK_SIGNING_KEY_FILE).");
        var dryRunOption = new Option<bool>("--dry-run", "Produce metadata without signatures or installable bundles.");
        var bundlesCommand = new Command("pack-bundles", "Produce signed first-party pack ZIPs and owner catalog.")
        {
            publishedRootOption, outputDirOption, keyFileOption, dryRunOption
        };
        bundlesCommand.SetHandler(async (publishedRoot, outputDir, keyFile, dryRun) =>
        {
            await PackBundleBuilder.BuildAsync(publishedRoot, outputDir, keyFile, dryRun);
        }, publishedRootOption, outputDirOption, keyFileOption, dryRunOption);
        rootCommand.AddCommand(bundlesCommand);

        var candidateZip = new Option<string>("--zip", "Exact candidate ZIP path.") { IsRequired = true };
        var candidateEvidence = new Option<string>("--evidence", "Candidate-specific evidence JSON file.") { IsRequired = true };
        var reportDirectory = new Option<string>("--report-dir", "Directory for generated JSON and Markdown reports.") { IsRequired = true };
        var installerPath = new Option<string?>("--installer", "Matching installer EXE path.");
        var publicAudience = new Option<bool>("--public", "Evaluate public distribution evidence as mandatory.");
        var verifyCommand = new Command("verify-candidate", "Fail-closed exact-candidate acceptance report.")
        {
            candidateZip, candidateEvidence, reportDirectory, installerPath, publicAudience
        };
        verifyCommand.SetHandler(async (zip, evidence, reportDir, installer, audience) =>
        {
            var report = await ReleaseCandidateGate.VerifyAsync(zip, evidence, reportDir, audience, installer);
            if (!report.MandatoryChecksPass || audience && !report.PublicReady)
                throw new InvalidOperationException("Candidate evidence is incomplete or blocked. Inspect the report.");
        }, candidateZip, candidateEvidence, reportDirectory, installerPath, publicAudience);
        rootCommand.AddCommand(verifyCommand);

        var isccPathOption = new Option<string>("--iscc", "Explicit local Inno Setup compiler executable.") { IsRequired = true };
        var installerBuildCommand = new Command("installer-build", "Build per-user installer from exact versioned ZIP payload.")
        {
            repoRootOption, outputDirOption, versionOption, isccPathOption
        };
        installerBuildCommand.SetHandler(async (repoRoot, outputDir, version, iscc) =>
        {
            await InstallerBuildCommand.ExecuteAsync(repoRoot, outputDir, version, iscc, new RealProcessRunner());
        }, repoRootOption, outputDirOption, versionOption, isccPathOption);
        rootCommand.AddCommand(installerBuildCommand);

        return await rootCommand.InvokeAsync(args);
    }
}
