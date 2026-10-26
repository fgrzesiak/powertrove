# Action packs

PowerTrove groups built-in actions into Files, Images, PDF, Media, Tables, Office and Data packs. The desktop composes them in `src/ContextBar.Desktop/BuiltInActionPacks*.cs`. Action availability depends on the selection and installed engines; the release notes define the supported alpha workflows.

Packs reference the abstractions SDK and standalone FileKit tools. Platform and infrastructure adapters implement engine interfaces; composition assemblies connect them to pack factories. File transformations use the shared per-file or combined-file pipeline for output planning, cancellation and source preservation.

See [contributing](../CONTRIBUTING.md), [manifest metadata](../docs/references/action-pack-manifest.md) and [alpha acceptance](../docs/release/alpha.md). Stable action and pack IDs retain the `contextbar.*` prefix for compatibility. These contracts do not authorize arbitrary third-party loading.
