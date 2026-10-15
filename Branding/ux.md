# MIDI Forwarder branding UX

## Applied identity — 14 October 2026

The user assigned the existing DIN Send socket-and-arrow mark to MIDI Forwarder and requested logo/icon integration for Windows and Mac. Both Client and Server now show the mark beside the readable application title and explicitly set their native window icon. Windows executables and Avalonia tray/menu-bar icons use the same source identity; macOS app bundles include the regenerated ICNS.

## Guidance and product choices

Primary sources consulted for this integration:

- [Microsoft Windows app-icon design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design): simple understood metaphors, balanced silhouettes, small-size legibility, and contrast on light/dark backgrounds.
- [Fluent 2 iconography](https://fluent2.microsoft.design/iconography): recognisable functional symbols and detail reduction at smaller sizes.
- [Fluent 2 layout](https://fluent2.microsoft.design/layout): consistent alignment and spacing that adapts to available space.

The socket plus outgoing arrow, cobalt/navy palette and preserved mild shading are user-selected product choices. The application uses its existing Avalonia Fluent theme. A shared header provides a 36-pixel decorative image and 12-pixel gap beside the existing 24-pixel title. The logo does not accept keyboard focus or pointer interaction and uses the raw accessibility view, keeping it out of the normal control/content views. The title remains readable text and identifies the role. Existing forms, actions, keyboard order and dismissal controls are retained; no new task action or dismissal interaction was added. No further deliberate deviation from the cited layout/icon principles.

## Verification

- Windows Release build and both Windows publishes completed successfully.
- Both macOS Apple Silicon apps were rebuilt and packaged with their existing publication script.
- All 17 Windows/core tests and 11 Mac MIDI codec tests passed on the Windows host. The latter validate managed message decoding, not native CoreMIDI execution.
- Formatting verification passed for the shared header helper and changed Client/Server windows.
- Headless Avalonia/Skia renders were inspected for both roles in light/dark at their normal sizes (Client 700×620, Server 700×610) and minimum 580×500. Logo/title fit without clipping, labels and forms reflow within their existing arrangement, and action buttons stay visible. Evidence is under artifacts/branding-preview/frames. These renders are of the shared application UI on Windows; they do not validate native macOS window chrome.
- Both published Windows EXE icons were extracted with the Windows/.NET icon API and visually confirmed as DIN Send.
- ICNS PNG frames were decoded at 16/32/64/128/256/512/1024 pixels. The 128-pixel Mac image and earlier light/dark small-size asset previews match the selected mark.
- Bundle verification checks Apple Silicon binaries/native dependencies, Info.plist icon references and icon hashes against the current source. Both ICNS files inside the final tar.gz archive were independently checked against the same source hash.

Native Mac launch, Dock/Finder/menu-bar rendering and signing remain unverified on this Windows host. The prepared archive includes the existing Mac setup script and instructions. No transport or settings behavior was changed.

