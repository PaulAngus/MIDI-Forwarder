# Full MIDI capture

Available in Client and Server 1.0.3 and later. Windows 1.0.4 also embeds the native API needed for automatic virtual interfaces; no separate API setup is required. Use **Settings > Capture** on the computer where you want to save the recording. Recording on one end captures both directions; the other end does not need this update. The TCP protocol is unchanged.

1. Connect the editor through MIDI Forwarder as usual.
2. Choose **Start capture**. You can do this before or after connecting, without restarting the relay.
3. Record some idle traffic first. Add a marker such as **Before renaming scene 1**, then perform that action on the hardware. Add another marker when you save it.
4. Choose **Stop capture** and wait for **Saved: no capture records lost**.
5. Use **Open capture folder** to find the JSONL file. The full path can also be selected and copied.

Capture is off on every app launch. Each start creates a new file. Closing Settings or hiding the app keeps capture running; quitting the app drains the recording to disk. A disconnect/reconnect is recorded within the same capture. The main window's Settings button indicates recording or a capture issue and opens the Capture page in either case.

For the FM9 investigation, close other MIDI applications that issue requests, capture FM9-Edit idle first, and test each action separately: preset rename, scene rename, Save, scene swap, preset swap, replacement/addition and clearing a preset. Capture hardware actions separately from actions performed within FM9-Edit. The latter already knows the edits it sends. A response to a regular editor request is different evidence from an unsolicited device notification; neither should be mistaken for a complete library scan.

## Files and limits

Files are saved below the app's per-user data folder, in **MIDI Forwarder/Captures**. On Windows this is `%LOCALAPPDATA%\MIDI Forwarder\Captures`. Filenames identify client/server, UTC start time and a unique capture ID. The folder button works on both platforms. Files can contain personal preset names and device settings. They stay local and are not automatically uploaded or deleted.

Each capture stops at 100 MiB (including its footer). The asynchronous writer has a queue bounded by both 4,096 records and 8 MiB of raw packet data. If writing cannot keep up, capture records can be lost while MIDI forwarding continues. The Capture page reports the count and the footer marks the recording incomplete. Disk errors are shown in the same page. A file without a `capture-end` record is unfinished or interrupted and must not be treated as complete evidence. A capture is an observation at the forwarding boundary, not an acknowledgement from the receiving device.

The writer receives owned copies of packet bytes. Hex encoding and file writes run in a background task. Capture does not wait for disk I/O on either MIDI pump. Copying and queueing still have a small CPU/allocation cost; no claim of zero overhead or hardware timing certification is made.

## JSONL schema 1

One UTF-8 JSON object per line:

- `capture-start`: schema version, local role, UTC origin, stopwatch frequency, file limit and direction definitions.
- `connection-start` / `connection-end`: unique connection ID and local MIDI input/output names. Starting capture while connected includes that connection's metadata.
- `packet`: monotonically increasing `sequence`, `utc`, monotonic `elapsedTicks`, `connection`, `direction`, `format`, byte `length` and complete uppercase `hex`.
- `marker`: the same timestamp/sequence fields plus user-entered `text` (up to 500 characters).
- `capture-end`: UTC end time, written packet count, `lostRecords`, `complete`, and stop `reason`.

Direction names describe the recording end:

| Direction | Server recording | Client recording |
| --- | --- | --- |
| `midi-to-network` | FM9/device to client/editor | Editor/app to server |
| `network-to-midi` | Client/editor to FM9/device | Server/device to editor/app |

Packets are recorded as received, before the subsequent forwarding write. A failed send is therefore still present as an observed packet; use the connection log when investigating delivery failure. UTC timestamps enable comparison with other logs; elapsed ticks preserve local ordering even if the wall clock changes. Sequence gaps and footer loss counts identify capture loss. Capture started after an action cannot establish what happened before recording began.

`Midi1` contains raw MIDI bytes. `UniversalMidiPacket` contains complete raw relay packets in the existing network byte order (big-endian UMP words). Multipart SysEx7/8 is retained as the observed UMP packets, with groups and boundaries unchanged; reconstruct it during analysis. No manufacturer bytes are interpreted, filtered or abbreviated by capture. Authentication tokens are not part of these MIDI records.

## UI decisions and verification

Consulted official [Windows settings guidance](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings) and [Fluent 2 button guidance](https://fluent2.microsoft.design/components/web/react/core/button/usage/) on 20 October 2026. These recommend clear action labels, feedback, accessible controls and a single scrollable column when needed. The implementation uses existing Avalonia/Fluent resources, explicit Start/Stop task buttons, a labelled marker field, polite status announcements, a copyable path and the existing separate Close action.

Product choice: capture has its own Settings tab because the existing Diagnostics tab must keep its connection log usable at the supported 440 x 440 minimum. It is an occasional troubleshooting task. Start/Stop acts immediately and does not use the connection Save button. At small heights only the Capture content scrolls; the existing footer remains available. Native window dismissal and Escape retain their existing behavior. Enter in the marker field adds the marker and keeps focus there.

Verification includes byte-for-byte loopback forwarding with capture disabled/enabled, a 70,000-byte SysEx and MIDI 2 UMP in both directions, starting/stopping during a connection, copied buffer ownership, connection metadata, UTF-8 markers, monotonic times, drain-on-stop, bounded queue loss, file limit/footer and disk-error handling. Client/server UI checks exercise keyboard markers and capture surviving Settings dismissal. Rendered Capture pages were inspected in both themes at 560 x 640 and 440 x 440, including scrolled lower content. No changes to FM9 presets were made during this verification.
