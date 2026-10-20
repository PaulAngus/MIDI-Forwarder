# local-packages

This folder is a local NuGet source (see `NuGet.Config`). It only needs to
contain `Windows.Devices.Midi2`, which is not published to nuget.org — every
other dependency restores normally from nuget.org.

`tools/Publish-Windows.ps1` prepares this build dependency automatically.
For a direct `dotnet restore`, first run
`.\Initialize-SharedMidi.ps1 -PrepareBuild`. The bootstrap inside the pinned shared
MIDI package downloads Microsoft's `0.99.88-preview.10` SDK and verifies its
SHA-256 before copying it here. It does not install application runtime files.

This folder is git-ignored; the `.nupkg` file itself is never committed.

Mac publishing takes its native MIDI libraries from the pinned shared packages.
It does not use the historical `rtmidi/` cache or require Homebrew on users' Macs.
