# Shared RTP-MIDI 0.3.6

`MightBeUseful.Midi.Rtp` targets net10.0 and depends only on MightBeUseful.Midi.
It contains no CoreMIDI, Windows, Fractal protocol, device authorization or native
discovery calls. Both consumers carry pinned package bytes; builds never read a
sibling repository. Shared codec source and its attribution ship inside the RTP archive.

| Policy | Explicit initiator (toolkit) | Loopback stream peer (Forwarder) |
| --- | --- | --- |
| Address | Explicit unicast IPv4/control port, data at port + 1 | Loopback only; driver ports explicitly selected |
| Negotiation | 12-second deadline; six invitation attempts per socket | Initiates/retries or accepts driver invitations |
| Ready | One matched clock reply | Six clock exchanges |
| Heartbeat | 15 seconds; incomplete SysEx expires after 10 seconds | 75 seconds; unchanged stream lifetime |
| Sequence | Loss, duplicate or reorder permanently faults | Duplicate/late ignored; forward loss closes current session |
| Receive frames | 4096 bytes; strict boundaries and cancellation failure | 1 MiB; cancellation discards partial frame |
| Timing | Timestamp/deltas honored; future scheduling limited to 2 seconds | Immediate delivery, existing relay behavior |
| Queues/sends | Serialized, 5-second send deadline, 1 ms segment pacing | 256 messages / 4 MiB queue; fairness after 32 segments |
| Recovery | No reconnect/replay; caller must reconcile uncertain writes | New session, sequence, queues and decoder; no MIDI replay |

The two production session classes deliberately retain these different lifecycles. They use
one shared codec with explicit Strict/Stream receive policies. Strict sends retain
the prior single-frame CC/PC/SysEx restrictions, marker and two-byte command length;
stream sends retain running status, realtime and cancellation encoding. Neither
receiver reconstructs messages from recovery journals.

The toolkit's RtpMidiTransport retains MidiSendAuthority, typed reads/edits/uploads
and exclusive endpoint leases. Its CLI/MCP gains no arbitrary MIDI API. Forwarder's
CoreMidiNetworkPort retains native endpoint validation and its public API; routing
and bridge reconnection remain application responsibilities. The diagnostic probe
now uses `RtpMidiDiagnosticPeer` directly: no application-owned RTP session code.
This separate diagnostic policy accepts loopback invitations, preserves the
`Loopback Test` reply and six count-2 clock exchanges, reports packet/gap/partial
SysEx counters, and sends control-socket feedback with the sequence in the high
16 bits. Its unbounded diagnostic channel and immediate stream decoding remain.
Loss, duplication or reordering faults reception; it has no automatic invitation,
heartbeat recovery or replay. Peer BY is logged only, as before. Disposal sends
BY and releases the sockets. The default ports remain 55006/55007; an explicit
loopback pair can be supplied for synthetic tests. Logging is supplied by the caller.

Tests use synthetic UDP loopback peers and fake ports only. They cover sequence
wrap/loss/duplicates, SysEx limits/cancellation, timestamps, reconnect invitations,
fresh session state, all-model authorization and offline package relocation.
No native MIDI enumeration, live device connection, macOS runtime validation or
new hardware compatibility claim is involved.

## Offline validation

The shared 0.3.6 suite has 66 passing tests, including synthetic responder
negotiation, clock readiness, sequence wrap/loss/duplication/reorder, feedback,
large SysEx, cancellation, counters and socket cleanup. CC/PC vectors cover
channels 1-16 and raw data boundaries. Consumer validation is recorded with
each repository. No native MIDI ports or external network peers are used.
Native macOS execution and live peer/device interoperability remain untested.

Consumer verification on Windows (2026-10-18): Forwarder core/client/server
111 tests passed; managed Mac tests 28 passed and three native tests skipped.
MacRtpProbe compiled from both the normal cache and an empty cache. Self-contained
Windows x64 and Mac arm64 client/server publications passed, with verification
output isolated and deployment disabled. Apphost architecture, runtime inclusion
and native package payloads were inspected without executing them.

Toolkit verification: 167 Python tests, cold-cache Windows/portable builds,
all-model CLI checks and a relocated MCP bundle with real stdio/helper binaries
passed for FM9, FM3 and Axe-Fx III (512/1024). Typed CC/PC vectors and invalid input
checks run for every definition; authorization, uncertain-bank no-retry and
preservation checks remain active. Package archives and source provenance match;
exact-pin/source-mapping negative probes refused missing bundles and a synthetic
higher version. No native MIDI enumeration or hardware operation was performed.
