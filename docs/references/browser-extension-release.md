# Browser extension packaging and publication

The Browser Companion is a Chromium Manifest V3 extension for Chrome and Edge.
The desktop native host is part of the regular PowerTrove Windows installer.
The extension is a **separate release download** and must be paired with that installed host.

## Deterministic versioned release ZIP

For each release tag `v<version>` (including an alpha suffix, when applicable), the
Manifest release controller builds the extension from the **exact merged public source commit**,
not the private orchestration checkout, using:

```shell
node scripts/browser/build-extension.mjs --check
node --test extensions/chromium/tests/*.test.mjs
node scripts/browser/build-extension.mjs --output artifacts/browser-extension/PowerTrove-Browser-Extension-v0.4.0.zip
```

The manifest release builder attaches `PowerTrove-Browser-Extension-v<version>.zip`
(for example `PowerTrove-Browser-Extension-v0.4.0.zip`) to the **same GitHub desktop
release** as the Windows installer and desktop ZIP. An alpha release named
`v0.3.5-alpha.1` has an extension asset named
`PowerTrove-Browser-Extension-v0.3.5-alpha.1.zip`.

The builder enforces fixed ZIP timestamps and sorted entries and verifies PNG icon sizes; it rejects unapproved
permissions, remote scripts/evaluation, localization mismatch, symlinks and test
files. `SHA256SUMS` includes the extension's exact digest, and the publication
controller checks all four assets (desktop ZIP, installer EXE, extension ZIP and
`SHA256SUMS`) before completing the release. A ZIP is **not** installed automatically.

## Until the Chrome and Edge listings are available

1. Download the matching `PowerTrove-Browser-Extension-v<version>.zip` from the
   official GitHub release and compare its SHA-256 with `SHA256SUMS`.
2. **Extract the ZIP into a persistent folder.** Do not select the ZIP directly.
3. Open `chrome://extensions` or `edge://extensions`, enable **Developer mode**,
   choose **Load unpacked** and select the extracted folder containing `manifest.json`.
4. Install the matching PowerTrove desktop **installer**. A portable desktop ZIP
   does not install/register the native messaging host.
5. Open the extension popup, select **Connect to PowerTrove**, grant only the
   optional permissions needed, and follow the explicit desktop pairing prompt.

Keep the unpacked folder in place while using the extension. Browser updates and
policy restrictions may affect an unpacked extension; once the official listings
exist, prefer the reviewed store installation. No silent installation or automatic
browser permission grant occurs.

## Store submission is manual and owner-approved

1. Register publishers in Chrome Web Store and Edge Add-ons.
2. Review requested permissions and the bilingual extension privacy notice.
3. Review the checked-in canonical PowerTrove 16/32/48/128 PNG extension icons; capture actual Chrome/Edge UI screenshots from local verified builds, and keep secrets and personal
   data out of source control and screenshots.
4. Upload the reviewed store ZIP as a **draft** and test on both browsers.
5. Approve the actual stable store extension IDs for native-host allowlisting
   and make listings public only after owner review.

Do not confuse the GitHub release ZIP with approval or publication in either store.
No store credentials or upload automation are included.

## Manual acceptance required

Test Chrome and Edge independently, fresh browser profiles, incognito denial,
optional permission revocation, exact extension-ID pairing, desktop/browser restart,
host removal, multiple windows, popup keyboard and screen-reader behavior, captured
page-data integrity, and both normal and larger exports. Verify release asset
digests against downloaded bytes. Building a ZIP is not evidence of full browser
integration. The user's local-only build and test policy remains applicable.


## Chrome Web Store / Edge Add-ons review: required debugger permission

This extension now declares **required** `debugger` in
`manifest.json.permissions`. Chrome explicitly forbids `debugger` in
`optional_permissions`. Adding it is a permission increase: installed
users may have to approve the updated extension, and the Chrome Web Store
and Microsoft Edge Add-ons may require deeper review. Do **not** claim
that `debugger` is requested only when enabling a popup toggle.

**Reviewer justification:**
> PowerTrove declares Chrome's required debugger permission solely to
> support user-invoked fast full-page PNG screenshots. The feature itself
> is disabled by default and can be turned off in the extension popup.
> When enabled and invoked on an allowed tab, it attaches briefly,
> executes only `Page.getLayoutMetrics`,
> `Emulation.setDeviceMetricsOverride`, `Page.captureScreenshot` and
> `Emulation.clearDeviceMetricsOverride`, then immediately detaches, including on errors. It never uses CDP
> to read page source, cookies, authentication tokens, console logs
> or network activity. The PNG stays local as a user-requested
> browser download or an explicit handoff to the local desktop app.
> Chrome/Edge's native "started debugging" banner is shown during the
> attachment. Canceling it cancels the capture. Disabling fast mode
> uses the original scrolling screenshot without any debugger attach.

Store reviewers must see the installation/update permission prompt,
the EN/DE popup text, a real fast capture and its visible debugger
bar, user cancellation, failover and the off-by-default flow.
Review Chromium/Edge policies for the breadth of the debugger grant.
The browser's infobar cannot be hidden. Do not publish or submit
a store package without owner approval and on-device acceptance.
See https://developer.chrome.com/docs/extensions/reference/api/permissions
and https://developer.chrome.com/docs/extensions/reference/api/debugger
