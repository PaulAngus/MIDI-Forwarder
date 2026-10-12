# Device gateway: design and UI validation

The gateway provides one physical-device writer and a separate virtual port per
participating app. Local and remote apps always share the device. Version 1.0.1
removes reservation: no app owns the gateway, pauses another route, suppresses
another app's replies, or rejects connections because another app is active.

## User workflow and product choices

Configure distinct public ports for participating apps and select those ports for
input and output. All routes can send; device replies go to every connected route.
Complete individual messages are serialized to prevent byte interleaving. The
forwarder does not lock an entire request/reply exchange or bulk transfer. Apps
must coordinate transfers when their device protocol requires that.

Each route has its own receive queue and SysEx assembly. A slow recipient cannot
hold up other recipients. Disconnecting one route preserves the others' pending
messages. Direct physical-device apps use Windows MIDI Services shared access;
other-process endpoint reporting is informational. The forwarder cannot correct
an operating-system service failure or another application's exclusive legacy API.

The desktop server and client each allow one instance per Windows session.
Start/stop operations are serialized and native MIDI setup runs off the UI thread.

## Design guidance applied

Official guidance consulted for this change:

- [Fluent 2 buttons](https://fluent2.microsoft.design/components/web/react/core/button/usage/):
  retain clear task labels and remove the obsolete Reserve/Release actions.
- [Windows keyboard interactions](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions):
  keep keyboard traversal and visible focus through the remaining controls.

The existing Avalonia Fluent theme supplies controls, focus indicators and theme
colours. The Device access section explains Always shared and lists available
connections without an ownership selector. Labels wrap at narrow widths, main
content scrolls, and the footer separates Hide to Tray from Start/Stop actions.
Always-shared routing and per-app ports are product choices, not Microsoft design
requirements. No new details pane or modal interaction is introduced.

## Validation

The checks tool renders actual Avalonia windows using Skia in light and dark
themes, at server 760x900/client 700x760 and the supported minimum 480x600. It also
checks keyboard traversal. Rendered previews are under artifacts/shared-access-preview.

Core regression checks cover concurrent connections, broadcast replies, slow
recipients, disconnect isolation, complete-message ordering and duplicate-instance
leases. They do not certify screen readers or physical hardware interoperability.

## Service hang investigation and final native validation

The old asymmetric loopback pair (UMP-only Relay, legacy-visible public side)
reproduced a service-wide stall during endpoint activation. Removing reservations
and duplicate desktop processes alone did not resolve it. The adapter now pins
SDK `0.99.88-preview.10` and exposes MIDI 1.0 ports on both sides of a symmetric
loopback pair. The internal Relay name is truncated to the WinMM length limit;
users continue selecting the configured public name.

An application-owned virtual-device replacement passed live traffic but stalled
MidiSrv during disposal. Preset Sender's original virtual-device publisher also
stalled on shutdown in the combined check. Microsoft's
[virtual transport shutdown source](https://github.com/microsoft/MIDI/blob/inbox-preview-10/src/in-box/Transport/VirtualMidiTransport/Midi2.VirtualMidiBidi.cpp)
contains a feature-gated `MIDI2VirtualDeviceRemovalDeadlock` fix. That source is
consistent with the observed shutdown failure, but does not establish which
Windows feature flags are enabled on this machine. Both apps therefore use
explicitly removed symmetric loopback pairs, avoiding that removal path.

On 11 October 2026, the revised combined check passed with the actual published
Preset Sender and native Forwarder client/server services: both startup orders,
two independent WinMM receivers on all sixteen channels, three forwarder
start/stop cycles, four simultaneous routes, interleaved multipart SysEx,
broadcast replies, continued local traffic after remote disconnect, and clean
sender shutdown. No physical device or saved configuration was used. The separate
WinMM/WinRT port visibility and bidirectional Program Change/SysEx check passed.
The shared-access check passed 400 distinct 16 KiB requests with 200 complete
1 KiB replies per app, using simultaneous direct WinMM and forwarded traffic.
All 39 core tests passed. The changed server layouts had already been visually
inspected at both supported sizes and in both themes; this transport fix makes
no further layout changes.

The user authorized a temporary elevated helper restricted to restarting
MidiSrv, with a one-hour expiry. It recovered the service during investigation;
it is a development recovery tool, not part of either application's runtime.
