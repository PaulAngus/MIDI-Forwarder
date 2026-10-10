# Complete messages and reconnect boundaries

The transport remains device and application agnostic. A server gateway now
coordinates its local virtual ports and remote connections through one physical
device writer, with optional explicit reservation. It does not understand Fractal
commands or match arbitrary requests to replies. Direct physical connections remain
outside gateway coordination.

## Lessons applied from network MIDI

Apple's [network MIDI protocol](https://developer.apple.com/library/archive/documentation/Audio/Conceptual/MIDINetworkDriverProtocol/MIDI/MIDI.html)
separates session acceptance, termination and timing from MIDI data. The Forwarder
borrows the explicit session boundary, rather than copying its UDP protocol.
Windows [Network MIDI 2.0](https://microsoft.github.io/MIDI/kb/network-midi2-transport/)
also documents bounded recovery buffers, connection health checks and sending-speed
limits. These inform the buffering and measurement choices below; they are not a
claim that this custom TCP transport implements either network MIDI standard.

* Preserve logical message boundaries: incoming SysEx7 and SysEx8 UMP fragments are
  collected separately by group and stream. A complete multipart message travels
  in one TCP frame, preserving the original UMP bytes and group. Other messages
  can pass while assembly is in progress; an incomplete SysEx is never sent.
* Bound memory: a receive session holds at most 4096 queued messages and 4 MiB of
  queued data. Assembly is separately bounded to 4 MiB in total and 1 MiB for one
  message. Overflow ends the session rather than silently dropping commands.
* Reset on connection changes: queued commands and partial SysEx are discarded.
  The client's virtual ports stay present while disconnected, but app traffic is
  discarded. Orphan continuation/end fragments cannot restart an old message.
* Monitor without slowing MIDI callbacks: asynchronous metadata logs contain
  counts in both directions, write/send duration, receive-queue delay, buffer
  high-water marks, discarded traffic, assembly losses and failures. They never
  contain payloads, tokens, preset names or raw endpoint identifiers.

Assembly expires after ten seconds of inactivity, checked when another packet
arrives. This is an inactivity limit, not a maximum transfer duration: a steadily
arriving large SysEx can take longer. Bounds also apply when no further data arrives.

Sending uses the endpoint's `GetSupportedMaxMidiWordsPerTransmission` limit and
`SendMultipleMessagesWordArray`. A complete message that fits is submitted in one
call. Larger messages are split only between whole UMPs; a failed call stops all
remaining sends. No partially delivered message is automatically replayed.

These batches reduce the gaps caused by one send per fragment. The gateway keeps
its writer across all batches of one logical message, preventing interleaving by
other gateway ports. This cannot guarantee atomic delivery across directly connected
applications, especially for messages larger than one endpoint transmission.
Microsoft's [multi-client SysEx collision handling issue](https://github.com/microsoft/MIDI/issues/60)
motivates the application-level workaround; it is not a driver fix.
See the [endpoint API guidance](https://microsoft.github.io/MIDI/sdk-reference/MidiEndpointConnection/)
for batching, endpoint-specific limits, partial failures and attaching handlers
before opening a connection. Both adapters now attach handlers before `Open()`.

## Gateway boundaries

The server opens the physical device once while running. It accepts up to eight
remote connections and up to eight configured local ports. Each app must use its
own virtual port, so incoming SysEx assembly stays separate even when apps use the
same UMP group and SysEx8 stream. In shared mode device input is broadcast.

Reservation takes the physical-send semaphore, waits for a current send to finish,
and changes the owner while clearing queued input and partial assemblies. A monotonic
timestamp fence rejects local commands already dequeued before this boundary. Other
local ports pause without ending their readers; other network routes close, causing
the clients to discard offline traffic. The reserved connection receives replies
alone. Release clears queues again and resumes local ports; remote clients retry.
Owner disconnection releases the reservation. Local virtual ports remain registered
when their app closes, so a local reservation requires explicit release.

Commands already accepted by hardware cannot be recalled. Wait for an existing
device operation to finish before reserving another app. There is no generic
request/reply arbitration or automatic reservation timeout. See the
[gateway design notes](gateway-ux.md) for workflow and scope.

## Protocol version 3

**Update both client and server together.** Authentication begins with `MIDIFWD3`
and the same token-length/token structure, followed by a two-byte big-endian UTF-8
app-port name length and name (up to 128 bytes, no control characters). After
validation and route registration, the server acknowledges with `MIDIFWD3`. While
reserved, a new route receives `MIDIBUSY` and closes; the client waits and retries.
The client allows five seconds for acknowledgement before enabling forwarding.
Version 1 and 2 peers are rejected rather than appearing successfully connected.

Each TCP frame contains a four-byte big-endian length, one format byte, and the
data. Format 1 is MIDI 1.0 bytes, format 2 is one UMP, and format 3 is one complete
multipart SysEx7/8 message encoded as contiguous big-endian UMP words. Every batch
must contain a start packet, zero or more continuations and an end packet with
consistent message type, group and stream. The existing 1 MiB frame limit applies.

TCP already supplies ordered delivery and retransmission within a connection.
There is no RTP recovery journal, custom resend scheme or promise that the MIDI
hardware accepted a message simply because TCP delivered it. The shared token is
authentication, not encryption.

TCP uses no-delay and keep-alive. Both endpoints request a ten-second idle period,
three-second probe interval and three failed probes, so an unreachable connection
does not retain an owner for the system's much longer default. This detects TCP
liveness, not MIDI device responsiveness; detection time depends on the OS and
network. These are documented Windows
[IPPROTO_TCP options](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-tcp-socket-options).

## Diagnostics and validation

An optional bulk-SysEx sending-speed limit would be a useful next addition if
hardware measurements show device overruns. It is not enabled by this change:
delays would affect transfer speed, and a generic bridge cannot infer a Fractal
request timeout from ordinary MIDI traffic. Clock synchronization and scheduled
playback are relevant to performance timing; discovery helps setup. Neither
addresses the currently observed missing device replies directly.

Logs are `%LOCALAPPDATA%\MIDI Forwarder\client-transport.jsonl`,
`server-transport.jsonl`, and `server-gateway.jsonl`. Each session has its own identifier, starts and ends with
a record, and reports cumulative direction counters every two seconds while
traffic flows. Receive-buffer counters are cumulative for the adapter lifetime.
Each log rotates at approximately 2 MiB and keeps two backups. A bounded 256-record
writer queue drops log records rather than blocking transport; summaries report
that count. Logging failure does not stop MIDI. Concurrent connection log writers
serialize appends and rotation within the process to avoid losing another session's
records. The server's activity log reports reservation and other-process endpoint
access changes without inspecting MIDI payloads. Session reporting uses
[MidiReporting](https://microsoft.github.io/MIDI/sdk-reference/Reporting/MidiReporting/);
it identifies open endpoints, not proven competing sends or exclusive access.

Regression coverage includes interleaved groups/streams, stale reconnect traffic,
partial messages, overflow, transmission limits and mid-transfer failure, framing,
version acknowledgement, log privacy/failure, and real localhost TCP forwarding in
both directions. Gateway tests cover same-group app isolation, serialized writes,
reservation during a send, stale traffic, paused readers, owner loss, physical-send
failure, and busy authentication. The developer `--virtual-gateway` check exercises
the real client/server services with four local/remote app ports and a virtual
hardware endpoint, including interleaved SysEx, reservation, discarded offline
commands, release/reconnect and replies. It leaves physical hardware untouched.
The original FM9 timeout cause remains unproven; simultaneous FM9-Edit/PresetMaestro
hardware validation through distinct gateway ports is still needed.
