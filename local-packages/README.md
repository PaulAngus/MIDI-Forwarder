# local-packages

This folder is a local NuGet source (see `NuGet.Config`). It only needs to
contain `Windows.Devices.Midi2`, which is not published to nuget.org — every
other dependency restores normally from nuget.org.

Run `./Initialize-MidiSdk.ps1` from the repository root before restoring a new
checkout. It downloads Microsoft's pinned `0.99.88-preview.10` package from the
[preview 10 release](https://github.com/microsoft/MIDI/releases/tag/inbox-preview-10)
and verifies its SHA-256 checksum. Then run `dotnet restore`.

This folder is git-ignored; the `.nupkg` file itself is never committed.
