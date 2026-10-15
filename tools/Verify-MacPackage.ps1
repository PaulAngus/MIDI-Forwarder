param([ValidateSet('Client', 'Server')][string[]]$Roles = @('Client', 'Server'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$bundleRoot = Join-Path $repoRoot 'publish/macos-arm64/MIDI Forwarder Apple Silicon'

function Read-BigEndianUInt32([byte[]]$Bytes, [int]$Offset) {
    return [uint32](([uint64]$Bytes[$Offset] -shl 24) -bor ([uint64]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint64]$Bytes[$Offset + 2] -shl 8) -bor [uint64]$Bytes[$Offset + 3])
}

function Test-Arm64Binary([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $magic = [BitConverter]::ToUInt32($bytes, 0)
    $arm64 = 0x0100000c
    $sliceOffset = 0
    if ($magic -eq [Convert]::ToUInt32('bebafeca', 16)) {
        $sliceOffset = -1
        $count = Read-BigEndianUInt32 $bytes 4
        for ($index = 0; $index -lt $count; $index++) {
            $offset = 8 + 20 * $index
            if ((Read-BigEndianUInt32 $bytes $offset) -eq $arm64) {
                $sliceOffset = Read-BigEndianUInt32 $bytes ($offset + 8)
                break
            }
        }
    }
    if ($sliceOffset -lt 0 -or [BitConverter]::ToUInt32($bytes, $sliceOffset) -ne [Convert]::ToUInt32('feedfacf', 16) -or
        [BitConverter]::ToUInt32($bytes, $sliceOffset + 4) -ne $arm64) {
        throw "No Apple Silicon Mach-O binary found: $Path"
    }

    $commandCount = [BitConverter]::ToUInt32($bytes, $sliceOffset + 16)
    $commandOffset = $sliceOffset + 32
    for ($index = 0; $index -lt $commandCount; $index++) {
        $command = [BitConverter]::ToUInt32($bytes, $commandOffset)
        $commandSize = [BitConverter]::ToUInt32($bytes, $commandOffset + 4)
        if (($command -band 0x7fffffff) -in @(0xc, 0x18, 0x1f, 0x20, 0x23)) {
            $nameOffset = $commandOffset + [BitConverter]::ToUInt32($bytes, $commandOffset + 8)
            $nameEnd = $nameOffset
            while ($bytes[$nameEnd] -ne 0) { $nameEnd++ }
            $name = [Text.Encoding]::UTF8.GetString($bytes, $nameOffset, $nameEnd - $nameOffset)
            if ($name.StartsWith('/') -and -not ($name.StartsWith('/System/Library/') -or $name.StartsWith('/usr/lib/'))) {
                throw "Non-system absolute native dependency: $name in $Path"
            }
        }
        if ($command -eq 0x32) {
            $minimum = [BitConverter]::ToUInt32($bytes, $commandOffset + 12)
            if ($minimum -gt 0x000f0000) {
                throw "Native library requires macOS newer than 15.0: $Path"
            }
        }
        $commandOffset += $commandSize
    }
    Write-Host "Apple Silicon verified: $([IO.Path]::GetFileName($Path))"
}

foreach ($role in $Roles) {
    $contents = Join-Path $bundleRoot "MIDI Forwarder $role.app/Contents"
    $plist = [System.Xml.Linq.XDocument]::Load((Join-Path $contents 'Info.plist'))
    $elements = @($plist.Root.Element('dict').Elements())
    $properties = @{}
    for ($index = 0; $index -lt $elements.Count; $index += 2) {
        $properties[$elements[$index].Value] = $elements[$index + 1].Value
    }
    if ($properties['CFBundleExecutable'] -ne "MidiForwarder.$role" -or $properties['LSMinimumSystemVersion'] -ne '15.0') {
        throw "Incorrect macOS bundle metadata for $role."
    }
    if ($properties['CFBundleIconFile'] -ne 'midi-forwarder' -or
        -not (Test-Path -LiteralPath (Join-Path $contents 'Resources/midi-forwarder.icns'))) {
        throw "Missing application icon for $role."
    }
    $sourceIconHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'assets/midi-forwarder.icns')).Hash
    $bundleIconHash = (Get-FileHash -LiteralPath (Join-Path $contents 'Resources/midi-forwarder.icns')).Hash
    if ($sourceIconHash -ne $bundleIconHash) {
        throw "Outdated application icon in the macOS $role bundle."
    }
    foreach ($binary in Get-ChildItem -LiteralPath (Join-Path $contents 'MacOS') -File) {
        Test-Arm64Binary $binary.FullName
    }
}

foreach ($role in $Roles) {
    $libraryPath = Join-Path $bundleRoot "MIDI Forwarder $role.app/Contents/MacOS/librtmidi.7.dylib"
    $libraryBytes = [System.IO.File]::ReadAllBytes($libraryPath)
    $symbols = [Text.Encoding]::ASCII.GetString($libraryBytes)
    foreach ($symbol in @('rtmidi_in_create', 'rtmidi_out_create', 'rtmidi_get_port_name', 'rtmidi_open_virtual_port',
        'rtmidi_in_set_callback', 'rtmidi_in_ignore_types', 'rtmidi_out_send_message', 'rtmidi_in_free', 'rtmidi_out_free')) {
        if (-not $symbols.Contains($symbol)) { throw "Missing RtMidi native symbol in $role bundle: $symbol" }
    }
}
Write-Host 'Native dependencies, bundle metadata and RtMidi symbols verified.'
