# FileKit.Html

BCL-only .NET 10 document bounds, print options and local Chromium print protocol.
The host supplies a markup validator and a platform process launcher. No PowerTrove
reference, third-party package, browser download or cloud API is used by this tool.

`HtmlPrintDocument` bounds UTF-8 input to 5 MiB and prefixes a restrictive content
policy. A real static-markup validator is mandatory; PowerTrove supplies one from
its separate HTML adapter. `ChromiumPdfWriter` creates a fresh temporary profile,
blocks external request schemes, disables script execution before document input,
prints A4/Letter with orientation/margins, and streams at most 100 MiB of PDF. The
private loopback HTTP/WebSocket endpoint is IPC to that one local browser target;
visitor documents are never sent to a remote service. No protocol payload is logged.

The Windows launcher starts the installed Edge executable suspended, assigns its
process tree to a job, then resumes it. Limits: 512 MiB per process, 1 GiB per job,
32 processes, 60 seconds aggregate CPU time and a 60-second wall-clock operation
limit. Closing the job terminates the tree. Chromium sandboxing stays enabled.
The renderer disposes the browser and deletes its profile on success/failure/cancel.
The calling file pipeline owns atomic output publication and partial-file cleanup.

Supported document content is static HTML, inline CSS and data-embedded images and
fonts. External stylesheets, relative/local-file resources, frames, active markup,
automatic refresh, templates and inline foreign markup are unsupported. The renderer
does not fetch them to make a document appear complete. Existing installed browser
policies can still prevent startup; the host must report this without fallback to
an unrestricted renderer.

Tests use generated inputs. Native Windows acceptance verifies actual page count,
Letter landscape dimensions, CSS-colored pixels, an embedded PNG, zero requests to
a local probe server, and cancellation after browser startup with profile cleanup.

`EpubBook` adds an in-memory, bounded EPUB 2/3 archive/manifest/spine reader. It
never extracts archive paths. It refuses encrypted/obfuscated resources and
fixed layouts. `EpubCss` rewrites supported resource URLs and simple imports
through host callbacks; escapes, conditional imports and markup are unsupported.
The host document builder must embed manifest resources and enforce expansion
limits before creating a validated print document. See the implementation record
for exact resource/chapter bounds and static reflow limitations.

Email print contracts keep MIME dependencies in host adapters. Export names are
numbered, bounded portable leaf names; the host must publish a complete PDF and
attachment folder atomically. No MIME parsing or cryptographic validation is
implemented in this BCL-only tool.
