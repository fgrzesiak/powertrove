# Changelog

All notable changes to FileKit.Imaging are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Add bounded multiple-region pixelation using one output allocation, with generated-region source-preservation tests.

- Add bounded, cancellable straight-alpha raster operations.
- Add alpha-aware median-cut palettes, optional error diffusion and streaming indexed/RGB/RGBA PNG encoding.
- Add PNG-entry ICO containers and non-identifying image metadata probing.
- Add generated-data tests for pixels, filters, metadata, malformed inputs and container offsets. These new tests were not executed during implementation.

## [0.1.0] - 2026-10-14

### Added

- Initial release, developed inside the ContextBar repository (see its ADR 0007).


