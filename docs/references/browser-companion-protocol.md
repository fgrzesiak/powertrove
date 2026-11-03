# Browser Companion wire protocol (v1)

This document describes the Browser Companion protocol and its initial WP2 integration. The native host supports a persistent `connectNative` session alongside the legacy one-shot `openSelection` command. The desktop listens on a dedicated same-user/same-session pipe; Chromium starts a session after explicit pairing, then returns active-tab context when PowerTrove requests it. On-demand typed page content and bounded PNG/MHTML binary capture are implemented as source; Windows and browser acceptance remains mandatory.

## Handshake and negotiation

- Native messaging uses Chromium's length-prefixed JSON framing. The session starts with a strict `hello` object: `{type:"hello",browser:"chrome"|"edge",extensionVersion:"0.1.0",minProtocol:1,maxProtocol:1,capabilities:["context.fast"]}`.
- The host replies with `ready` specifying `minProtocol`, `maxProtocol` and capability names. Both peers select the highest common protocol using `Negotiate`; no overlap means reject the session and show an update prompt in a later UI slice.
- The contract currently supports version 1. A later version may advertise N and N−1 to allow one version of skew without pretending current unsupported versions already work.
- No inbound message may exceed 960 KiB, leaving headroom below Chrome's 1 MiB native-host-to-browser limit.

## Requests and responses

- Request: `{type:"request",id:"UUID",action:"getContext",deadlineMs:350,windowTitle:"..."}`; optional typed `tab` handle `{sessionId:"UUID",windowId:42,tabId:7,documentId:"..."}`.
- Allowed actions are enumerated in both implementations. No arbitrary process commands, URLs or script execution primitives are accepted by this protocol.
- Success response: `{type:"response",id:"UUID",ok:true,result:{...}}`. Failure: `{type:"response",id:"UUID",ok:false,error:"navigated"}`.
- Error codes are strictly `restricted`, `permission`, `navigated`, `timeout`, `tooLarge`, `canceled` and `failed`.
- Cancellation: `{type:"cancel",id:"UUID"}`. Deadline is a finite request budget (1–30,000 ms). The desktop/worker apply deadlines and send best-effort cancellations; browser operations must still validate the tab before and after capture.
- Each envelope has an exact set of allowed keys. The C# parser rejects duplicate JSON fields recursively; the browser parser validates structured native-messaging objects with exact keys, as Chromium has already parsed the JSON. Operation-specific `result` object schemas must be validated by the implementation that consumes them.

## Chunk transport limits

`{type:"chunk",id:"UUID",seq:0,last:true,kind:"text",base64:"..."}`

- Each chunk decodes to **at most 512 KiB**, and must contain valid canonical Base64.
- Total limits per transfer: **text 2 MiB**, **image/PDF 24 MiB**, **MHTML 64 MiB**.
- The host's `BrowserChunkBuffer` enforces matching UUID, matching kind, monotonic sequence numbers, aggregate size and completion before exposing bytes. Incomplete or cancelled transfers produce no final result.
- Structured content limits used by typed extractors: 5,000 table rows × 200 columns, 200 links and 100 images.

No page content, selection, title, URL or chunk bytes are placed in diagnostics. Logs must contain only safe identifiers, failure categories and sizes. The new desktop session server rejects clients unless the kernel-reported client process belongs to the same Windows session and its executable path equals the installed `PowerTrove.BrowserHost.exe`; the pipe uses current-user-only protection. Browser Host separately checks the user-approved Chrome/Edge extension origin. All frames are strictly validated and framed, with at most eight connected sessions.

## Cross-language validation

Shared fixtures are generated (not typed into binary assets) by:

```
node scripts/browser/generate-protocol-fixtures.mjs
node scripts/browser/generate-protocol-fixtures.mjs --check
```

Both `tests/PowerTrove.Core.Tests/Browser/BrowserWireProtocolTests.cs` and `extensions/chromium/tests/browser-protocol.test.mjs` consume `tests/fixtures/browser-protocol/frames.json`. Run tests **locally**. The persistent native session implementation now exists in source; local Windows tests remain mandatory.

## Session behavior (WP2 source implementation)

The extension's service worker calls `chrome.runtime.connectNative('com.powertrove.browser')` after the user chooses **Connect to PowerTrove**. A `hello` message starts the session, the desktop replies `ready`, and the native host relays framed requests and responses. On browser startup the extension reconnects if pairing is enabled, with capped exponential retry; it does not monitor tabs continuously or scrape page contents while idle. Without the optional site permission, selected-text injection is disabled. Incognito and restricted pages are rejected. The one-shot `sendNativeMessage` path is retained for existing extension actions.

When PowerTrove is invoked from Chrome/Edge, it queries the current browser tab **before displaying its own palette**; window-caption matching must yield exactly one candidate. For now this produces a transient browser `TextSelection` or a URL-as-text fallback for pages without a selection. Page metadata, tables, image links, downloads and contextual ranking now have source implementations, but no claims of real-browser acceptance are made.

Local acceptance required: verify cold start/reconnect, multiple browser sessions, Chrome and Edge window titles/profiles, no host permissions, incognito, navigation races, cancellation/timeouts, and absence of untrusted content in logs. A server/API contract without such Windows tests is **not** a production-ready session.

## Binary capture transfer (source implementation)

The signed Browser Pack requests a binary operation only through the active, expiring
browser tab snapshot. The native host forwards numbered `chunk` messages and a
completion receipt with `kind` and `bytes`; desktop reassembly checks canonical
base64, sequence, total size, completion and PNG/MHTML magic bytes before sharing a
buffer. The desktop sends separate correlated `browserServiceBinaryChunk` frames
(max 128 KiB decoded) to the isolated worker, which repeats ordering/size checks and
writes using its existing staging/commit pipeline. Unfinished transfers cannot
produce a successful file receipt. The standalone extension and signed Browser Pack
are different entry points. Dialog-free desktop PDF export uses extracted Markdown
rendered by sandboxed local Edge, **not** a Chrome debugger or binary PDF capture.
