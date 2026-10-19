# MIDI Forwarder

Current source release: [1.0.2](docs/releases/1.0.2.md).

Shared MIDI implementation: see [dependency and setup notes](dependencies/midi/README.md). Current and legacy backends come from the `MightBeUseful.Midi` packages maintained in [MIDIHelperLibrary](https://github.com/PaulAngus/MIDIHelperLibrary); earlier implementation/package details below describe the pre-migration builds.

For loopMIDI on older Windows, see the [setup instructions](#loopmidi-setup-on-older-windows).

Both Client and Server use the **DIN Send** socket-and-arrow logo in their app headers, window/tray icons and published Windows/macOS application icons. Branding exports and verification notes are in [Branding](Branding/README.md).

MIDI Forwarder makes a physical MIDI interface connected to one Windows PC
available to an ordinary MIDI application on another Windows PC. The local
application sees a normal named MIDI input and output; it does not need to
know that the device is remote.

The forwarder is MIDI-device and application agnostic. It transports complete
MIDI 1.0 messages, including channel messages, system common/realtime messages,
and SysEx, without interpreting manufacturer-specific bytes.

## How it works

Run the two small Windows desktop applications as a pair:

- **Server** runs beside the physical MIDI hardware. It opens a Windows MIDI
  Services endpoint or a WinMM MIDI input/output pair and listens for a client connection.
- **Client** runs beside the MIDI application. It uses a transient Windows
  MIDI Services loopback pair, or two existing WinMM virtual cables, and connects
  them to the server over TCP.

```text
MIDI application
    <->  MIDI Forwarder virtual input/output
    <->  Client
    <->  authenticated length-prefixed TCP stream
    <->  Server
    <->  physical WinMM MIDI input/output
    <->  MIDI device
```

Both directions are pumped concurrently. A connection ends when either side
closes or either MIDI pump fails. TCP uses `TCP_NODELAY` and keep-alive for
responsive control traffic. The computer-to-computer connection uses TCP.
The macOS client uses a local RTP-MIDI connection to Apple's network MIDI driver.

## Requirements

For published binaries:

- Windows 10 version 1809 (build 17763) or later, or Windows 11; x64.
- On Windows 10, the **client** needs two virtual MIDI cables provided by an
  installed driver, such as [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html).
  The **server** uses ordinary WinMM-visible hardware inputs and outputs.
- On Windows 11 25H2 (build 26200) or later, the apps prefer Windows MIDI
  Services when its API is available. The client creates its own virtual ports
  when the loopback transport is available. Earlier Windows 11 releases, missing
  services, and SDK initialization failures use WinMM. A missing loopback
  transport selects existing WinMM cables for the client while the server can
  still use MIDI Services for hardware.

The selected API is shown in the application log and by `--midi-backend`.
The Windows MIDI Services API requires Windows 11 25H2 or later according to
[Microsoft's requirements](https://microsoft.github.io/MIDI/kb/minimum-requirements/).
WinMM does not create virtual cables itself; the client does not install a driver.

For development:

- .NET 10 SDK.
- The Windows SDK reference and Windows MIDI Services packages pinned in
  `local-packages`.
- A Windows x64 build environment. Both apps compile against
  `net10.0-windows10.0.26100.0`, with `SupportedOSPlatformVersion` explicitly
  set to `10.0.17763.0`. The SDK version controls the available build-time API;
  the supported-platform version sets the Windows 10 runtime baseline.
  Newer MIDI Services calls are guarded and isolated from the Windows 10 path.

The published applications are self-contained single-file executables, so a
separate .NET runtime is not required on the machine that runs them.

## First-time setup

Set up the server first, then the client. The two sides must use the same
shared token byte-for-byte.

### Server (computer connected to the MIDI hardware)

1. Start `MidiForwarder.Server.exe`.
2. Choose the physical **MIDI endpoint** when using Windows MIDI Services,
   or the **MIDI input** that receives from the device and the **MIDI output**
   that sends to it when using WinMM. Refresh the ports after connecting a device.
3. Leave **TCP listen address** as `tcp://0.0.0.0:5180` for trusted-LAN use, or
   bind to a specific local address. The scheme must be `tcp://` and the port
   must be between 1 and 65535.
4. Enter a long, unique **Shared token**. A token is required when listening
   beyond the local machine; loopback-only listeners may use an empty token.
5. Click **Start server**.
6. Allow inbound TCP port 5180 (or your chosen port) in Windows Firewall only
   from the development computer or trusted LAN.

The server accepts one active client at a time. Additional connections are
closed while another client is relaying.

### Client (computer running the MIDI application)

#### loopMIDI setup on older Windows

Use this on supported Windows versions when the Client uses the legacy MIDI backend, including Windows 10 and earlier Windows 11 releases. The shared library selects that backend automatically when the current Windows API/loopback is unavailable. Windows MIDI Services and the optional current API installer are not required for this route.

1. On the **client PC**, download and install [loopMIDI from Tobias Erichsen](https://www.tobias-erichsen.de/software/loopmidi.html), then start it. This is the PC running the MIDI application, such as PresetMaestro; the hardware/server PC normally uses its physical USB/MIDI ports and does not need loopMIDI.
2. Create two ports named `MIDI Fwd From App` and `MIDI Fwd To App`. Each cable appears in both input and output lists. Keep the names distinct and select them exactly as shown below.
3. Keep loopMIDI running in the tray. Enable its autostart option if the MIDI apps start at sign-in. Its ports are available only while it runs, under the same Windows user account as the MIDI apps.
4. In the Client's connection settings, click **Refresh MIDI ports**. Set **From MIDI app (input)** to `MIDI Fwd From App` and **To MIDI app (output)** to `MIDI Fwd To App`.
5. In the MIDI application, choose `MIDI Fwd From App` as its **output** and `MIDI Fwd To App` as its **input**. This is the same arrangement used in PresetMaestro's loopMIDI guide.

| Application | MIDI input (receives) | MIDI output (sends) |
|---|---|---|
| MIDI application, e.g. PresetMaestro | `MIDI Fwd To App` | `MIDI Fwd From App` |
| MIDI-Forwarder Client | `MIDI Fwd From App` | `MIDI Fwd To App` |

6. Start the Server with its physical MIDI ports selected. Enter its TCP address and the same shared token in the Client, save/apply the connection settings, and connect the Client.
7. Send a known preset change from the MIDI application and check that the hardware responds. For bidirectional device queries/SysEx, also confirm that replies return to the application through `MIDI Fwd To App`. The computer-to-computer link is supplied by MIDI-Forwarder; loopMIDI only connects the programs on the client PC.

Use **two different cables** for the two directions. Selecting the same cable for both Client ports creates feedback and is rejected. Also avoid MIDI echo/thru settings that send received data straight back onto the sending cable.

**Ports missing or no response?** Create both ports before opening the MIDI apps, refresh the port list, or restart an app that only discovers ports at startup. Check that loopMIDI is running, the Windows user account and port names match, and the MIDI application's channel matches the hardware. If MIDI is routed correctly but the Client keeps reconnecting, check the Server address, shared token and firewall settings separately.

If you need to force the shared legacy backend, close the Client and run this PowerShell block from its executable folder:

```powershell
$env:MBU_MIDI_BACKEND = 'legacy'
.\MidiForwarder.Client.exe --midi-backend | Out-Host
.\MidiForwarder.Client.exe
```

The backend check should report **Legacy MIDI 1.0**. This affects programs launched from that shell and uses the same setting as PresetMaestro and Preset Sender. To return to automatic selection, run `Remove-Item Env:MBU_MIDI_BACKEND` in that shell and restart the Client.

#### Built-in ports on current Windows

On Windows 11 with Windows MIDI Services and loopback available:

1. Start `MidiForwarder.Client.exe`.
2. Enter a name for the virtual interface, such as `MIDI Forwarder`.
   The name is limited to 31 characters so the public endpoint name fits
   the WinMM name limit.
3. Enter the server address, for example
   `tcp://192.168.2.155:5180`.
4. Enter exactly the same shared token as on the server.
5. Click **Connect**.
6. In the local MIDI application, select the generated endpoint
   `MIDI Forwarder` for both MIDI input and MIDI output.

The input and output use the configured name without an `(App)` suffix. The
internal `(Relay)` endpoint does not create WinMM or WinRT MIDI 1.0 ports, so
it is absent from those applications' input/output selectors.
The client retries a dropped connection with
bounded exponential backoff (1, 2, 4, … up to 30 seconds) while keeping the
virtual endpoint alive.

## Configuration and lifecycle

Both applications start with a configuration window on first launch. After a
successful **Start server**/**Connect**, they can hide to the
notification area. Closing the window hides it; it does not stop the relay.
Use the tray menu or the window's **Stop**/**Disconnect** button to stop it.
The window **Quit** button and tray **Quit** command stop the MIDI and TCP resources before the process
exits.

The client's **Settings → Startup** page includes **Connect automatically on launch**.
It defaults to off, including for older configurations without this preference.
When off, the client loads the last saved settings and waits for **Connect**.
When enabled, it starts trying to connect using those settings whenever the app
opens, with the usual retries if the server is unavailable. The choice saves
immediately, even while connected, without changing the current connection.
Existing saved connection settings are preserved when changing this choice.
**Start with Windows** controls launching the app
at sign-in separately; enable both options to connect at sign-in.

The server has the matching **Listen automatically on launch** option in
**Settings → Startup**, also off by default, including for older configurations.
When enabled, launching the server starts listening with its last saved endpoint,
TCP address, and token. When off, those settings load and the server waits for
**Start server**. The choice saves immediately and can be changed while listening
without stopping the server or changing its saved connection settings.
Enable **Start with Windows** as well to start listening at sign-in.

Both apps also have **Start minimized to tray** in **Settings → Startup**, off by
default for new and existing configurations. This choice saves immediately and
applies on the next launch. It controls the window independently of automatic
connection/listening and **Start with Windows**. Enable the automatic connection
or listening option as well to run the relay in the background at launch.
Choose **Open Settings** from the tray icon to restore the window. First-time
setup and startup configuration/device errors still open the window.

The **Startup** page groups **Start with Windows**, automatic connection/listening,
and **Start minimized to tray** as switches that save immediately. On macOS,
**Start with Windows** is labeled **Start at login**.

Open **Settings** from the main window or **Open Settings** from the tray menu.
The separate Settings window has three fixed pages: **Connection**, **Startup**,
and **Diagnostics**. Connection changes require **Save** and can be saved without
connecting or opening MIDI hardware. Closing Settings discards connection edits
that have not been saved. Disconnect or stop the server before editing an active
connection. The main window's **Connect**/**Start server** uses the saved configuration.
Settings also has a **Connect**/**Start server** button that saves connection edits
and starts the relay. It changes to **Disconnect**/**Stop server** while running
and remains available on every settings page. The **Diagnostics** page fits the
window, with scrolling confined to the connection log.
Clicking the tray icon or choosing **Open MIDI Forwarder** restores the main window.
Tray icons show a **green check** when the client is connected or the server is
listening, an **amber clock** while the client connects or retries, and a **gray
dash** when disconnected or stopped. Hover over an icon for its current status.
The badges update even when the windows are hidden. A listening server stays
green while waiting for a client; its hover text also indicates a connected client.

Settings are JSON files under:

```text
%LOCALAPPDATA%\MIDI Forwarder\server.json
%LOCALAPPDATA%\MIDI Forwarder\client.json
```

The files contain the selected port names, TCP address, token, interface name,
and the startup preferences. Saving uses a temporary
file followed by replacement. Enabling startup writes a per-user entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; it works from a
published executable, not from `dotnet run`.

The client upgrades saved `ws://` and `wss://` addresses from older builds to
the current `tcp://` form. No WebSocket listener is present in this build.

## Virtual MIDI details

The client creates a transient Windows MIDI Services loopback association with
two endpoints when that backend is selected:

```text
<name> (Relay)  - UMP-only internal side opened by MIDI Forwarder
<name>          - public MIDI input/output (default: MIDI Forwarder)
```

Only the public side creates MIDI 1.0 input/output ports. The Relay side remains
discoverable through the Windows MIDI Services UMP API and diagnostic tools;
this is not a system-wide visibility or access restriction.

The association is removed when the client stops normally. If the process is
killed or the Windows MIDI service crashes, Windows may retain the transient
endpoint until the service or computer restarts. The client reports a startup
error when Windows MIDI Services or its loopback transport is unavailable.

The WinMM backend instead opens the two selected pre-existing virtual cables.
It closes its handles on stop and leaves ownership of those cables with their
driver/software. Some WinMM drivers allow only one application per port; close
other software using the server's hardware ports if opening them fails.
MIDI 1.0 UMP messages from a Windows 11 peer are converted to MIDI 1.0 bytes,
including multipart SysEx7, before sending to WinMM. MIDI 2.0 channel messages
and SysEx8 cannot be sent through WinMM and produce an explicit error.

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
.\MidiForwarder.Client.exe --midi-backend
.\MidiForwarder.Server.exe --midi-backend
```

`--list-ports` prints the WinMM input and output names with their indexes and
also lists MIDI Services endpoints when selected, then exits.
With MIDI Services selected, `--check-virtual-midi` creates a uniquely named short-lived test
endpoint, checks that WinMM and WinRT MIDI 1.0 each expose only one public input
and output without suffixes, and tests Program Change and multipart SysEx in
both directions. It removes the test endpoints on normal completion and exits with:

- `0` when visibility and message checks pass;
- `1` when Windows MIDI Services, endpoint creation, or message checks fail;
- `2` when the public port names or counts are incorrect;
- `3` when the MIDI service does not complete the check within 30 seconds.

For the WinMM backend, create the two cables first, close other MIDI apps, and run:

```powershell
.\MidiForwarder.Client.exe --check-virtual-midi --midi-input "MIDI Fwd From App" --midi-output "MIDI Fwd To App"
```

This opens both ends of the two cables and verifies Program Change and SysEx in
both directions. It exits with `0` on success, `1` on configuration/message failure,
or `3` on timeout. The check leaves the existing cables in place.
To force the compatibility backend on Windows 11 for driver troubleshooting,
set `$env:MIDI_FORWARDER_MIDI_API = 'winmm'` before starting either app.

## Diagnostics and logs

Both applications use compact 520-pixel Fluent windows with shared light and dark themes.
The server's main view shows the selected endpoint, client connection, and two MIDI
activity lights. Each light stays on briefly after a packet is successfully
forwarded in that direction and returns to idle when traffic stops.
Open **Settings → Diagnostics** for message/byte totals and timestamped connection logs;
the last connection's totals remain available after disconnect. Counts reset
when the server restarts or a new client successfully opens the MIDI endpoint. **Settings → Connection**
contains the endpoint selection, listen address, and masked shared token. The
**Save** button saves these settings; stop the server to edit them.
First launch and configuration errors open the Settings window automatically.

The client has the same layout, with **MIDI app → server** and **Server → MIDI app**
activity lights. Its **Connect** button starts the relay with the saved settings;
**Disconnect** also stops automatic retries. Open **Settings → Diagnostics** for connection
logs and per-connection message/byte totals. Totals remain available while
disconnected or retrying and reset on the next connection. **Settings → Connection**
retains Windows MIDI Services, legacy virtual-cable, and macOS Apple network MIDI
options, with the shared token masked. On macOS, Audio MIDI Setup remains available
while the client is waiting for its local MIDI connection.

The live configuration window shows timestamped status and connection messages.
The server reports when it is waiting for a network client and opens the selected
physical MIDI endpoint after that client authenticates. The Diagnostics log
includes a message and byte summary for each active direction once per second,
including single messages and short bursts. Pending summaries are also logged
on disconnect; the totals and activity lights are measured independently.
Server messages are also appended to:

```text
%LOCALAPPDATA%\MIDI Forwarder\server.log
```

The server records startup, unhandled exceptions, selected physical ports,
client authentication failures, connection transitions, and shutdown. The
client keeps its live log in **Diagnostics**; its connection loop reports failures,
retry delays, and traffic summaries there.

## Network protocol

The transport is deliberately small and stream-safe:

1. The client sends the ASCII magic `MIDIFWD1`, a two-byte big-endian UTF-8
   token length, and the token bytes.
2. The server validates the token using a fixed-time comparison and a five
   second authentication deadline.
3. Each MIDI packet is sent as a four-byte big-endian signed length followed
   by a one-byte format (`1` for MIDI 1.0 bytes, `2` for UMP) and the packet bytes.

Frames must be non-empty and are limited to 1 MiB (`1,048,576` bytes). The
length prefix allows arbitrary packet boundaries on TCP while preserving
message boundaries and byte order. MIDI Services peers can send UMP directly;
WinMM peers send complete MIDI 1.0 messages. The receiving backend converts the
format as needed, reassembling multipart SysEx7 before sending to WinMM.

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

On Windows, each publish automatically runs the optional local
`CopyPublishedEXEs.ps1` script when it exists. It copies the newly published
EXE directly into each destination folder configured in that script, without
creating client or server subdirectories. The script is ignored by Git so
machine-specific destinations stay local. All configured destination folders
must exist; a failed copy fails the publish command. Run
`.\CopyPublishedEXEs.ps1` manually to copy both existing published EXEs.

The projects enable compressed single-file publishing and native-library
self-extraction for `win-x64`. ReadyToRun is disabled to keep executables
smaller; initial startup can take longer while .NET compiles code. The UI
references only Avalonia's Windows backend and Skia renderer, and the WinMM
adapter references `NAudio.WinMM` instead of the full audio/WinForms package.
The executables still include the .NET runtime and Windows MIDI Services SDK.
Build output (`bin`, `obj`, and `publish`) is ignored by Git.

## Apple Silicon builds

The macOS apps use the same TCP protocol as the Windows applications and require
macOS 15 (Sequoia) or later. The client defaults to **Apple network MIDI**, using
a loopback RTP-MIDI peer to supply a native driver-owned input/output. The server
uses RtMidi's CoreMIDI backend and lists matching physical input/output names.
These builds support MIDI 1.0, including
MIDI clock and SysEx, and decode MIDI 1.0 UMP/SysEx7 from Windows peers.
MIDI 2.0 channel messages and SysEx8 are not supported by the Mac backend.

One-time Mac client setup:

1. In Audio MIDI Setup, open MIDI Studio → Configure Network Driver and create
   an **RTP** session. Enable it; the default name/port are `RTP Session 1` / `5004`.
2. Add a directory entry for `127.0.0.1`, port `55006`, named `MIDI Forwarder`.
   Set the session's **Connection policy** to **Directory entries**. Leave live
   routings as **None**. The client initiates and reconnects the local session;
   the persistent-peer checkbox is not needed.
3. In the client, choose **Apple network MIDI (recommended)**, use the matching
   endpoint (`Network RTP Session 1`) and Apple session UDP port, and save the
   TCP server address and shared token.
4. In PresetMaestro or another MIDI app, select `Network RTP Session 1` for both
   MIDI input and output. The old `MIDI Forwarder` virtual ports belong to the
   compatibility backend.

The **Virtual MIDI (compatibility)** option retains the paced virtual ports.
Apple network MIDI avoids that source's sustained-transfer restriction. See the
[macOS diagnosis and validation](docs/macos-preset-sync.md) for the measured
fourfold FM9 speed improvement and protocol checks.

Build and package both self-contained Apple Silicon apps from PowerShell:

```powershell
.\tools\Publish-Mac.ps1
# Rebuild and install only the client on a Mac:
.\tools\Publish-Mac.ps1 -ClientOnly
dotnet restore .\MIDI-Forwarder.Mac.slnx --configfile .\NuGet.Config
dotnet test .\MIDI-Forwarder.Mac.slnx -c Release --no-restore
```

The script downloads the SHA-256-pinned RtMidi native library, publishes the
`osx-arm64` applications, and creates
`publish/macos-arm64/MIDI-Forwarder-macos-arm64.tar.gz`. Extract it on a Mac
and follow its `README.txt` to prepare and locally sign both `.app` bundles.
The archive preserves executable permissions. No .NET or Homebrew installation
is needed to run the apps. On macOS the script also signs and installs the apps
in `/Applications` and `~/Applications`. Run `--check-network-midi` with the GUI
client disconnected to check controls, clock, sustained bulk SysEx and large
messages through the real Apple driver. `--check-virtual-midi` checks the
compatibility backend.

The macOS UI uses **Start at login** and stores per-user LaunchAgents under
`~/Library/LaunchAgents`. Configure this after moving the apps to their final
location. The menu-bar icon provides settings, stop and quit commands.

## Repository layout

```text
src/MidiForwarder.Core/                 framing, authentication, settings, codecs
src/MidiForwarder.Midi.WinMM/           physical WinMM ports and startup registration
src/MidiForwarder.Midi.WindowsServices/ virtual loopback and UMP/MIDI 1.0 conversion
src/MidiForwarder.Server/               server UI, listener, and relay lifecycle
src/MidiForwarder.Client/               client UI, reconnect loop, and tray lifecycle
tests/MidiForwarder.Core.Tests/         framing, codec, and bridge tests
assets/                                 application icon (.ico for Windows, .icns for macOS)
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

**The client shows WinMM instead of creating a virtual interface.** The OS or
installed MIDI Services does not provide the required API/loopback transport.
Create two virtual cables and select them as described above. Run `--midi-backend`
to see why WinMM was selected, or the WinMM virtual-cable check to test the wiring.

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

**A device works locally but not remotely.** Verify that the server's selected
input is the device-to-PC direction and its selected output is the PC-to-device
direction. Test with a simple MIDI monitor on each side, and inspect the
server/client status logs before changing the token or port assignment.

## License and notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the dependency and
license summary. The Windows MIDI Services package is a preview dependency;
its distribution terms should be checked independently before redistribution.

## Application Test Lab

Portable dashboard integration and build instructions: [Test Lab adapter](docs/test-lab.md).

## Offline Windows Test Lab profile

Build an audited bundle without MIDI access:

```text
python packaging/build_test_lab.py --rid win-x64 --config packaging/test-lab-offline-windows.json --output artifacts/offline-windows
```

Every bundle uses fresh build outputs and records its selected test-source hashes in
`offline-policy.json`. All covers only the profile's offline Functional stages.
Native and interactive Desktop requests report unavailable; no hardware or MIDI
routing qualification is implied. No native, virtual, TCP or UDP MIDI connections
are opened or created. In-memory logic fixtures remain included. The normal
development profile and its broader transport tests remain separate.
