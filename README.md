# MIDI Forwarder

MIDI Forwarder makes a physical MIDI interface connected to one Windows PC
available to an ordinary MIDI application on another Windows PC. The local
application sees a normal named MIDI input and output; it does not need to
know that the device is remote.

The forwarder is MIDI-device and application agnostic. It transports complete
MIDI 1.0 messages, including channel messages, system common/realtime messages,
and SysEx, without interpreting manufacturer-specific bytes.

## How it works

Run the two small Windows desktop applications as a pair:

- **Server** runs beside the physical MIDI hardware. It opens one WinMM MIDI
  input and one WinMM MIDI output and listens for a client connection.
- **Client** runs beside the MIDI application. It creates a transient Windows
  MIDI Services loopback pair and connects that pair to the server over TCP.

```text
MIDI application
    <->  MIDI Forwarder (App) virtual input/output
    <->  Client
    <->  authenticated length-prefixed TCP stream
    <->  Server
    <->  physical WinMM MIDI input/output
    <->  MIDI device
```

Both directions are pumped concurrently. A connection ends when either side
closes or either MIDI pump fails. TCP uses `TCP_NODELAY` and keep-alive for
responsive control traffic. This is a reliable control/SysEx bridge, not a
clock-synchronised RTP-MIDI implementation.

## Requirements

For published binaries:

- Windows x64.
- A Windows MIDI Services installation that provides the loopback transport
  for the **client**. The client uses the current Windows MIDI Services App SDK
  preview projection bundled with this repository.
- A WinMM-visible physical MIDI input and output for the **server**.

For development:

- .NET 10 SDK.
- The Windows SDK reference and Windows MIDI Services packages pinned in
  `local-packages`.
- A Windows x64 build environment. The client targets
  `net10.0-windows10.0.26100.0`; the server targets `net10.0-windows`.

The published applications are self-contained single-file executables, so a
separate .NET runtime is not required on the machine that runs them.

## First-time setup

Set up the server first, then the client. The two sides must use the same
shared token byte-for-byte.

### Server (computer connected to the MIDI hardware)

1. Start `MidiForwarder.Server.exe`.
2. Choose the physical **MIDI input** that receives data from the device and
   the physical **MIDI output** that sends data to it. Use **Refresh MIDI
   Ports** if the device was connected after the window opened.
3. Leave **TCP listen address** as `tcp://0.0.0.0:5180` for trusted-LAN use, or
   bind to a specific local address. The scheme must be `tcp://` and the port
   must be between 1 and 65535.
4. Enter a long, unique **Shared token**. A token is required when listening
   beyond the local machine; loopback-only listeners may use an empty token.
5. Click **Save and Start**.
6. Allow inbound TCP port 5180 (or your chosen port) in Windows Firewall only
   from the development computer or trusted LAN.

The server accepts one active client at a time. Additional connections are
closed while another client is relaying.

### Client (computer running the MIDI application)

1. Start `MidiForwarder.Client.exe`.
2. Enter a root name for the virtual interface, such as `MIDI Forwarder`.
   The root name is limited to 23 characters so the Windows endpoint names fit
   the WinMM name limit.
3. Enter the server address, for example
   `tcp://192.168.2.155:5180`.
4. Enter exactly the same shared token as on the server.
5. Click **Save and Connect**.
6. In the local MIDI application, select the generated endpoint
   `MIDI Forwarder (App)` for both MIDI input and MIDI output.

The companion `(Relay)` endpoint is private to MIDI Forwarder and should not
be selected by the application. The client retries a dropped connection with
bounded exponential backoff (1, 2, 4, … up to 30 seconds) while keeping the
virtual endpoint alive.

## Configuration and lifecycle

Both applications start with a configuration window on first launch. After a
successful **Save and Start**/**Save and Connect**, they can hide to the
notification area. Closing the window hides it; it does not stop the relay.
Use the tray menu or the window's **Stop**/**Disconnect** button to stop it.
The tray **Exit** command stops the MIDI and TCP resources before the process
exits.

Settings are JSON files under:

```text
%LOCALAPPDATA%\MIDI Forwarder\server.json
%LOCALAPPDATA%\MIDI Forwarder\client.json
```

The files contain the selected port names, TCP address, token, interface name,
and the **Start automatically with Windows** choice. Saving uses a temporary
file followed by replacement. Enabling startup writes a per-user entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; it works from a
published executable, not from `dotnet run`.

The client upgrades saved `ws://` and `wss://` addresses from older builds to
the current `tcp://` form. No WebSocket listener is present in this build.

## Virtual MIDI details

The client creates a transient Windows MIDI Services loopback association with
two endpoints:

```text
<root> (Relay)  - private side opened by MIDI Forwarder
<root> (App)    - side exposed to local MIDI applications
```

The association is removed when the client stops normally. If the process is
killed or the Windows MIDI service crashes, Windows may retain the transient
endpoint until the service or computer restarts. The client reports a startup
error when Windows MIDI Services or its loopback transport is unavailable.

Because the adapter uses the Windows MIDI Services preview API, this build is
intended for development and testing. Review Microsoft's distribution terms
before shipping it as a production product, and revisit the dependency when a
supported Windows MIDI API is broadly available.

## Command-line diagnostics

Run these commands from a terminal in the published application's directory:

```powershell
.\MidiForwarder.Server.exe --list-ports
.\MidiForwarder.Client.exe --list-ports
.\MidiForwarder.Client.exe --check-virtual-midi
```

`--list-ports` prints the WinMM input and output names with their indexes and
then exits. `--check-virtual-midi` creates a short-lived test endpoint named
`MIDI Fwd Check`, verifies that both sides are visible through WinMM, removes
it, and exits with:

- `0` when the virtual MIDI path is available and visible;
- `1` when Windows MIDI Services or endpoint creation fails;
- `2` when creation succeeds but the endpoint is not visible through WinMM.

## Diagnostics and logs

The live configuration window shows timestamped status and connection messages.
Server messages are also appended to:

```text
%LOCALAPPDATA%\MIDI Forwarder\server.log
```

The server records startup, unhandled exceptions, selected physical ports,
client authentication failures, connection transitions, and shutdown. The
client keeps its live log in the window; its connection loop reports failures
and retry delays there.

## Network protocol

The transport is deliberately small and stream-safe:

1. The client sends the ASCII magic `MIDIFWD1`, a two-byte big-endian UTF-8
   token length, and the token bytes.
2. The server validates the token using a fixed-time comparison and a five
   second authentication deadline.
3. Each MIDI message is sent as a four-byte big-endian signed length followed
   by the message bytes.

Frames must be non-empty and are limited to 1 MiB (`1,048,576` bytes). The
length prefix allows arbitrary packet boundaries on TCP while preserving
message boundaries and byte order. SysEx is passed as one complete MIDI 1.0
message on the wire; the client converts it to and from SysEx7 UMP packets for
the Windows MIDI Services loopback endpoint.

The stream is not encrypted. The token authenticates the peer but does not
provide confidentiality or protection against an attacker who can observe the
LAN. Keep the listener on a trusted network or behind a VPN; never expose the
TCP port directly to the public internet.

## Build, test, and publish

Restore uses only the sources configured in `NuGet.Config`, including the
repository's pinned packages:

```powershell
dotnet restore .\MIDI-Forwarder.slnx --configfile .\NuGet.Config
dotnet build .\MIDI-Forwarder.slnx --no-restore
dotnet test .\MIDI-Forwarder.slnx --no-restore
```

Publish the two self-contained Win-x64 applications with:

```powershell
dotnet publish .\src\MidiForwarder.Server\MidiForwarder.Server.csproj -c Release --no-restore
dotnet publish .\src\MidiForwarder.Client\MidiForwarder.Client.csproj -c Release --no-restore
```

The configured output locations are:

```text
publish\server\MidiForwarder.Server.exe
publish\client\MidiForwarder.Client.exe
```

The projects enable single-file publishing, native-library self-extraction,
and ReadyToRun for `win-x64`. Build output (`bin`, `obj`, and `publish`) is
ignored by Git.

## Repository layout

```text
src/MidiForwarder.Core/                 framing, authentication, settings, codecs
src/MidiForwarder.Midi.WinMM/           physical WinMM ports and startup registration
src/MidiForwarder.Midi.WindowsServices/ virtual loopback and UMP/MIDI 1.0 conversion
src/MidiForwarder.Server/               server UI, listener, and relay lifecycle
src/MidiForwarder.Client/               client UI, reconnect loop, and tray lifecycle
tests/MidiForwarder.Core.Tests/         framing, codec, and bridge tests
assets/                                 application icon
local-packages/                         pinned offline restore packages
THIRD-PARTY-NOTICES.md                  dependency license summary
```

The MIDI adapter is behind `IMidiDuplexPort`, and the network relay is behind
the core framing code. This keeps transport and device-specific code separate
from MIDI message semantics.

## Troubleshooting

**No physical ports appear.** Confirm Windows can see the device through a
WinMM MIDI application, then click **Refresh MIDI Ports** or run
`--list-ports`. The server matches the saved port name exactly (ignoring case);
duplicate device names are therefore ambiguous.

**The client says Windows MIDI Services or loopback is unavailable.** Install
or enable the Windows MIDI Services build that supplies the loopback transport
and run `--check-virtual-midi`. The client cannot fall back to loopMIDI or
manually created ports.

**The client keeps reconnecting.** Check the server address, firewall rule,
listener status, and token. The server log reports invalid-token/protocol
rejections; the client status shows the next retry delay.

**The application cannot see `(<root>) (App)`.** Ensure the client is running
and connected or connecting, choose the `(App)` endpoint rather than `(Relay)`,
and restart the MIDI application's port enumeration if it cached the old list.
If a crashed client left a stale endpoint, restart the Windows MIDI service or
Windows and run the virtual-MIDI check again.

**A device works locally but not remotely.** Verify that the server's selected
input is the device-to-PC direction and its selected output is the PC-to-device
direction. Test with a simple MIDI monitor on each side, and inspect the
server/client status logs before changing the token or port assignment.

## License and notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the dependency and
license summary. The Windows MIDI Services package is a preview dependency;
its distribution terms should be checked independently before redistribution.
