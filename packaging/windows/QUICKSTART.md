# PowerTrove alpha quickstart

Use Windows 11 x64 and download only an explicitly published release. Check its version, SHA-256 and publisher signature using the accompanying release information. Candidate build artifacts are not approved public releases.

## Portable ZIP

Extract the entire ZIP to a writable local directory, then start `PowerTrove.exe` inside it. Keep the app-local runtime files and `packhost` directory together. A correctly packaged self-contained release needs no development SDK or prerequisite download. Do not run the EXE directly inside the archive.

## First action

1. Select disposable local files in File Explorer, then press **Alt+Space**.
2. Choose an action, review its options/preview and run it.
3. Review the result. File transformations create new files; originals remain unchanged by default.

Use Settings to change a conflicting shortcut. An unsupported application or selection shows an explicit unavailable state and general actions. Escape closes an idle palette; during processing it asks about cancellation. Cancellation may leave completed output files, which the result reports.

Excel export requires a supported locally installed desktop Excel and a rectangular cell selection. Media operations requiring FFmpeg remain unavailable unless a compatible external installation is configured. Read the release notes for supported workflows and known limitations; the full catalog is not proof that every operation is validated on your machine.

Core use works offline without an account. The optional Check for updates button contacts GitHub only when requested. For problems, use the issue link in the published repository and provide version, environment and reproduction steps without private filenames, selected contents or credentials.

## Installer and removal

Use only the installer published for the same release. It installs per user without requiring elevation; startup at sign-in is not enabled implicitly. Close the application before updating. Use Windows Installed apps to uninstall an installed copy. For portable use, exit the application before removing its extracted directory; local preferences are stored separately. Never delete source or output files as part of troubleshooting.
