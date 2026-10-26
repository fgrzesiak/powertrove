# Contributing

Please open a focused issue or pull request against `main`. Describe the user-visible problem, the change and the checks performed. Submit only material you have the right to contribute under the project's Apache-2.0 license and preserve third-party attribution.

## Structure

- `src/PowerTrove.Abstractions`: typed snapshots, action contracts and shared file pipelines.
- `src/PowerTrove.Core`: matching, ranking and other pure logic.
- `src/PowerTrove.Application`: lifecycle and view models.
- `src/PowerTrove.Platform.Windows`: Windows integration adapters.
- `src/PowerTrove.Desktop`: WinUI host and composition.
- `packs/`: actions, depending only on the abstractions and standalone tools.
- `tools/FileKit.*`: independent processing libraries, with no PowerTrove references.
- `tests/`: automated checks, including enforced architecture boundaries.

Keep processing offline, selection capture explicit and file outputs non-destructive. Never log selected text, spreadsheet contents, credentials or private paths. Keep UI text in English/German resource files. Add meaningful tests for behavior changes and retain cancellation and clear errors.

Build and test on Windows as described in README. Test source files are not evidence that Explorer, Office COM or WinUI interactions work; report real Windows observations separately. Do not add third-party execution, background content capture, or network requirements for core features.

Public pull requests do not run on a maintainer's personal computer. Merged code is built in a GitHub-hosted Windows environment; publication remains a separate approval.
