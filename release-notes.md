# PowerTrove 0.2.0 Alpha 1

In-app updates, contextual official pack downloads and coordinated desktop/pack releases.

## Added

- Check for desktop updates at startup and install a new version from the application.
- Download relevant official file packs from contextual suggestions and use their actions without restarting.
- Plan and publish independent pack versions together with a desktop release.

## Changed

- Verify signed pack catalogs and downloads, retain compatible older pack versions, and refresh open palettes after installation or removal.
- Use a release manifest to supply public versions and complete release notes.

## Fixed

- Preserve installed action descriptors when newer remote pack metadata becomes available.
- Wait for active worker requests before changing packs and prevent stale action references from restarting removed workers.

## Known limitations

- Experimental unsigned desktop alpha; Windows may display a publisher warning.
- The standard installer still includes its built-in pack profile; selecting only downloaded packs in Setup remains separate work.
- Compressing some stitched video clips can extend the video timeline; verify output duration and audio synchronization.
- Hosted checks cannot exercise optional HEIF codecs or physical audio playback when those capabilities are unavailable; verify these on a supported Windows desktop.
- Downloadable table-selection packs and complete manual Explorer/Excel, accessibility and clean-install acceptance are not included in this release proof.

## Publication status

- Automatically built experimental Windows x64 alpha. The desktop installer is unsigned; publisher identity is not verified by an Authenticode certificate.
- Official packs are signed separately and verified before activation.
- Automated tests and startup checks do not establish manual Explorer/Excel, accessibility, clean-machine or installer-lifecycle acceptance.
