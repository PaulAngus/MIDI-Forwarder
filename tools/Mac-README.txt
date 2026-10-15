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
Apple network MIDI is the recommended Mac backend. One-time setup:
  - In Audio MIDI Setup, open MIDI Studio > Configure Network Driver.
  - Create and enable an RTP session (default RTP Session 1, UDP port 5004).
  - Add a directory entry: MIDI Forwarder, address 127.0.0.1, port 55006.
  - Set Connection policy to Directory entries; leave Live Routings as None.
    The persistent-peer checkbox is not required; the app connects itself.
  - Use Network RTP Session 1 and the matching session port in the client.
  - Select Network RTP Session 1 for BOTH input and output in your MIDI app.
The Virtual MIDI (compatibility) option retains the older paced virtual ports.

Server: select a device with matching CoreMIDI input/output names, set the
listen address and shared token, then click Save and Start.

The window Quit button and menu-bar Quit command close the app.
The menu-bar icon also provides settings and stop/disconnect commands.
Start at login applies at the next login. Enable it after moving the app
to its permanent location.

Diagnostics (run from the extracted folder in Terminal):
  "./MIDI Forwarder Client.app/Contents/MacOS/MidiForwarder.Client" --list-ports
  "./MIDI Forwarder Client.app/Contents/MacOS/MidiForwarder.Client" --check-network-midi
  "./MIDI Forwarder Client.app/Contents/MacOS/MidiForwarder.Client" --check-virtual-midi
  "./MIDI Forwarder Server.app/Contents/MacOS/MidiForwarder.Server" --list-ports

Disconnect the GUI client before running the network check, which uses the
configured Apple session (default Network RTP Session 1, port 5004). It verifies
controls, clock, 100 bulk messages each direction, and large SysEx. Override the
endpoint name with --endpoint "Network Your Session Name" if needed.
The virtual MIDI check removes its temporary ports on exit.

This build uses CoreMIDI MIDI 1.0. It receives MIDI 1.0 UMP and SysEx7 from
the Windows builds. MIDI 2.0 channel messages and SysEx8 are not supported.
The TCP protocol and shared-token authentication are the same on both OSes.

These apps are not Developer ID signed or notarized. The preparation script
uses local ad-hoc signing and removes
download quarantine only from these two apps.
