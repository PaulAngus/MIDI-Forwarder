# local-packages

This folder is a local NuGet source (see `NuGet.Config`). It only needs to
contain `Windows.Devices.Midi2`, which is not published to nuget.org — every
other dependency restores normally from nuget.org.

To populate it on a new machine:

1. Go to https://github.com/microsoft/MIDI/releases and find the release whose
   notes say "NuGet package version for this release is `X.Y.Z-devpreview.N`"
   matching the version pinned in
   `src/MidiForwarder.Midi.WindowsServices/MidiForwarder.Midi.WindowsServices.csproj`
   (currently `0.99.83-devpreview.9`).
2. Download that release's NuGet package asset and extract the
   `Windows.Devices.Midi2.<version>.nupkg` file into this folder.
3. Run `dotnet restore` from the repo root.

This folder is git-ignored; the `.nupkg` file itself is never committed.
