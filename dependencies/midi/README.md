# Pinned shared MIDI dependency

These first-party packages come from the independently buildable [MIDIHelperLibrary repository](https://github.com/PaulAngus/MIDIHelperLibrary). The package and namespace names remain `MightBeUseful.Midi`. MidiVersion.props, version.txt and packages.sha256 identify the selected release. Normal builds restore from this relative feed and need no sibling checkout.

The selected release is 0.3.4. Client and server discovery runs on a background worker at startup and on explicit refresh. Windows refreshes the backend capabilities before enumeration; the UI applies the resulting backend description and port choices together, retaining saved selections. Concurrent refresh requests share one discovery operation, automatic connection/listening waits for initial discovery, and closing the application ignores late results. Windows still uses the shared optional API setup described below; selecting 0.3.4 does not bundle the native API into this application.

To update all consumers, increment the library version and run its Publish-SharedMidi.ps1 -ConsumerRoot command with the three checkout paths. The script updates packages, version metadata and hashes. Do not copy MIDI implementation source into the applications.

MBU_MIDI_BACKEND=auto/current/legacy controls the shared Windows policy. Run ../../Initialize-SharedMidi.ps1 -EnableWindowsServices on an eligible Windows 11 25H2+ PC to install the optional current native API for all apps. Legacy MIDI needs no optional setup. Application --midi-backend and --list-ports commands report the same provider policy.
