# PowerTrove 0.3.2 Alpha 3

Updated tool packs now follow your display language for commands and options. This update also corrects startup of the separately provided MSIX test package.

## Fixed

- German display language now applies to command names, descriptions, pack names and options in updated tool packs. Restart PowerTrove after changing the language.
- Corrected missing interface resources that prevented the MSIX test package from opening its first window.

## Known limitations

- Experimental unsigned desktop alpha; Windows may display a publisher warning.
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
