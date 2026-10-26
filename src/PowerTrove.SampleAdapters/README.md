# PowerTrove Sample Adapters

This project serves as an internal SDK example and a developer-facing contract testing kit for the PowerTrove ecosystem. It demonstrates how to build Context Providers and Actions that integrate with the core `PowerTrove.Core` contracts.

> **⚠️ EXPERIMENTAL VERSIONING POLICY**
>
> The plugin and adapter APIs demonstrated here are currently in an **experimental** state. We are actively refining these contracts in preparation for eventual community contributions.
>
> - **No Public API Stability:** The `IContextProvider`, `IAction`, and related contracts may introduce breaking changes without major version bumps during this alpha phase.
> - **Internal Use Only:** While these examples show how a future third-party plugin might be written, true out-of-process plugin loading is not yet supported.

## Developing Context Providers

A Context Provider (`IContextProvider`) is responsible for translating the active state of a foreground application into a typed `ContextSnapshot` that PowerTrove can understand.

Providers must:
1. Implement `CanCaptureAsync` to quickly determine if they can handle the given `SourceWindowIdentity` (e.g., by checking the process name or window handle).
2. Implement `CaptureAsync` to extract the content (text, files, tables, etc.) and return it as a structured, immutable snapshot.
3. Gracefully handle unsupported states by returning explicit `ContextStatus.Unsupported` instead of throwing exceptions.

Check out `SampleTextProvider.cs` for a basic example of capturing text from a simulated application.

## Developing Actions

An Action (`IAction`) represents an operation a user can perform on the captured context.

Actions must:
1. Expose an `ActionDescriptor` that declares what `ContentKind` they accept, what capabilities they require, and metadata for discovery (title, description, search terms).
2. Implement `MatchAsync` to perform dynamic validation on the context (e.g., "does this text actually look like JSON?").
3. Implement `PreviewAsync` to show the user what will happen before they commit to the action.
4. Implement `ExecuteAsync` to perform the work, returning an `ActionReceipt` detailing the outcome, generated files, and any failures.

Check out `CountWordsAction.cs` and `ReverseTextAction.cs` for examples of stateless and file-modifying actions.

## Contract Testing Kit

This project also includes a suite of reusable contract tests (`src/PowerTrove.Abstractions.Testing`, also used by the built-in action packs) that developers can use to ensure their own custom Actions and Providers adhere to PowerTrove's architectural invariants (e.g., proper cancellation support, handling of stale contexts, descriptor validation).

When building your own components, extending from `ActionContractTestsBase` or `ProviderContractTestsBase` will help guarantee compatibility with the core engine.
