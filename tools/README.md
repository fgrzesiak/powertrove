# FileKit tools

Standalone processing libraries used by PowerTrove's action packs. Each library has its own build configuration and tests, without references to the desktop application or its packages.

| Tool | Purpose |
| --- | --- |
| [FileKit.Detection](FileKit.Detection/README.md) | File format detection from content |
| [FileKit.Imaging](FileKit.Imaging/README.md) | Image headers, dimensions and orientation |
| [FileKit.Archive](FileKit.Archive/README.md) | ZIP creation and extraction |
| [FileKit.Pdf](FileKit.Pdf/README.md) | PDF reading, writing and page operations |
| [FileKit.Tabular](FileKit.Tabular/README.md) | Table serialization |
| [FileKit.Text](FileKit.Text/README.md) | Text transformations |
| [FileKit.Xlsx](FileKit.Xlsx/README.md) | Spreadsheet output |

Architecture tests enforce the dependency boundary. See [contributing](../CONTRIBUTING.md) for repository structure and [alpha delivery](../docs/release/alpha.md) for checks on the final package.
