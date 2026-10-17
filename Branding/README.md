# MIDI Forwarder branding

The existing DIN Send mark from Preset Sender was assigned to MIDI Forwarder on 14 October 2026 at the user's request. It combines a five-pin MIDI socket and an outgoing arrow. The original artwork has been preserved.

- assets/midi-forwarder.ico is the ten-resolution Windows executable and Avalonia tray icon, already referenced by Client and Server on both platform builds.
- assets/midi-forwarder.icns is the macOS bundle icon, with PNG frames at 16, 32, 64, 128, 256, 512 and 1024 pixels.
- assets/midi-forwarder-256.png is the compact logo export, embedded in all four app builds for the decorative header and native window icon.
- Website/ contains the transparent original mark, PNG sizes from 16 through 512, and favicon.ico, renamed for MIDI Forwarder. These files are prepared for website use; no hosted website was changed.
- Previous/ preserves MIDI Forwarder's prior ICO and ICNS.

Run Export-BrandAssets.ps1 on Windows to recreate all PNG/ICO exports and the ICNS from Website/midi-forwarder-logo.png. Export-MacIcon.ps1 can also recreate the ICNS alone. The full-size original was copied from Preset Sender's former DIN Send artwork; this repository now owns its exports independently. The sibling preset app uses the user-selected Amber Gradient pedal identity.

Exports use Get-LogoSourceRectangle.ps1 to center the visible mark with approximately
2% padding per edge, filling about 96% of the icon width. The original full-size
artwork remains unchanged. Both app cards display the export at 32 pixels inside
their existing 40-pixel tile.

The mark follows the simple socket/send metaphor described in [Microsoft app icon guidance](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design). The source uses mild tonal variation and is retained as requested. Both app headers use the logo beside the readable Client/Server title. Preview.png shows the transferred icon on light/dark backgrounds and at common small sizes. [UX choices and verification](ux.md) record layout, keyboard/accessibility treatment, rendered previews and platform limits.

Validation on 14 October 2026: Windows and macOS Release solutions built with zero warnings and errors. The transferred Windows ICO matches the original byte-for-byte and contains ten frames. The ICNS header, total/chunk lengths and seven embedded PNG frame signatures were checked. The mark was visually inspected on light/dark backgrounds at large size and 16, 32, 48 and 64 pixels. Native macOS rendering was not exercised on this Windows host.

The Windows Client/Server executables and the Mac Apple Silicon archive have now been rebuilt in publish/. All 28 Windows/core and Mac codec tests passed; both Windows executable icons and both ICNS files inside the final Mac archive were verified. The Mac bundle verifier also rejects stale ICNS assets whose hash differs from the current source. Native Mac launch/signing/menu-bar acceptance remains a Mac-side check.
