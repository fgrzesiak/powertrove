# PowerTrove

A Windows command bar that offers actions for your current selection: files in Explorer, cells in Excel, and supported text input.

Select content, press **Alt+Space**, choose an action, and review its options. File transformations create new files by default. Core processing runs locally without an account.

## Alpha availability

Download only explicitly published assets from [Releases](https://github.com/fgrzesiak/powertrove/releases). A successful build artifact is a test candidate, not an approved release. Read the release notes for the exact supported workflows and remaining limitations.

Initial target: **Windows 11 x64**. Excel actions require a supported desktop Excel installation. Media actions that need FFmpeg remain unavailable unless you configure a compatible local installation. Third-party runtime loading and a hosted marketplace are not part of this alpha promise.

## Build and test

On Windows, install the .NET 10 SDK and compatible WinUI development tools. Use PowerShell 7:

```powershell
dotnet build ContextBar.slnx -c Release
dotnet test ContextBar.slnx -c Release --no-build
```

Run the desktop project using Visual Studio or `scripts/run-desktop.ps1`. Building from source requires development prerequisites; release ZIPs are packaged separately with app-local runtimes.

See [alpha delivery](docs/release/alpha.md) for candidate builds and the separate public-release checks, and [contributing](CONTRIBUTING.md) for code structure.

## Feedback

Report reproducible problems through [Issues](https://github.com/fgrzesiak/powertrove/issues). Include the application version, Windows/Excel version where relevant, expected behavior and reproduction steps. Remove private filenames, selected content, credentials and personal paths. Do not attach sensitive files or logs.

## License

First-party code is licensed under [Apache-2.0](LICENSE). Preserve [NOTICE](NOTICE) and applicable third-party notices. The project license does not replace the licenses of bundled dependencies.
