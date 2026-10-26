# Official Platform References

These references informed the initial architecture. Verify the applicable release/version before implementation; pages may change.

## .NET / Windows App SDK

- [.NET official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) — .NET 10 LTS lifecycle.
- [Windows App SDK overview](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/) — recommended Windows desktop platform.
- [Windows App SDK downloads/releases](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) — verify current stable version and prerequisites.
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) — native desktop UI guidance.
- [WinUI windowing overview](https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview) — HWND/AppWindow behavior and focus.

## Hotkey and source window

- [`RegisterHotKey`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) — global hotkey behavior and errors.
- [`GetForegroundWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) — foreground HWND (may be null temporarily).

## Explorer and selection accessibility

- [`ShellFolderView.SelectedItems`](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selecteditems) — selected Shell items; must validate matching view and modern Explorer behavior.
- [UI Automation TextPattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview) — capability limitations for text selections.

## Microsoft Excel desktop interop

- [`Application.Selection`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.office.interop.excel._application.selection?view=excel-pia) — selection is dynamic and not always a `Range`.
- [`Range.Value2`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.office.interop.excel.range.value2?view=excel-pia) — date/currency represented as floating-point values.

## Third-party dependency policy

Prefer official download channels, maintained versions, and audited licenses. Do not bundle FFmpeg, ImageMagick, libvips, or other binaries without checking the exact build's applicable licensing and redistribution obligations.
