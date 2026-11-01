# PowerTrove 0.3.4

Tool packs are easier to manage: Settings describes every official pack, links to its actions on the website and updates all packs at once. Running Setup again now also updates your installed packs.

## Added

- Settings > Packs shows a short description of every official pack and a link to all of its actions on powertrove.net.
- Update all installs every available pack update after one confirmation that shows the number of updates and the download size.

## Changed

- Running Setup again updates the installed packs you keep selected to their newest compatible version. Offline, your installed packs stay as they are.
- PowerTrove releases no longer carry an alpha label. Every release now has its own version number.

## Fixed

- Scrolling Settings with the mouse wheel no longer gets stuck on the pack list.

## Known limitations

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
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
