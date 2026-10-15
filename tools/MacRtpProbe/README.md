# Apple network MIDI comparison probe

This Apple Silicon diagnostic tests a local Apple RTP-MIDI driver endpoint while
retaining the existing TCP connection to the Windows forwarder. It is an
experiment, not an installed-client replacement. It accepts only loopback UDP
peers and does not change the Windows protocol or server. The live test requests
FM9 preset dumps for slots 200–229; it does not write presets or change the
selected preset.

In Audio MIDI Setup, open MIDI Studio, then Configure Network Driver. Create an
RTP session, leave its local name `RTP Session 1`, and enable it. Keep the
connection policy `No one` and live routings `None`. Add a directory entry named
`Loopback Forwarder Test`, address `127.0.0.1`, port `55006`. Leave its persistent
peer checkbox unchecked. The diagnostic listens on local UDP 55006 and 55007.

Start the probe from the repository root, then select the directory entry and
click Connect. It waits for six startup clock synchronization exchanges before
beginning MIDI checks. Override `--endpoint` if the session's CoreMIDI display
name differs from `Network RTP Session 1`.

```sh
# Disconnect the normal client first: the Windows server accepts one TCP client.
dotnet run --project tools/MacRtpProbe -- --live

# Replay a captured dump: one complete raw MIDI SysEx message per hex-file line.
dotnet run --project tools/MacRtpProbe -- --fixture /path/to/fm9-dump.hex

# Independent large SysEx and channel-message checks, without hardware/TCP.
dotnet run --project tools/MacRtpProbe -- --large-only

# Verify the integrated, running client. Do not disconnect it for this mode.
dotnet run --project tools/MacRtpProbe -- --installed --start 200 --count 30
```

`--installed` uses the client's existing native endpoint and connection, with
independent DryWetMIDI/RtMidi receivers and Fractal reply-checksum validation.
It does not open TCP or bind the local RTP peer ports. Choose up to all 512
slots with `--start 0 --count 512`. The other modes above predate integration
and run their own diagnostic responder; disconnect the client for those modes.

Live mode reads saved server settings through `DiagnosticLog` without printing
the authentication token. Each reply is reconstructed from TCP UMP, sent as
RTP-MIDI, and compared byte-for-byte with both DryWetMIDI 8.0.3 (PresetMaestro's
library) and an independent RtMidi receiver. Requests travel from DryWetMIDI
through Apple's destination endpoint to the local peer before going to TCP.

Replay sends 30 dumps with 80 ms between messages. Large-message checks deliver
70,000-byte SysEx into both receivers, 60,000-byte SysEx back through DryWetMIDI,
and channel messages in both directions. DryWetMIDI's bundled native output
publishes each SysEx in one legacy MIDIPacket, whose length is 16 bits; its
70,000-byte outbound test did not complete, so that direction is intentionally
tested below the packet limit. This limitation is separate from FM9's 3,082-byte
dump chunks.

After testing, reconnect the normal client and disable the test network MIDI
session. The probe sends a session exit packet on disposal. It does not modify
Audio MIDI Setup configuration itself.

The responder implements the negotiated Apple control/clock exchange and the
RTP-MIDI command subset exercised here, including SysEx segmentation. It rejects
sequence gaps, ignores received recovery journals, and sends no recovery journal
of its own. This is not a general-purpose reliable RTP-MIDI implementation;
reconnection, arbitrary MIDI streams, fault recovery, and production UI setup
still need implementation before shipping this approach.
