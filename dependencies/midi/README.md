# Pinned shared MIDI dependency

The exact package family is **0.3.14** (`MidiVersion.props`, `version.txt` and
`packages.sha256`). It is built from MIDIHelperLibrary and copied here as a
build feed; no sibling checkout, user-profile API installation or global runtime
is required by the published application. `source-manifest.json` records the
library source identity from clean committed source. `normal-publication.json` retains the original package identities. Other consumers retain their independently pinned versions.

The Windows provider embeds Windows.Devices.Midi2 0.99.88-preview.10 DLL/PRI and
the matching architecture's required Visual C++ runtime libraries. Eligible
Windows 11 builds automatically extract verified, content-addressed private copies
and load them using absolute paths. Windows 10 never extracts or loads this API.
Existing application-specific extraction remains supported. The explicit
`MBU_MIDI_API_DIRECTORY` diagnostic override is optional and must point at a
complete compatible API if set. Ordinary users should leave it unset.

Windows 10/11 hardware MIDI uses the legacy Windows MIDI path where the current
service is unavailable. On Windows 10, the existing public Client also opens two
manually created loopMIDI cables; see [the Client setup](../../README.md#loopmidi-cables-on-windows-10)
for cable names, directions and Preset Sender selections. Keep loopMIDI running
under the same Windows account. The Server opens its selected hardware ports
directly. No teVirtualMIDI SDK or private build is needed. Automatic virtual
interfaces require compatible Windows 11 MIDI Services and loopback transport;
embedding the API does not install or upgrade those OS components.

The macOS packages carry DryWetMIDI's universal native library and RtMidi's arm64
library. RtMidi bulk relay is used by Forwarder (Apple Silicon); Maestro and
Sender's standard CoreMIDI paths also support their existing Intel build target.
Native .NET, Avalonia, Skia and HarfBuzz assets belong inside the signed .app.
CoreMIDI and Apple network MIDI are OS components; the network backend still needs
an enabled OS RTP session. No Homebrew or runtime setup script is required.

`Initialize-SharedMidi.ps1 -PrepareBuild` is developer package preparation only.
The older `-EnableWindowsServices` per-user API installer is obsolete for these
application releases and must not be included in user installation instructions.

## Validation scope

The dependency audit separates static/published checks from clean-host and full
MIDI workflow qualification. See crossplatform-testing/docs/runtime-dependency-audit.md
for evidence and outstanding Windows/macOS/architecture checks.
