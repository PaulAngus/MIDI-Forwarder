# Third-party components

MIDI Forwarder uses these packages under their respective licenses:

- Avalonia UI (`Avalonia`, `Avalonia.Win32`, `Avalonia.Skia`, `Avalonia.Themes.Fluent`), MIT.
- NAudio (`NAudio.WinMM` and its core/MIDI dependencies), MIT.
- Microsoft C#/WinRT, MIT.
- Windows MIDI Services App SDK preview (`Windows.Devices.Midi2`), MIT.
- macOS builds use `Avalonia.Native` (MIT) and RtMidi 6.0.0 (MIT), using
  Homebrew's pinned Apple Silicon bottle. The RtMidi license is included
  in each macOS application bundle.
- xUnit, Microsoft.NET.Test.Sdk, and coverlet for tests under their published
  open-source licenses.

Package versions are recorded in the project files and `local-packages`.
