# Alpha delivery

## Manifest-managed experimental releases

Releases whose `release.json` contains `automation.driver: contextbar-manifest` are prepared by the upstream release pipeline. That pipeline creates a public snapshot PR with complete patchnotes, verifies its file inventory before and after a head-pinned automatic merge, and builds/tests/packages the exact merged public commit on hosted Windows. The public candidate workflow detects this driver and defers to it, avoiding duplicate builds. Ordinary source changes do not request a release.

The pipeline freezes independently versioned packs, checks every test-project report, the real packaged application's startup window/icon and installer compilation. It signs required official packs, checks their real workers and public downloads, then publishes the desktop ZIP/installer and SHA256SUMS as an experimental prerelease. Unchanged packs reuse their published versions; compatible older versions remain available. Desktop Authenticode is explicitly deferred, and release notes warn that the installer is unsigned. The pack key is held only by the upstream private workflow's encrypted secret and is never copied to this repository, artifacts or the website.

Automated machine evidence does not establish interactive Explorer/Excel workflows, accessibility, clean-machine operation, redistribution review or installer lifecycle acceptance. Those checks remain Not run unless actually observed. Stable publication is outside this automated policy. The existing manual candidate/evidence gate below remains available and is not replaced with fabricated pass records. Temporary outputs and pack plans live outside the checkout; `candidate.json` retains the exact public revision and frozen pack-plan hash.

## Manual candidate workflow

Every product change arrives through a pull request into `main`. Maintainers review and merge it themselves. The Alpha candidate workflow runs only when a release PR from this repository's `release/*` branch changes `release.json` on `main`. It checks the exact merged commit and rejects an already existing release tag. Ordinary public changes and all private development trigger no candidate build. It uses a GitHub-hosted Windows runner; pull-request code never executes on a personal runner. Protect `main` by requiring a pull request and disabling force pushes; do not grant automation bypass permission.

`release.json` selects the numeric package version and alpha sequence. For example, version `0.1.0` and sequence `1` identify tag `v0.1.0-alpha.1`. Increase the sequence for another published alpha of that version. The build passes this sequence into packaging: desktop and worker informational versions carry `0.1.0-alpha.1` (with the public revision as build metadata), so an installed alpha can discover later alphas and the eventual stable release. Windows assembly/file versions and installer versions remain numeric. ZIP filenames retain the numeric version; use a fresh candidate directory for every sequence. The hashed payload manifest records `ReleaseVersion`; draft publication requires it to match the selected alpha tag. Never replace an existing release/tag or reuse an accepted artifact for changed source.

## Candidate build

After merge, open **Actions → Alpha candidate** and download the `alpha-win-x64-…` artifact within its 14-day retention period. GitHub wraps every artifact in a ZIP download. Extract that outer ZIP: it contains `PowerTrove-v0.1.0-win-x64-installer.exe` for installation, the separate self-contained portable ZIP, `SHA256SUMS` for both files and `candidate.json` with the public commit. The `alpha-tests-…` artifact contains test reports, not the application. The installer is an unsigned test candidate; it installs for the current user without administrator privileges and adds a Start menu shortcut. The build does not sign, install, publish a release or establish real Explorer/Excel acceptance. Build, tests, packaging, published-app startup and installer compilation have separate visible steps and time limits. Packaging requires the application PRI and compiled window resources. Before building the installer, the workflow launches the actual staged EXE, requires a responsive startup window and embedded Windows icon, and stops only that test process. This catches startup failures but does not establish action workflows or clean-machine acceptance. The test command uses a three-minute VSTest hang timeout with memory dumps disabled. On a hang it terminates the test host and records the test sequence; failures still stop packaging. The workflow attempts to retain XML/TRX test diagnostics even after failure or cancellation as `alpha-tests-…` for seven days. Download the sequence report to locate tests that started but did not complete; a timeout does not prove its root cause. A separate ten-minute test-step limit bounds failures outside the test collector. Direct pushes without a matching merged PR fail the workflow.

For an equivalent local Windows build from a clean public checkout:

```powershell
$iscc = ./scripts/setup-inno.ps1
./scripts/build-alpha.ps1 -Iscc $iscc
```

The default command runs all phases and stops before packaging on a failed build or test. The workflow invokes `-Phase Build`, `-Phase Test`, `-Phase Package` and `-Phase Installer -Iscc <path>` in that order; individual phase switches are for staged execution and do not themselves establish that earlier phases passed. The packaging script refuses an existing output directory and installer compilation refuses an existing EXE. `setup-inno.ps1` downloads Inno Setup 6.7.3 from its official release, checks its pinned SHA-256 and valid Pyrsys B.V. Authenticode signature, then installs the compiler for the current user in `artifacts/toolchain/`. It refuses an existing compiler directory. Review the [compiler license](https://jrsoftware.org/isinfo.php) for your intended usage. Move earlier candidates to a safe location before a new local build. Never overwrite evidence for an accepted candidate.

For a local startup check of an extracted candidate (close any existing app instance first):

```powershell
./scripts/smoke-startup.ps1 -Executable C:/Releases/extracted/PowerTrove.exe
```

The check opens the app with the current user profile and stops its own instance afterward. It does not change files selected in other applications.

## Public release approval

Validate the exact final candidate on a clean Windows 11 x64 standard-user machine: offline launch, Explorer and Excel export, ZIP/PDF/image workflows, input preservation, collision handling, cancellation/partial outputs, locked files, keyboard/Narrator/high contrast, DPI/multi-monitor behavior, actual latency, runtime inventory and installer lifecycle. Record the environment and observations; missing checks remain Not run or Blocked. Obtain beta feedback and resolve dependency redistribution/notices for the exact payload.

Public alpha releases may explicitly defer codesigning. Select `-AllowUnsignedAlpha` for draft publication only after every other gate passes; the report marks the two signature checks Deferred and the release notes receive a visible unsigned warning. This option cannot be used for a stable release and does not waive owner approval, redistribution clearance or native acceptance. No signing service or certificate is needed for this alpha policy.

For signed distribution, prepare publisher signatures with an independently reviewed local signing process; the workflow creates an unsigned installer for testing. Rebuild the installer from the finalized signed payload with the selected Inno Setup compiler before signing that installer. No signing private key belongs in the repository, build artifacts or the candidate workflow. Signing changes bytes: finalize the signed ZIP/installer and hashes before gathering evidence. The existing `installer-build` tool verifies that the staging directory matches the ZIP; use that final staging directory. Signing and native acceptance are not automated or claimed complete by the workflow.

The release verifier requires evidence JSON containing `Revision`, `PayloadSha256`, `Version`, `InstallerSha256`, and `Checks`. Every check contains `Id`, `Status`, matching `Revision`/`PayloadSha256`, an actual `EvidenceRef`, `Environment` and `ObservedUtc`. Read `src/PowerTrove.ReleaseTools/ReleaseCandidateGate.cs` for the complete required check IDs. Never substitute generated pass records for observed results. Keep detailed observations private when they contain internal or personal information.

Write `release-notes.md` in the candidate directory, naming supported workflows, prerequisites and known limitations. From a public checkout matching the candidate, run:

```powershell
./scripts/publish-alpha.ps1 -CandidateDirectory C:/Releases/alpha-1 -Evidence C:/Releases/alpha-1/evidence.json -Installer C:/Releases/alpha-1/PowerTrove-installer.exe
```

For an explicitly unsigned alpha, add `-AllowUnsignedAlpha` to that command; omit it to require signatures. The command evaluates `verify-candidate --public` before any upload. Only a passing candidate can become a **draft prerelease**. The maintainer reviews the draft assets and notes and publishes it manually in GitHub. Do not infer publication readiness from an Actions success badge or checksums alone.

## Offline third-party notices

Packaging collects supplier license/copyright/notice texts from the restored packages named by the published application/worker dependency manifests and supporting Windows App SDK packages. It writes `THIRD-PARTY-NOTICES.txt` and `licenses/dependency-inventory.json` plus the verbatim texts and their hashes. The installer displays the index after installation; Settings → About can reveal the local index in Explorer without network access.

Collection is not redistribution clearance. The inventory explicitly retains missing legal texts and pending review; it does not prove that statically linked/native components have all been reconciled or that source/relinking obligations are fulfilled. Resolve these entries and audit native dependencies before approving the existing license/redistribution gates. The public release verifier still requires candidate-specific human observations.

`collect-notices --repo-root <checkout> --published-root <payload>` is available through ReleaseTools for a fresh local inspection payload. It requires the corresponding restored desktop/worker `project.assets.json` files and NuGet cache. It refuses existing notice output. Never modify accepted/signed artifacts to add notices: rebuild, sign and hash a new candidate instead.
