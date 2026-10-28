# PowerTrove 0.3.0 Alpha 1

A small core app with tool packs you choose, remove and update independently.

## Added

- Choose which tools to install during setup; PowerTrove downloads the selected packs at its first start.
- Remove any tool pack in Settings and install it again later.
- Table Export (Excel selections to CSV, JSON, Markdown or SQL) and E-Invoice Tools are available as downloadable packs.
- Image conversions can limit the maximum width and height.

## Changed

- The installer and ZIP contain only the PowerTrove core; tool packs are signed downloads. The download is about half the size of 0.2.0.
- Updating from 0.2 keeps your tools: PowerTrove downloads the previously included packs once in the background and retries at the next start if it is offline.
- Saved image presets use the installed Image Tools pack.
- Tool packs can receive updates without a new app release.

## Known limitations

- Experimental unsigned desktop alpha; Windows may display a publisher warning.
- A new installation needs an internet connection once to download tools. Offline, install a pack from a .cbpack file in Settings.
- Setup does not download or verify the selected packs itself; PowerTrove does this at its first start.
- Very large Excel selections can exceed the transfer limit of a downloaded pack; select a smaller range.
- Compressing some stitched video clips can extend the video timeline; verify output duration and audio synchronization.
- Hosted checks cannot exercise optional HEIF codecs or physical audio playback when those capabilities are unavailable; verify these on a supported Windows desktop.
- Manual Explorer/Excel, accessibility and clean-install acceptance are not included in this release proof.

## Publication status

- Automatically built experimental Windows x64 alpha. The desktop installer is unsigned; publisher identity is not verified by an Authenticode certificate.
- Official packs are signed separately and verified before activation.
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
