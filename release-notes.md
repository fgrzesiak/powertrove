# PowerTrove 0.3.3 Alpha 1

PowerTrove can now start when you sign in to Windows, so Alt + Space works right away. This release also adds a guided FFmpeg installation, help when your shortcut is already taken, and better suggestions for several images.

## Added

- Start at sign-in: Setup preselects "Start PowerTrove when I sign in". PowerTrove then waits in the notification area so the shortcut works right after you sign in; it reads nothing until you press the shortcut. Turn it off in Settings > General or in Windows Settings > Apps > Startup.
- Settings > FFmpeg installs FFmpeg with winget for formats such as OGG and WebM. Nothing is downloaded until you click Install FFmpeg, and the section names the package and its license first.
- If another app already uses your shortcut, PowerTrove suggests a free alternative and applies it with one click. A notice also appears when PowerToys Run, Flow Launcher or Wox may compete for Alt + Space.
- Settings > About > Copy diagnostics copies the version, number of actions and timing information for bug reports. It contains no file names or file contents.

## Changed

- Better suggestions for several images: Create PDF from Images comes first, followed by Compress, Resize and Collage. Exactly two images start with Compare. A single PDF starts with Compress and a JSON file with Format JSON.
- Suggestions for audio, video, CSV, JSON and text files no longer depend on the Windows display language.

## Fixed

- Switches in the English interface show On and Off instead of words in the Windows display language.
- With an older Images pack, Resize and Effects open their options page instead of a visual editor without a preview.
- The tool worker retries its cleanup when Windows briefly locks a file after a conversion, instead of failing.

## Known limitations

- Experimental unsigned desktop alpha; Windows may display a publisher warning.
- Updating from inside the app keeps your current startup setting. To start PowerTrove when you sign in, turn it on in Settings > General.
- A new installation needs an internet connection once to download tools. Offline, install a pack from a .cbpack file in Settings.
- Very large Excel selections can exceed the transfer limit of a downloaded pack; select a smaller range.
- Compressing some stitched video clips can extend the video timeline; verify output duration and audio synchronization.
- Hosted checks cannot exercise optional HEIF codecs or physical audio playback when those capabilities are unavailable; verify these on a supported Windows desktop.
- The complete installed-pack image-editor workflow and mixed-DPI behavior still require manual desktop verification. Complex SVGs can exceed the visual preview limits; use the numeric conversion options instead.
- Manual Explorer/Excel, accessibility and clean-install acceptance are not included in this release proof.
- Microsoft Store distribution is not available in this alpha.

## Publication status

- Automatically built experimental Windows x64 alpha. The desktop installer is unsigned; publisher identity is not verified by an Authenticode certificate.
- Official packs are signed separately and verified before activation.
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
