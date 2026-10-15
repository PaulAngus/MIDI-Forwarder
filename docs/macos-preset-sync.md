# macOS preset sync diagnosis (13 October 2026)

On macOS 27.0.1, sustained FM9 preset dumps reached the client over TCP but
disappeared while publishing the virtual CoreMIDI source. PresetMaestro waited
15 seconds for the absent dump footer, then drained late replies and retried.
Its sync log showed fast send, decode and UI operations, with the time spent
in reply deadlines and timeout quarantine.

The fault was reproduced without hardware: a local TCP server replayed recorded
UMP packets at the captured timing. Both DryWetMIDI 8.0.3 (the application's
library) and an RtMidi monitor missed part of every fifth dump. Rapid playback
passed, so short/burst-only self-checks did not expose it. Direct TCP reads of
30 physical presets completed with all ten SysEx messages per preset.

At each loss, macOS MIDIServer logged `DeviceTable.cpp:1036 feedback loop from
MIDI source`. Inspection of the installed CoreMIDI `MIDISource::SchedulePacketList`
implementation showed a rate detector counting **UMP bytes**, testing six
one-second buckets against 90,006 bytes and suppressing the source for 12 seconds.
This is observed implementation behavior on this OS, not a documented Apple
compatibility guarantee. A 3,082-byte FM9 data message becomes approximately
4,112 UMP bytes; ordinary sustained dumps exceed that detector's budget without
any actual MIDI feedback path. Neither switching to the modern UMP API nor
declaring a higher `maxSysExSpeed` changed the failure in isolated comparisons.

The Mac virtual output now uses a 12,000 UMP-byte/second budget with an 8,192-byte
burst allowance. The corresponding maximum six-second volume stays below the
observed limit. Small control bursts remain immediate; bulk transfers progress
at a steady rate instead of losing messages and waiting for retries. Physical
Mac outputs and the Windows adapters are not paced by this change.

The virtual source is owned directly through CoreMIDI so SysEx larger than
4,096 bytes can be published in paced fragments. RtMidi's output function
rejects continuation fragments without a leading status byte. Apple documents
SysEx portions across packet lists and the 65,536-byte packet-list limit in
[MIDIPacketListAdd](https://developer.apple.com/documentation/coremidi/midipacketlistadd(_:_:_:_:_:_:)).
Native return codes are checked, and cancellation terminates an already-started
SysEx with EOX so the next session cannot append to it.

Validation includes native sustained-transfer, 70,000-byte fragmentation and
cancellation/reuse checks. The previously failing ten-dump timed replay delivered
all 100 messages after pacing. `--check-virtual-midi` now checks sustained bulk
traffic in both directions, not only a ten-byte SysEx message.

The installed fixed client then completed 30 consecutive read-only FM9 dumps
(slots 200–229): all 300 reply messages reached both the DryWetMIDI and RtMidi
receivers, with no missing frames and no CoreMIDI feedback-loop log entries
during sustained transfer. All 14 CoreMIDI tests and the installed application's
expanded self-check passed. The full 512-slot PresetMaestro UI sync was not run.

## Built-in driver alternatives investigated

The fault applies to the forwarder's choice of an application-created virtual
source, not MIDI generally. Apple's USB MIDI driver serves class-compliant USB
hardware; it does not provide an application API for publishing TCP replies as
USB input. The [FM9 manual](https://www.fractalaudio.com/downloads/manuals/FM9/FM9-Owners-Manual.pdf)
confirms that no extra Mac driver is required.

Apple's built-in IAC driver is also classified as virtual by this installed
CoreMIDI implementation. `MIDIEndpoint::SetIsVirtual` compares the owning driver
name with `com.apple.AppleMIDIIACDriver`; driver-owned sources with that flag
allocate the same rate detector as application-owned sources. Both comparison
strings were confirmed by a read-only diagnostic in a separate process:
the other string is `com.apple.AppleMIDIUSBDriver`. Thus, the disassembly does
not support expecting IAC to remove the limit; an IAC throughput comparison has
not been performed.

Apple's built-in network MIDI driver is a different candidate. Its documented
[RTP-MIDI protocol](https://developer.apple.com/library/archive/documentation/Audio/Conceptual/MIDINetworkDriverProtocol/MIDI/MIDI.html)
can provide a driver-owned endpoint, potentially using a local loopback peer
while retaining the forwarder's existing TCP connection. This requires a new
protocol adapter, including session negotiation, timestamps and SysEx
segmentation; it is not an interchangeable output API. The installed SDK's `MIDINetworkSession.h`
also explicitly states that session configuration methods are non-functional
on macOS, so setup must occur in Audio MIDI Setup rather than using the iOS
session API.

## Apple network MIDI experiment

A dedicated RTP session was created in Audio MIDI Setup, with connection policy
`No one`, no live routings, and an outgoing connection to a diagnostic peer bound
only to `127.0.0.1:55006/55007`. The normal client's saved settings and the Windows
server were unchanged. The test peer implements Apple's invitation and clock
exchange, waits for six observed startup calibration exchanges, and publishes
received TCP messages using RFC 6295 SysEx segments with 1,000-byte data parts.
Its RTP header matches the installed driver's observed M=0/PT=0x61 header.

The peer reproduces sustained traffic without the virtual-source pacing budget.
Thirty captured dumps at 80 ms per message delivered all 300 messages exactly
to both DryWetMIDI 8.0.3 and an independent RtMidi receiver in 24.6 seconds.
Large-message checks passed with 70,000-byte SysEx into both receivers,
60,000-byte SysEx out through DryWetMIDI, and channel messages both directions.

An extra 70,000-byte DryWetMIDI **outbound** check failed. Increasing the UDP
receive buffer and extending the deadline did not resolve it. Inspection of
the bundled native `SendSysExEventToOutputDevice_Mac` showed one call to
`MIDIPacketListAdd` for the entire message followed by `MIDISend`; the legacy
packet's length is 16 bits. The 60,000-byte outbound check subsequently delivered
all bytes in about 2 ms. This library limit does not affect FM9's 3,082-byte dump
chunks or the successful 70,000-byte receive test.

The live comparison used the same FM9, Windows TCP server, and preset slots
200–229:

| Path | 30 preset dumps | Delivery |
| --- | ---: | --- |
| Installed paced virtual-source client | 81.91 seconds | 300 messages on both receivers |
| TCP → loopback RTP → Apple network MIDI source | 20.446 seconds | 300 messages, each byte compared with TCP on both receivers |

The network route was approximately four times faster. Most individual live
dumps completed in 0.65–0.66 seconds, consistent with the earlier direct TCP
measurement. Its 1,021 outgoing RTP packets and 31 incoming packets completed
without observed sequence gaps; MIDIServer had no feedback-loop log entries
during the comparison. Transfer timing starts after the connection is ready;
network session clock calibration adds approximately eight seconds once at
startup. The baseline also includes probe process startup and 20 ms of monitor
settling per preset, together under one second of the measured difference.

The repeatable diagnostic is saved in [tools/MacRtpProbe](../tools/MacRtpProbe/README.md).
It builds with zero warnings/errors. Its live mode sends only preset-read
queries, and its replay mode needs a supplied captured dump file. At the end of
this experiment the paced client was restored and the RTP session disabled.

This experiment established that the built-in network driver could carry the
FM9 workload at the incoming TCP rate on this Mac. The production integration
and its validation are described below.

## Integrated client (14 October 2026)

The Mac client now defaults to Apple network MIDI. `CoreMidiNetworkPort` owns
the loopback-only control/data sockets, establishes the session using Apple's
documented invitation and synchronization protocol, and creates a separate MIDI
queue, SysEx decoder and RTP sequence state for each connection. The existing
TCP protocol and authentication are unchanged. The paced virtual ports remain
available through the explicit compatibility option.

A restart test showed that relying on the Audio MIDI Setup persistent-peer
checkbox did not re-establish a connection within the 60-second test deadline.
The client therefore initiates the local connection itself. Audio MIDI Setup
must enable the RTP session, include `127.0.0.1:55006` as a directory entry, and
use the `Directory entries` connection policy. Live routings remain `None`;
the persistent-peer checkbox is unnecessary. Two consecutive automated native
checks established and calibrated their connections without UI clicks.

Inbound RTP accepts duplicate/late packets without replaying their MIDI events.
A missing sequence closes the MIDI/TCP session, rather than emitting guessed
recovery data or an incomplete SysEx. MIDI messages are length-checked and the
receive queue is bounded. A new connection cannot inherit old messages, partial
SysEx or sequence state. Outgoing large SysEx is segmented; cancellation sends
the protocol's SysEx cancellation marker. The local connection supplies periodic
clock exchanges and detects an expired peer. This is a local RTP link; the
computer-to-computer link remains TCP.

Validation:

- All 30 Mac tests passed, including long SysEx, running status, clock interleave,
  malformed input, sequence loss, duplicates, wraparound and cancellation.
- Native integration checks passed controls, clock, 100 sustained 3,082-byte
  messages in each direction, 70,000-byte input and 60,000-byte output. Automatic
  initiation and clean restart were exercised separately.
- The signed `/Applications/MIDI Forwarder Client.app` passed the same native
  integration check. Its executable matches the prepared bundle and the copy in
  `~/Applications`, and its code signature verifies.
- The installed client completed slots 200–229 in 19.855 seconds. Both independent
  receivers agreed on all 300 messages, every Fractal checksum passed, and each
  dump had the expected header, eight chunks and footer.

The new settings were saved, and PresetMaestro's input/output were both changed
to `Network RTP Session 1`. A full PresetMaestro **Sync device** run then completed
successfully: 512 slots checked, 264 populated presets read, 248 empty slots
skipped, zero errors, zero MIDI timeouts/failures, zero dropped diagnostic
records, and no log write failure. It took 180.207 seconds, from
2026-10-14 07:50:10 UTC to 07:53:10 UTC. The 264 dump operations match the 264
populated presets, with no repeat dumps. The longest MIDI reply was 709 ms.
The UI reported `Device synced` and confirmed that presets, scenes, amps and
profile preset/scene names were refreshed. The client and PresetMaestro were
left connected using the native endpoint.

The Windows cross-build could not be completed on this checkout because its
local `Windows.Devices.Midi2` package is absent. The Mac package builds with zero
warnings/errors; no Windows binary was replaced.
