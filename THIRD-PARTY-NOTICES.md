# Third-party components

MIDI Forwarder uses these packages under their respective licenses:

- Avalonia UI (`Avalonia`, `Avalonia.Win32`, `Avalonia.Skia`, `Avalonia.Themes.Fluent`), MIT.
- NAudio (`NAudio.WinMM` and its core/MIDI dependencies), MIT.
- Microsoft C#/WinRT, MIT.
- Windows MIDI Services App SDK preview (`Windows.Devices.Midi2`), MIT.
- macOS builds use `Avalonia.Native` (MIT) and RtMidi 6.0.0 (MIT), using
  Homebrew's pinned Apple Silicon bottle. The RtMidi license is included
  in each macOS application bundle.
- Shared physical macOS MIDI now uses DryWetMIDI 8.0.3 (MIT). Its copyright
  and license are included as `DryWetMidi.LICENSE.txt` in the Mac bundle.
- xUnit, Microsoft.NET.Test.Sdk, and coverlet for tests under their published
  open-source licenses.

Shared MIDI versions are pinned in `dependencies/midi/MidiVersion.props`;
the shared feed includes the native MIDI dependency license notices.
