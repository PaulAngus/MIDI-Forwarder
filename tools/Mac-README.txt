MIDI Forwarder for Apple Silicon

Requires an Apple Silicon Mac running macOS 15 (Sequoia) or later.
Both apps include the .NET runtime. Homebrew and Windows MIDI Services are
not required on the Mac.

1. Extract MIDI-Forwarder-macos-arm64.tar.gz on the Mac.
2. Open Terminal and type bash followed by a space.
   Drag Prepare-Mac-Apps.command from the extracted folder into Terminal,
   then press Return. This prepares and locally signs both apps.
3. Open MIDI Forwarder Client.app or MIDI Forwarder Server.app.
   You can move the prepared apps into Applications before configuring them.

Client: enter the Windows or Mac server's tcp:// address and matching token.
Select MIDI Forwarder for input and output in the local MIDI application.

Server: select a device with matching CoreMIDI input/output names, set the
listen address and shared token, then click Save and Start.

The menu-bar icon provides settings, stop/disconnect, and exit commands.
Start at login applies at the next login. Enable it after moving the app
to its permanent location.

Diagnostics (run from the extracted folder in Terminal):
  "./MIDI Forwarder Client.app/Contents/MacOS/MidiForwarder.Client" --list-ports
  "./MIDI Forwarder Client.app/Contents/MacOS/MidiForwarder.Client" --check-virtual-midi
  "./MIDI Forwarder Server.app/Contents/MacOS/MidiForwarder.Server" --list-ports

The virtual MIDI check verifies Program Change, MIDI clock and SysEx in
both directions and removes its temporary ports on exit.

This build uses CoreMIDI MIDI 1.0. It receives MIDI 1.0 UMP and SysEx7 from
the Windows builds. MIDI 2.0 channel messages and SysEx8 are not supported.
The TCP protocol and shared-token authentication are the same on both OSes.

Built on Windows; native UI, signing and MIDI hardware behavior must be
validated on an Apple Silicon Mac. These apps are not Developer ID signed
or notarized. The preparation script uses local ad-hoc signing and removes
download quarantine only from these two apps.
