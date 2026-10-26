# Action pack manifests v1 and v2

`PowerTrove.Abstractions.Manifest.ActionPackManifest.Create(pack)` now derives schema **2.0** metadata from a compiled pack. `ToJson` validates and serializes it; `Read` accepts schema **1.0** and **2.0** with bounded parsing and semantic checks. The formal schema is [action-pack-schema.json](action-pack-schema.json).

This is discovery metadata. Reading a manifest never loads an assembly, starts a process, downloads a pack, checks a signature or grants permissions. Arbitrary third-party execution is outside the current MVP. Worker loading, signed bundles and installation are separate mechanisms, outside manifest parsing.

## Factory construction

Every production project under `packs/` declares exactly one assembly-level `ActionPackFactoryAttribute`. Its public parameterless factory implements `IPackFactory` (`Info`, `Create(IPackHostServices)`). The host supplies its version, shared `FileActionServices` and explicitly registered typed capabilities. Missing optional engines produce the existing unavailable actions, rather than removing action IDs.

`PackHostServices` is an explicit type-keyed map. Register an interface under the exact interface type requested by the factory; it does not perform assembly discovery or global service lookup. Duplicate registrations fail. The host owns engine lifetimes. `ImagesPackOptions` carries saved presets and optional limits; presets remain last in registration order.

Composition assemblies declare the pack factory attribute and connect native or third-party engines to the action pack. The desktop supplies shared services. The host's recipe factory is not an attributed distributable pack assembly.

## Pack fields

| Field | Versions | Meaning |
| --- | --- | --- |
| `schemaVersion` | Required | `1.0` or `2.0`; new manifests use `2.0`. |
| `id`, `version`, `displayName`, `publisher` | Required | Stable pack identity, version and presentation. |
| `protocolVersion` | Required | Execution protocol identity; metadata alone does not establish compatibility. |
| `actions` | Required | Up to 10,000 action descriptors. IDs must be unique, ignoring case. |
| `permissions` | Optional | Declared permissions; these are requests, not authorization. |
| `platformConstraints` | Optional | `os` and `minimumArchitecture`. |
| `minHostVersion` | Required in v2 | Numeric .NET-style minimum host version, default `0.1.0` for the current SDK contract. |
| `capabilities` | Required in v2 | Host capability identifiers supplied by the manifest producer. Defaults to an empty list; optional OCR/Office availability is not incorrectly declared mandatory. These differ from an action's snapshot `requiredCapabilities`. |
| `payload` | Optional discovery metadata | Entry assembly, total uncompressed payload size and file inventory. Required for an installable bundle; absent for unbundled built-in discovery. |

`Create(pack, permissions, minHostVersion, payload, capabilities)` accepts the last three fields as explicit packaging inputs. It never invents hashes from assembly names or claims a not-yet-built payload exists. Bundle producers must supply the exact built artifacts; installers must verify the actual payload before loading it.

## Payload metadata

`payload` contains `entryAssembly` (a portable relative `.dll` path), `sizeBytes` (total uncompressed bytes), and `files`, each with `path`, `sha256` and optional `sizeBytes`. File digests are 64 hexadecimal characters. Paths reject traversal, rooted/drive paths, backslashes, Windows device names, duplicate names ignoring case, control characters and unsafe trailing dots/spaces.

The entry assembly must appear in the inventory. When every per-file size is present, their sum must equal the total; overflow and negative sizes fail. The reader validates metadata only: signatures, actual bytes, hash matches, decompressed sizes, atomic staging and host-version enforcement remain installer responsibilities. Installable bundles must provide complete per-file sizes even though discovery inventory records may omit them.

## Action metadata

V1 action fields remain readable: `id`, `title`, `inputKinds` (`none`, `files`, `table`, `text`), optional `description`, `acceptedMimeTypes`, `requiredCapabilities`, `requiresPreview` and `parameters`.

V2 adds:

- `changesFiles`: explicit Boolean; v1 leaves it unknown (`null` in the model), never silently assumes file safety.
- `selection`: required for file actions. Carries `families`, `minItems`, optional `maxItems`, `idealMinItems`, `idealMaxItems`, `allowOtherItems`, `idealNeedsEveryFamily`, `familyLimits` and `idealMinDistinctFamilies`. The last fields preserve combination roles and Office mixed-family ranking. `FromRequirements`/`ToRequirements` copy these rules without losing semantics.
- `extensions`: the exact static extension filter exposed by `IFileActionMetadata.InputExtensions`, implemented by `FileActionBase<TOptions>`. The getter returns a copy. Missing extensions means no additional extension restriction; actions still apply input-kind and selection checks. An empty array is an empty extension set. Non-file-base actions may rely on selection families alone.

Parameters retain `name`, `type`, optional `title`, `description`, `required`, `choices` and `default`. Types are `string`, `boolean`, `number`, `choice`, `password`, `file`, `color`. Password defaults are never generated and are rejected by the reader.

## Limits and compatibility

`Read` caps UTF-8 JSON at 4 MiB and depth at 32, rejects duplicate properties, unsupported schema versions, invalid input kinds, negative/inconsistent selection limits, invalid payload metadata and missing v2 safety fields. It performs no IO. Unknown fields follow the DTO serializer's additive-read behavior; the checked-in schema remains stricter (`additionalProperties: false`) for manifests we generate. Direct `JsonSerializer.Deserialize<ActionManifest>` supports the additive DTO fields but is not a substitute for `Read` validation.

Existing v1 examples continue to deserialize with absent v2 fields. Schema/protocol/host-version metadata are separate; a parseable v1 manifest does not authorize installation or execution.

Test sources cover all seven compiled-in pack factories, preset ordering, v2 schema/descriptor parity, v1 reading, payload round trips, malformed metadata, path/size checks and password-default privacy. See [alpha acceptance](../release/alpha.md) for the unexecuted checks.
