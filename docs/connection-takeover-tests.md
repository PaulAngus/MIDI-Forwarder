# Cross-platform connection takeover checks

The 20 October 2026 run passed all **12 scenarios** between the Windows 11
development PC and the Apple Silicon macOS 27.0.1 development VM. It used the
managed assemblies from the distributed 1.0.4 applications, verified against the
Windows EXE extraction and final signed Mac distribution hashes.

| Server | Initial client → incoming client | Confirm | Cancel | Allow takeover off |
| --- | --- | --- | --- | --- |
| Windows | Windows → Mac | Passed | Passed | Passed |
| Windows | Mac → Windows | Passed | Passed | Passed |
| Mac | Windows → Mac | Passed | Passed | Passed |
| Mac | Mac → Windows | Passed | Passed | Passed |

Each scenario verifies the initial connection and exact Program Change, MIDI
clock and 3,082-byte SysEx data in both directions. While the production takeover
dialog is open, the original client continues forwarding and retains its MIDI
session. Confirming replaces it, releases the previous server MIDI fixture before
opening the replacement, and pauses the displaced client without reconnecting.
Cancelling keeps the original connection. With takeover disabled, the incoming
client pauses with a busy error, no confirmation dialog opens, and the original
connection continues. Test MIDI resources and the VM endpoint inventory are
restored after each scenario. Existing applications and user settings are left
running and unchanged.

## Scope

These checks use real TCP connections between separate Windows and Mac machines.
The production `ClientWindow` confirmation dialog is exercised using Avalonia
Headless. Server MIDI devices and Windows client MIDI are injected in-memory
fixtures. The Mac client exercises its production CoreMIDI compatibility backend
with temporary virtual ports on the development VM. This does not qualify the
default Apple network MIDI backend, native desktop mouse interaction, physical
FM9 access, or other OS versions.

## Repeating the checks

The standalone runner is `tools/MidiForwarder.TakeoverChecks`. Build it in a fresh
output tree, providing `ClientPayload` and `ServerPayload` paths to the exact
published assemblies. For Windows, use the extraction produced by
`tools/Test-BundledWindowsMidiApi.ps1`. For Mac, use a verified extraction of the
signed app archives, set `MacChecks=true`, and copy the packaged native libraries
alongside the runner. The Mac runner must execute only on the development VM.
It acquires the same host resource lease as Test Lab.

`run_cross_platform.py` controls both runners over standard input/output, using
SSH with an existing trusted host identity for the VM. Application traffic uses
direct TCP on the selected test port; it is not tunnelled through SSH. The script
requires paths to the runner/published payloads, the build distribution record,
the VM and its staging root, and a fresh results directory. Use `--help` for the
arguments. It stops after any failed scenario or cleanup, writes worker logs and
`report.json`, and checks assembly identities against the distribution record.

The original run evidence is under
`artifacts/takeover-cross-platform-20261020/`; it is local build evidence and is
not committed. Production application binaries were not changed for this test.

## Actual desktop apps with Apple network MIDI

A separate 20 October 2026 run passed all four scenarios using the published
Windows server and clients, and the physical Apple Silicon Mac running the
published Mac client with **Apple network MIDI** enabled. Native controls were
clicked in the actual desktop apps; this run used neither Avalonia Headless nor
injected MIDI ports.

| Allow takeover | Original client | Incoming client | Result |
| --- | --- | --- | --- |
| On | Windows | Mac | Native confirmation accepted; Mac connected, Windows paused |
| On | Mac | Windows | Native confirmation accepted; Windows connected, Mac paused |
| Off | Windows | Mac | Busy message, no confirmation, Windows connection retained |
| Off | Mac | Windows | Busy message, no confirmation, Mac connection retained |

The original client remained connected while each confirmation was open. The
server setting was switched off and saved through its actual Settings window.
TCP ownership and both clients' native status/error controls were checked after
each attempt. The displaced client did not automatically reclaim the connection.

The physical Mac used its existing `Network RTP Session 1` session on port 5004.
The Windows server used the existing `PresetMaestro-Test-f5a180b19b` MIDI endpoint
and a separate TCP test port, 52181. This run qualifies actual GUI connection
takeover and the Apple network MIDI connection path; it did not send FM9 commands
or test physical-device MIDI payloads. The earlier matrix separately checked
exact MIDI payloads with fixtures.

Windows UI was operated through Computer Use. Mac UI was operated through the
approved native desktop test driver and Accessibility API. Its external managed
runtime was temporarily replaced with `tools/MidiForwarder.NativeGuiChecks`, then
restored and hash-verified. The approved native app's signature remained valid
with unchanged CDHash. Isolated application profiles preserved both machines'
saved settings. Test apps exited and host leases were released. The older
PresetSender test occupying the Mac was stopped with explicit user permission.

Local evidence, native Mac screenshots, UI observations and the result record
are in `artifacts/takeover-actual-apps-20261020/`.
