# PowerTrove 0.3.1 Alpha 1

Setup now installs your tools before it finishes, and tool pack updates recover on their own.

## Changed

- Setup downloads and verifies the selected tools before it finishes. Tools it cannot download yet, for example while offline, are installed at PowerTrove's next start.
- If an updated tool pack cannot start, PowerTrove restores its previous version and shows a notice.

## Fixed

- Setup and PowerTrove no longer interfere when both change tool packs at the same time.
- GitHub release notes no longer repeat the release title.

## Known limitations

- Experimental unsigned desktop alpha; Windows may display a publisher warning.
- A new installation needs an internet connection once to download tools. Offline, install a pack from a .cbpack file in Settings.
- Very large Excel selections can exceed the transfer limit of a downloaded pack; select a smaller range.
- Compressing some stitched video clips can extend the video timeline; verify output duration and audio synchronization.
- Hosted checks cannot exercise optional HEIF codecs or physical audio playback when those capabilities are unavailable; verify these on a supported Windows desktop.
- Manual Explorer/Excel, accessibility and clean-install acceptance are not included in this release proof.

## Publication status

- Automatically built experimental Windows x64 alpha. The desktop installer is unsigned; publisher identity is not verified by an Authenticode certificate.
- Official packs are signed separately and verified before activation.
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
