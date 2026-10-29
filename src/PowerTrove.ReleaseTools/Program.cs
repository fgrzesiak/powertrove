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

        var distributionFileOption = new Option<string?>("--pack-distribution-file", "Prepared public owner channel/trust configuration to embed in Desktop and worker.");
        var packPlanOption = new Option<string?>("--pack-plan-file", "Frozen pack release plan selected before desktop packaging.");
        var packageCommand = new Command("package", "Builds and packages PowerTrove into a self-contained ZIP.")
        {
            repoRootOption,
            outputDirOption,
            versionOption,
            alphaSequenceOption,
            distributionFileOption,
            packPlanOption
        };

        packageCommand.SetHandler(async (repoRoot, outputDir, version, alphaSequence, distributionFile, plan) =>
        {
            var processRunner = new RealProcessRunner();
            await PackageCommand.ExecuteAsync(repoRoot, outputDir, version, processRunner, alphaSequence, distributionFile, plan);
        }, repoRootOption, outputDirOption, versionOption, alphaSequenceOption, distributionFileOption, packPlanOption);

        rootCommand.AddCommand(packageCommand);

        var payloadOption = new Option<string>("--payload", "Verified self-contained Core payload directory.") { IsRequired = true };
        var identityOption = new Option<string>("--identity", "Explicit test or Partner Center package identity.") { IsRequired = true };
        var publisherOption = new Option<string>("--publisher", "Publisher distinguished name matching the signing identity.") { IsRequired = true };
        var msixVersionOption = new Option<string>("--package-version", "Four-part Store version; positive major and revision zero.") { IsRequired = true };
        var msixCommand = new Command("prepare-msix", "Prepare a Core payload's MSIX manifest and localized package resources; does not install or publish.")
        { repoRootOption, payloadOption, identityOption, publisherOption, msixVersionOption };
        msixCommand.SetHandler((repo, payload, identity, publisher, packageVersion) =>
            MsixManifest.Prepare(repo, payload, identity, publisher, packageVersion),
            repoRootOption, payloadOption, identityOption, publisherOption, msixVersionOption);
        rootCommand.AddCommand(msixCommand);

        var coreZipOption = new Option<string>("--core-zip", "Exact previously verified Core ZIP.") { IsRequired = true };
        var coreHashOption = new Option<string>("--core-sha256", "Expected SHA-256 of the Core ZIP.") { IsRequired = true };
        var sourceOption = new Option<string>("--source-revision", "Exact public source commit.") { IsRequired = true };
        var releaseVersionOption = new Option<string>("--release-version", "Semantic host release version, including alpha sequence.") { IsRequired = true };
        var inputEvidenceOption = new Option<string>("--input-evidence", "Input payload evidence JSON.") { IsRequired = true };
        var extractMsix = new Command("extract-msix-payload", "Verify and extract an existing Core ZIP without rebuilding.")
        { coreZipOption, coreHashOption, payloadOption, sourceOption, versionOption, releaseVersionOption, inputEvidenceOption };
        extractMsix.SetHandler(async (zip, hash, payload, revision, version, releaseVersion, evidenceFile) =>
        {
            var evidence = await MsixPayload.ExtractAsync(zip, payload, hash, revision, version, releaseVersion);
            await File.WriteAllTextAsync(evidenceFile, System.Text.Json.JsonSerializer.Serialize(evidence));
        }, coreZipOption, coreHashOption, payloadOption, sourceOption, versionOption, releaseVersionOption, inputEvidenceOption);
        rootCommand.AddCommand(extractMsix);
        var msixFileOption = new Option<string>("--msix", "Unsigned final MSIX to verify.") { IsRequired = true };
        var proofFileOption = new Option<string>("--proof-file", "Verified final payload inventory JSON.") { IsRequired = true };
        var verifyMsix = new Command("verify-msix-payload", "Require unchanged Core files and exact final MSIX archive contents.")
        { msixFileOption, payloadOption, inputEvidenceOption, proofFileOption };
        verifyMsix.SetHandler(async (msix, payload, inputFile, proof) =>
        {
            var evidence = System.Text.Json.JsonSerializer.Deserialize<MsixPayload.InputEvidence>(await File.ReadAllTextAsync(inputFile))
                ?? throw new InvalidDataException("Missing input payload evidence.");
            var files = await MsixPayload.VerifyAsync(msix, payload, evidence);
            await File.WriteAllTextAsync(proof, System.Text.Json.JsonSerializer.Serialize(files));
        }, msixFileOption, payloadOption, inputEvidenceOption, proofFileOption);
        rootCommand.AddCommand(verifyMsix);

        var previousCatalogOption = new Option<string?>("--previous-catalog-file", "Verified published catalog and adjacent .sig.");
        var sdkVersionOption = new Option<string>("--sdk-version", "Actual .NET SDK version used for release planning.") { IsRequired = true };
        var planCommand = new Command("pack-plan", "Plan independent versions from sources and transitive dependencies; no signing key required.")
        { repoRootOption, outputDirOption, previousCatalogOption, sdkVersionOption };
        planCommand.SetHandler(async (repo, output, previous, sdk) =>
            await PackReleasePlanner.ExecuteAsync(repo, output, previous, sdk, new RealProcessRunner()),
            repoRootOption, outputDirOption, previousCatalogOption, sdkVersionOption);
        rootCommand.AddCommand(planCommand);

        var publishedRootOption = new Option<string>("--published-root", "Folder containing individually published pack compositions.") { IsRequired = true };
        var keyFileOption = new Option<string?>("--key-file", "External PEM P-256 signing private-key file (or POWERTROVE_PACK_SIGNING_KEY_FILE).");
        var dryRunOption = new Option<bool>("--dry-run", "Produce metadata without signatures or installable bundles.");
        var minimumHostOption = new Option<string>("--min-host-version", () => "0.1.0", "Oldest host compatible with these pack binaries and required services.");
        var bundlesCommand = new Command("pack-bundles", "Produce signed first-party pack ZIPs and owner catalog.")
        {
            publishedRootOption, outputDirOption, keyFileOption, dryRunOption, minimumHostOption, packPlanOption
        };
        bundlesCommand.SetHandler(async (publishedRoot, outputDir, keyFile, dryRun, minimumHost, plan) =>
        {
            if (!Version.TryParse(minimumHost, out var minimum)) throw new ArgumentException("Invalid minimum host version.");
            await PackBundleBuilder.BuildAsync(publishedRoot, outputDir, keyFile, dryRun, minimumHostVersion: minimum, releasePlanFile: plan);
        }, publishedRootOption, outputDirOption, keyFileOption, dryRunOption, minimumHostOption, packPlanOption);
        rootCommand.AddCommand(bundlesCommand);

        var packRootOption = new Option<string>("--pack-root", "Signed packs directory produced by pack-bundles.") { IsRequired = true };
        var previousPackRootOption = new Option<string?>("--previous-pack-root", "Previous signed pack directory or GitHub asset export; retain supported older releases.");
        var githubAssetsCommand = new Command("pack-github-assets", "Prepare flat GitHub release assets and a signed multi-version catalog; never uploads.")
        { packRootOption, previousPackRootOption, outputDirOption, keyFileOption };
        githubAssetsCommand.SetHandler(async (packs, previous, output, key) =>
            await PackGitHubAssetsCommand.ExecuteAsync(packs, output, key, previous),
            packRootOption, previousPackRootOption, outputDirOption, keyFileOption);
        rootCommand.AddCommand(githubAssetsCommand);

        var privateKeyOutput = new Option<string>("--private-key-file", "New private key path outside all repositories and web hosting.") { IsRequired = true };
        var publicKeyOutput = new Option<string>("--public-key-file", "New public PEM key file.") { IsRequired = true };
        var keyGenerationCommand = new Command("pack-keygen", "Explicitly generate an offline owner pack-signing key pair; never prints key contents.")
        { privateKeyOutput, publicKeyOutput };
        keyGenerationCommand.SetHandler(async (privatePath, publicPath) => await PackKeyCommand.GenerateAsync(privatePath, publicPath), privateKeyOutput, publicKeyOutput);
        rootCommand.AddCommand(keyGenerationCommand);

        var exportPublicKeyCommand = new Command("pack-export-public", "Export only the public P-256 verification key for local release preparation.")
        { keyFileOption, publicKeyOutput };
        exportPublicKeyCommand.SetHandler(async (key, output) => await PackKeyCommand.ExportPublicAsync(key!, output), keyFileOption, publicKeyOutput);
        rootCommand.AddCommand(exportPublicKeyCommand);

        var publicKeyOption = new Option<string>("--public-key-file", "Owner P-256 public PEM key; private keys are refused.") { IsRequired = true };
        var channelOption = new Option<string>("--channel", "Official GitHub packs-v1 release directory or owner HTTPS pack directory.") { IsRequired = true };
        var configFileOption = new Option<string>("--config-file", "New public distribution configuration file for release embedding.") { IsRequired = true };
        var distributionCommand = new Command("pack-configuration", "Prepare the public trust/channel resource for a release build.")
        { publicKeyOption, channelOption, configFileOption };
        distributionCommand.SetHandler(async (key, channel, path) => await PackDistributionCommand.ExecuteAsync(key, channel, path),
            publicKeyOption, channelOption, configFileOption);
        rootCommand.AddCommand(distributionCommand);

        var noticesCommand = new Command("collect-notices", "Collect supplier legal texts for an existing published payload; does not grant redistribution clearance.")
        {
            repoRootOption, publishedRootOption
        };
        noticesCommand.SetHandler(async (repoRoot, publishedRoot) =>
        {
            await ThirdPartyNotices.BuildAsync(repoRoot, publishedRoot);
        }, repoRootOption, publishedRootOption);
        rootCommand.AddCommand(noticesCommand);

        var compositionProjectOption = new Option<string>("--composition-project", "Exact published pack composition project.") { IsRequired = true };
        var packNoticesCommand = new Command("collect-pack-notices", "Collect supplier texts for one published pack before signing.")
        { repoRootOption, publishedRootOption, compositionProjectOption };
        packNoticesCommand.SetHandler(async (repo, published, project) => await ThirdPartyNotices.BuildPackAsync(repo, published, project),
            repoRootOption, publishedRootOption, compositionProjectOption);
        rootCommand.AddCommand(packNoticesCommand);

        var candidateZip = new Option<string>("--zip", "Exact candidate ZIP path.") { IsRequired = true };
        var verifyPlanCommand = new Command("verify-pack-plan", "Require the selected pack plan to match the exact desktop ZIP.")
        { candidateZip, packPlanOption };
        verifyPlanCommand.SetHandler(async (zip, plan) => await PackPlanBinding.VerifyCandidateAsync(zip,
            plan ?? throw new ArgumentException("--pack-plan-file is required.")), candidateZip, packPlanOption);
        rootCommand.AddCommand(verifyPlanCommand);
        var candidateEvidence = new Option<string>("--evidence", "Candidate-specific evidence JSON file.") { IsRequired = true };
        var reportDirectory = new Option<string>("--report-dir", "Directory for generated JSON and Markdown reports.") { IsRequired = true };
        var installerPath = new Option<string?>("--installer", "Matching installer EXE path.");
        var publicAudience = new Option<bool>("--public", "Evaluate public distribution evidence as mandatory.");
        var unsignedAlpha = new Option<bool>("--allow-unsigned-alpha", "Explicitly defer publisher/installer signatures for an alpha prerelease; all other evidence remains required.");
        var verifyCommand = new Command("verify-candidate", "Fail-closed exact-candidate acceptance report.")
        {
            candidateZip, candidateEvidence, reportDirectory, installerPath, publicAudience, unsignedAlpha
        };
        verifyCommand.SetHandler(async (zip, evidence, reportDir, installer, audience, unsigned) =>
        {
            var report = await ReleaseCandidateGate.VerifyAsync(zip, evidence, reportDir, audience, installer, allowUnsignedAlpha: unsigned);
            if (!report.MandatoryChecksPass || audience && !report.PublicReady)
                throw new InvalidOperationException("Candidate evidence is incomplete or blocked. Inspect the report.");
        }, candidateZip, candidateEvidence, reportDirectory, installerPath, publicAudience, unsignedAlpha);
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
