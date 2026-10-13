#!/bin/bash
set -euo pipefail
cd -- "$(dirname -- "$0")"

if [[ "$(uname -m)" != "arm64" ]]; then
    echo "These apps require an Apple Silicon Mac."
    read -r -p "Press Return to close. "
    exit 1
fi

for app in "MIDI Forwarder Client.app" "MIDI Forwarder Server.app"; do
    if [[ ! -d "$app" ]]; then
        echo "Missing $app. Keep this script beside both apps."
        exit 1
    fi
    chmod +x "$app/Contents/MacOS/"MidiForwarder.*
    xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
    # Cross-publishing on Windows cannot sign the final Mach-O apphost.
    # Local ad-hoc signing also repairs the bundled Homebrew library signature.
    codesign --force --deep --sign - "$app"
    codesign --verify --deep --strict "$app"
    echo "Ready: $app"
done

echo "You can now open either app or move it into Applications."
read -r -p "Press Return to close. "
