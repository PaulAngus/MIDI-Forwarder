# Device gateway: design and UI validation

The gateway provides one physical-device writer and a separate virtual port per
participating app. It supports local and remote apps with the same rules, without
manufacturer-specific parsing. The server keeps the physical endpoint open while
running; the client retains its public virtual ports across TCP reconnects.

## User workflow and product choices

Configure one distinct public port per app, and select that port for both input and
output. The server supplies local ports; the client supplies remote ports. Shared
mode serializes complete messages and broadcasts device replies. Before a bulk
operation, select its app connection on the server and choose **Reserve device**.
Choose **Release device** when it finishes. Reservation is manual because the
gateway cannot infer arbitrary request/reply boundaries or when a device operation
has finished from ordinary MIDI data.

Other local ports pause; other remote connections close and retry. Discarding their
commands avoids surprising playback or stale device queries on release. Paused apps
may time out. Ownership ends on explicit release, owner connection loss or server
stop. A local app exiting leaves its gateway port alive, so local ownership must be
released explicitly. There is no automatic time limit for a valid owner.

Reservations cover gateway routes only. Direct physical-device apps and multiple
apps sharing one public virtual port bypass this isolation. Other-process endpoint
reporting is advisory and clearly labelled. It does not imply an exclusive driver
lock. Wait for existing hardware operations to finish before switching owners;
commands already accepted by hardware cannot be cancelled by this gateway.

## Design guidance applied

Current official sources consulted for the changed controls:

- [Fluent 2 dropdown guidance](https://fluent2.microsoft.design/components/web/react/core/dropdown/usage)
  and [Windows combo boxes](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/combo-box):
  a labelled selector for one active connection, with full name available in a tooltip.
- [Fluent 2 buttons](https://fluent2.microsoft.design/components/web/react/core/button/usage)
  and [Windows buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/buttons):
  explicit **Reserve device** and **Release device** actions; disabled when unavailable.
- [Windows text boxes](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/text-box):
  persistent labels and accessible names, with multiline names described as one per line.
- [Fluent 2 accordion guidance](https://fluent2.microsoft.design/components/web/react/core/accordion/usage)
  and [Windows expander guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/expander):
  configuration is expanded while stopped and collapses on start, leaving status and
  active task controls prominent. The user can expand it while running. Connection
  events do not repeatedly override that choice.

These sources inform the pattern; the implementation uses the existing Avalonia
Fluent theme and controls rather than WinUI or web components. Manual ownership,
per-app ports, collapsed active configuration and reconnect behavior are product
choices, not Microsoft design requirements.

Labels stack above inputs to fit narrow windows. Main content scrolls, while the
footer remains reachable. **Hide to Tray** is separated from start/stop actions.
There is no new modal or details pane. Reservation moves keyboard focus to release;
release returns focus to the connection selector. Status, logs, names and inputs
have accessible names. Theme foreground/background resources replace fixed dark
log colours so both light and dark themes remain readable.

## Validation performed

`tools/MidiForwarder.Checks` renders the actual windows using Avalonia Headless with
the Skia renderer. Running and stopped/configuration states were inspected in light
and dark themes: server 760×900, client 700×760, and both at the supported minimum
480×600. Narrow content scrolls, and task/dismissal footer controls remain visible.
Keyboard Tab traversal covers the active selector, release, logs, stop/disconnect,
hide and configuration controls. This is a rendered layout and keyboard check, not
a screen-reader certification or an FM9-Edit hardware test.

Core regression tests and the native Windows virtual gateway check validate the
interaction consequences: only the owner sends/receives while reserved, other app
commands are discarded, release resumes connections without replay, owner loss
releases access, and physical-send failures terminate affected routes.
