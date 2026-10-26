# Windows desktop host

PowerTrove uses WinUI 3 on .NET 10. Build on Windows with compatible Windows App SDK development tools. The project file records the required SDK version.

Development builds may require the matching Windows App Runtime. `scripts/run-desktop.ps1` checks prerequisites and starts the app. Self-contained release packaging is separate from development launch; do not assume a development build runs on a clean machine.

Native validation covers actual Explorer selection, supported desktop Excel ranges, no-selection fallback, keyboard use, cancellation, scaling and multiple monitors. A successful compile does not establish those results.
