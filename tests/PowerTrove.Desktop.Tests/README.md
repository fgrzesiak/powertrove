# Managed desktop composition tests

This test project compiles the actual Full composition, context-provider registration, recipe pack and localized resource sources from [the desktop project](../../src/PowerTrove.Desktop). It references the real pack composition assemblies and platform adapters. Tests exercise the production managed logic without referencing or loading the WinUI executable.

Loading the unpackaged WinUI executable can run the Windows App SDK module initializer before a managed test method enters its body. The SDK bootstrapper can display a runtime-resolution dialog on a machine without the required runtime, which is unsuitable for an unattended testhost. The dependency-closure regression test rejects the desktop executable and Windows App SDK/WinUI dependencies in this test project.

These are composition, provider-failure and recipe-preview checks. The full solution still builds the WinUI project; the actual application, installation and native user workflows require their separate Windows acceptance. No production adapters are replaced with test-only fallbacks.
