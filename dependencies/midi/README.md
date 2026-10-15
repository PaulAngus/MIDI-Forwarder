# Pinned shared MIDI dependency

These first-party packages come from the independently buildable [MIDIHelperLibrary repository](https://github.com/PaulAngus/MIDIHelperLibrary). The package and namespace names remain `MightBeUseful.Midi`. MidiVersion.props, version.txt and packages.sha256 identify the selected release. Normal builds restore from this relative feed and need no sibling checkout.

To update all consumers, increment the library version and run its Publish-SharedMidi.ps1 -ConsumerRoot command with the three checkout paths. The script updates packages, version metadata and hashes. Do not copy MIDI implementation source into the applications.

MBU_MIDI_BACKEND=auto/current/legacy controls the shared Windows policy. Run ../../Initialize-SharedMidi.ps1 -EnableWindowsServices on an eligible Windows 11 25H2+ PC to install the optional current native API for all apps. Legacy MIDI needs no optional setup. Application --midi-backend and --list-ports commands report the same provider policy.
