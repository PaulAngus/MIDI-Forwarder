# Pinned shared MIDI dependency

These first-party packages come from the independently buildable [MIDIHelperLibrary repository](https://github.com/PaulAngus/MIDIHelperLibrary). The package and namespace names remain `MightBeUseful.Midi`. MidiVersion.props, version.txt and packages.sha256 identify the selected release. Normal builds restore from this relative feed and need no sibling checkout.

The selected release is 0.3.6. Client and server discovery runs on a background worker at startup and on explicit refresh. Windows refreshes the backend capabilities before enumeration; the UI applies the resulting backend description and port choices together, retaining saved selections. Concurrent refresh requests share one discovery operation, automatic connection/listening waits for initial discovery, and closing the application ignores late results. Windows still uses the shared optional API setup described below; selecting 0.3.6 does not bundle the native API into this application.

To update all consumers, increment the library version and run its Publish-SharedMidi.ps1 -ConsumerRoot command with the three checkout paths. The script updates packages, version metadata and hashes. Do not copy MIDI implementation source into the applications.

MBU_MIDI_BACKEND=auto/current/legacy controls the shared Windows policy. Run ../../Initialize-SharedMidi.ps1 -EnableWindowsServices on an eligible Windows 11 25H2+ PC to install the optional current native API for all apps. Legacy MIDI needs no optional setup. Application --midi-backend and --list-ports commands report the same provider policy.

Release 0.3.6 includes the platform-neutral RTP package. See ../../docs/shared-rtp-midi.md for preserved failure/reconnection policies. source-manifest.json identifies the local library snapshot; packages.sha256 pins the distributed bytes. Existing NuGet UI/test dependencies remain normal package dependencies.

Exact package selection: MidiVersion.props keeps the plain release value for
packaging filenames and exposes SharedMidiPackageVersion as [0.3.6]. All direct
MIDI references, including the Mac RTP dependency, use that exact range.
NuGet source mapping routes MightBeUseful.Midi and MightBeUseful.Midi.* exclusively
to this bundled feed. Other configured sources retain their existing third-party
roles. The repository-local .build/nuget-contained cache isolates builds from
unrelated global cache entries. Source-development overrides remain explicit.

Release verification: restores use exact closed ranges and exclusive bundled
MIDI source mapping. Local empty-cache probes restore 0.3.6, refuse a missing
bundle despite an alternate feed, and refuse a synthetic higher version. Active
restore graphs, tested Release outputs and isolated self-contained publication
payloads match the selected archives. Toolkit CLI/MCP relocation checks and
Forwarder managed suites passed. No native MIDI ports were opened. See
[offline validation and policy limits](../../docs/shared-rtp-midi.md).

Release 0.3.6 also owns the complete RTP diagnostic responder. MacRtpProbe calls the shared responder directly and supplies console logging. Its measurement and failure behavior remains unchanged; production reconnect policy remains separate.
