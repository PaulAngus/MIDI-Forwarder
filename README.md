# MIDI Forwarder

MIDI Forwarder makes a physical MIDI interface connected to one Windows PC
available to an ordinary MIDI application on another Windows PC. The local
application sees a normal named MIDI input and output; it does not need to
know that the device is remote.

The forwarder is MIDI-device and application agnostic. It transports MIDI 1.0 and
MIDI 2.0 UMP messages without interpreting manufacturer-specific bytes. Multipart
SysEx7/8 messages are assembled before forwarding, and queued or partial traffic
is discarded across reconnects. See [transport reliability](docs/transport-reliability.md).

## How it works

Run the two small Windows desktop applications as a pair:

- **Server** runs beside the physical MIDI hardware. It keeps one physical Windows
  MIDI Services endpoint open, creates optional local app ports, and accepts up to
  eight remote app connections through one shared device writer.
- **Client** runs beside the MIDI applications. It creates a separate transient
  Windows MIDI Services loopback pair and TCP connection for each configured app port.

```text
MIDI application
    <->  MIDI Forwarder virtual input/output
    <->  Client
    <->  authenticated length-prefixed TCP stream
    <->  Server
    <->  physical Windows MIDI Services endpoint
    <->  MIDI device
```

Both directions are pumped concurrently. A connection ends when either side
closes or either MIDI pump fails. TCP uses `TCP_NODELAY` and keep-alive for
responsive control traffic. This is a reliable control/SysEx bridge, not a
clock-synchronised RTP-MIDI implementation.

## Requirements

For published binaries:

- Windows x64.
- Windows MIDI Services on both PCs, providing a loopback transport on the
  **client** and the physical endpoint on the **server**. The apps use the pinned
  App SDK preview projection bundled with this repository.

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
2. Choose the **Physical MIDI endpoint** for the device. Use **Refresh endpoints**
   if the device was connected after the window opened.
3. Leave **TCP listen address** as `tcp://0.0.0.0:5180` for trusted-LAN use, or
   bind to a specific local address. The scheme must be `tcp://` and the port
   must be between 1 and 65535.
4. Enter a long, unique **Shared token**. A token is required when listening
   beyond the local machine; loopback-only listeners may use an empty token.
5. Enter one local MIDI port name per line for apps on this computer. The defaults
   are `MIDI Gateway 1` and `MIDI Gateway 2`; leave this empty for network-only use.
6. Click **Save and Start**.
7. Allow inbound TCP port 5180 (or your chosen port) in Windows Firewall only
   from the development computer or trusted LAN.

Each local port and remote connection represents one app. Use distinct names and
select the assigned public port for both input and output in each app.

### Client (computer running the MIDI application)

1. Start `MidiForwarder.Client.exe`.
2. Enter a name for the virtual interface, such as `MIDI Forwarder`.
   The name is limited to 31 characters so the public endpoint name fits
   the WinMM name limit.
3. For additional apps, enter one distinct name per line in **Additional MIDI ports**.
   Up to eight ports can be configured per client.
4. Enter the server address, for example
   `tcp://192.168.2.155:5180`.
5. Enter exactly the same shared token as on the server.
6. Click **Save and Connect**.
7. In each MIDI application, select its assigned generated endpoint for both MIDI
   input and MIDI output. For example, use `PresetMaestro` and `FM9-Edit` as two port names.

The input and output use the configured name without an `(App)` suffix. The
internal `(Relay)` endpoint does not create WinMM or WinRT MIDI 1.0 ports, so
it is absent from those applications' input/output selectors.
The client retries a dropped connection with
bounded exponential backoff (1, 2, 4, … up to 30 seconds) while keeping the
virtual endpoint alive.

## Sharing and reserving the device

In shared mode, complete messages from gateway ports pass through one device writer.
Device replies are broadcast to the connected gateway ports. This prevents those
ports from interleaving multipart SysEx, but a generic gateway cannot match arbitrary
device requests and replies or coordinate two editors' state.

For a full sync, firmware operation or other bulk transfer:

1. Let existing transfers finish. On the server, select the app's connection under
   **Device access** and click **Reserve device** before starting the transfer.
2. Run the transfer in that app. Other local ports pause, other network connections
   close, and the reserved app alone receives device replies.
3. Click **Release device** when finished. Local ports resume immediately; remote
   ports reconnect using their normal retry delay.

Traffic sent while paused or disconnected is discarded, including queued commands
and partial SysEx. It is never replayed on release. Paused apps can report timeouts;
avoid starting work in them until access is released. A reservation remains until
released, its connection ends, or the server stops. A local app closing does not
remove its virtual port, so release a local reservation explicitly.

All participating apps must use different gateway ports. Apps connected directly
to the physical endpoint, or sharing the same virtual port, bypass this coordination.
The server reports other processes opening the physical endpoint when Windows MIDI
Services supports session reporting. This is advisory: an open endpoint does not
prove active traffic, and the gateway cannot lock out an external process.
Reservation waits for the current send call; it cannot cancel a command already
accepted by the device or determine that an arbitrary device operation has finished.

See [gateway design and UI validation](docs/gateway-ux.md).

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
<name> (Relay)  - UMP-only internal side opened by MIDI Forwarder
<name>          - public MIDI input/output (default: MIDI Forwarder)
```

Only the public side creates MIDI 1.0 input/output ports. The Relay side remains
discoverable through the Windows MIDI Services UMP API and diagnostic tools;
this is not a system-wide visibility or access restriction.

Each association is removed when its client or server stops normally. If the process is
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
then exits. `--check-virtual-midi` creates a uniquely named short-lived test
endpoint, checks that WinMM and WinRT MIDI 1.0 each expose only one public input
and output without suffixes, and tests Program Change and multipart SysEx in
both directions. It removes the test endpoints on normal completion and exits with:

- `0` when visibility and message checks pass;
- `1` when Windows MIDI Services, endpoint creation, or message checks fail;
- `2` when the public port names or counts are incorrect;
- `3` when the MIDI service does not complete the check within 30 seconds.

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

Bounded metadata logs in the same directory are `client-transport.jsonl`,
`server-transport.jsonl`, and `server-gateway.jsonl`. They record counts, timing,
queue pressure and failures without MIDI payloads or tokens. Each rotates at about
2 MiB with two backups. See [transport reliability](docs/transport-reliability.md).

## Network protocol

The transport is deliberately small and stream-safe:

1. The client sends the ASCII magic `MIDIFWD3`, a two-byte big-endian UTF-8
   token length, the token bytes, then a two-byte big-endian app-port name length
   and its UTF-8 bytes (at most 128 bytes, without control characters).
2. The server validates the token using a fixed-time comparison and a five
   second authentication deadline.
3. The server registers the app connection and acknowledges with `MIDIFWD3`.
   If another connection has reserved the device, it replies `MIDIBUSY` and closes;
   the client reports that it is waiting for access and retries.
4. Each MIDI message is sent as a four-byte big-endian signed length, one format
   byte, and message bytes. Update both applications together; old peers are rejected.

Frames must be non-empty and are limited to 1 MiB (`1,048,576` bytes). The
length prefix allows arbitrary packet boundaries on TCP while preserving
message boundaries and byte order. Multipart SysEx travels as a complete batch of
its original UMP packets, preserving group and SysEx8 stream identity. Sending uses
the endpoint's supported batch limit. Gateway writes are serialized across whole
messages; directly connected apps remain outside that coordination.

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

Developer checks render both windows in light/dark themes at normal/narrow sizes,
or exercise the actual Windows virtual ports, client/server TCP connections,
reservations, reconnects, message isolation and device replies:

```powershell
dotnet run --project .\tools\MidiForwarder.Checks -c Release
dotnet run --project .\tools\MidiForwarder.Checks -c Release -- --virtual-gateway
```

The virtual check uses uniquely named temporary ports and does not touch physical
hardware or saved settings. The rendering check writes PNGs under `artifacts/gateway-preview`.

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

The projects enable compressed single-file publishing and native-library
self-extraction for `win-x64`. ReadyToRun is disabled to keep executables
smaller; initial startup can take longer while .NET compiles code. The UI
references only Avalonia's Windows backend and Skia renderer, and the WinMM
adapter references `NAudio.WinMM` instead of the full audio/WinForms package.
The executables still include the .NET runtime and Windows MIDI Services SDK.
Build output (`bin`, `obj`, and `publish`) is ignored by Git.

## Repository layout

```text
src/MidiForwarder.Core/                 gateway, framing, buffers, diagnostics, settings
src/MidiForwarder.Midi.WinMM/           physical WinMM ports and startup registration
src/MidiForwarder.Midi.WindowsServices/ virtual loopback and UMP/MIDI 1.0 conversion
src/MidiForwarder.Server/               server UI, listener, and relay lifecycle
src/MidiForwarder.Client/               client UI, reconnect loop, and tray lifecycle
tests/MidiForwarder.Core.Tests/         framing, codecs, bridge, and gateway tests
tools/MidiForwarder.Checks/             rendered UI and native virtual gateway checks
docs/                                  transport and gateway design notes
assets/                                 application icon
local-packages/                         pinned offline restore packages
THIRD-PARTY-NOTICES.md                  dependency license summary
```

The MIDI adapter is behind `IMidiDuplexPort`, and the network relay is behind
the core framing code. This keeps transport and device-specific code separate
from MIDI message semantics.

## Troubleshooting

**No physical ports appear.** Confirm Windows can see the device through a
WinMM MIDI application, then click **Refresh endpoints** or run
`--list-ports`. The server matches the saved port name exactly (ignoring case);
duplicate device names are therefore ambiguous.

**The client says Windows MIDI Services or loopback is unavailable.** Install
or enable the Windows MIDI Services build that supplies the loopback transport
and run `--check-virtual-midi`. The client cannot fall back to loopMIDI or
manually created ports.

**The client keeps reconnecting.** Check the server address, firewall rule,
listener status, and token. The server log reports invalid-token/protocol
rejections; the client status shows the next retry delay.

**The application cannot see `MIDI Forwarder`.** Ensure the client is running
and connected or connecting, choose the configured interface name without a suffix,
and restart the MIDI application's port enumeration if it cached the old list.
If a crashed client left a stale endpoint, restart the Windows MIDI service or
Windows and run the virtual-MIDI check again.

After upgrading from a build that exposed `(App)` and `(Relay)`, exit that old
client and start the updated client, then reselect `MIDI Forwarder` for input
and output. A still-running old client will continue to publish its old names.

**The client is waiting for access.** Release the reservation on the server when
the owning app's transfer finishes. Remote ports reconnect after their retry delay.

**A device works locally but not remotely.** Verify the server's selected physical
endpoint and the app's assigned public virtual input and output. Test with a simple
MIDI monitor on each side, and inspect the
server/client status logs before changing the token or port assignment.

## License and notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the dependency and
license summary. The Windows MIDI Services package is a preview dependency;
its distribution terms should be checked independently before redistribution.
