# PowerTrove 0.4.0

PowerTrove now works with text and web pages, not only files: transform selected text, save web pages as PDF, screenshots or Markdown, and view and edit Markdown files. The app, the tool packs and powertrove.net are available in ten languages.

## Added

- Ten languages: English, German, Spanish, Brazilian Portuguese, French, Japanese, Polish, Italian, Turkish and Indonesian. Choose one in Settings > General or follow Windows.
- Text pack: select text in an app and press the PowerTrove shortcut to change case, trim or sort lines, remove duplicates, replace with a regular expression, encode and decode Base64, URL and HTML text, or escape text for JSON. The result is copied to the clipboard; you can also paste clipboard text into the palette yourself.
- Markdown pack: view and edit Markdown files with a live preview, convert them to HTML or PDF, merge several into one, and convert PDFs, Office documents and spreadsheets to Markdown.
- Extract Text from Images opens a reader for a single image: drag across the text in the image to select it like in a document, or click a line, then copy it or save it as a text file. The language is detected from the OCR languages installed in Windows.
- Browser pack (preview) with an extension for Chrome and Edge: save the page as PDF without the print dialog, as a screenshot of the visible area or the full page, as MHTML or as Markdown, export tables and links as CSV or Markdown, and copy a quote with its source link.
- Full-page screenshots of web pages show fixed headers and banners only once. An optional fast mode in the extension popup captures a long page in under a second instead of scrolling through it.
- Files saved from the extension open a Save As dialog so you choose the folder and name; the extension popup can switch to saving straight to the browser's download folder. These exports work without the desktop app.
- Settings > Browser explains how to install and connect the extension, and the palette offers "Connect browser" in Chrome and Edge until it is connected.

## Changed

- Search finds actions by their exact name even when they do not fit the current selection and shows them as unavailable with the reason. It ignores accents, punctuation and word order and tolerates small typos.
- Converting PDFs to Markdown keeps headings, lists, tables and footnotes more reliably and uses the structure of tagged PDFs.
- Numbers of files, pages and results use the correct plural forms in every language, including Polish.
- Tool packs updated with this release need this version of the app or newer; update the app first to keep receiving pack updates.

## Fixed

- In Explorer windows with several tabs, PowerTrove reads the files selected in the tab you are looking at, not only in the first tab.

## Known limitations

- The browser extension is not in the Chrome Web Store or Edge Add-ons yet. Install it from the release download in developer mode; see powertrove.net/download. Firefox is not supported.
- The browser connection needs the desktop installer; the Microsoft Store version cannot connect a browser.
- The browser extension asks for access to the page debugger. It is used only for the optional fast full-page screenshot, during which Chrome or Edge shows a debugging bar for about a second.
- Selected text is read through Windows UI Automation. Apps that do not expose their selection need the text pasted into the palette.
- The new languages have not been reviewed by native speakers yet.
- The desktop installer is not code-signed yet; Windows may display a publisher warning.
- Updating from inside the app keeps your current startup setting. To start PowerTrove when you sign in, turn it on in Settings > General.
- A new installation needs an internet connection once to download tools. Offline, install a pack from a .cbpack file in Settings.
- Very large Excel selections can exceed the transfer limit of a downloaded pack; select a smaller range.
- Compressing some stitched video clips can extend the video timeline; verify output duration and audio synchronization.
- Hosted checks cannot exercise optional HEIF codecs or physical audio playback when those capabilities are unavailable; verify these on a supported Windows desktop.
- The complete installed-pack image-editor workflow and mixed-DPI behavior still require manual desktop verification. Complex SVGs can exceed the visual preview limits; use the numeric conversion options instead.
- Manual Explorer/Excel, accessibility and clean-install acceptance are not included in this release proof.
- Microsoft Store distribution is not available yet.

## Publication status

- Automatically built Windows x64 release. The desktop installer is unsigned; publisher identity is not verified by an Authenticode certificate.
- Official packs are signed separately and verified before activation.
- The separately downloadable Browser Companion extension ZIP is included with SHA256SUMS; until Chrome/Edge store listings exist, extract it and use Developer mode > Load unpacked. Pair it explicitly with the installed desktop app.
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
