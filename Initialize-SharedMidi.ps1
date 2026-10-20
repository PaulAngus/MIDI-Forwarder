[CmdletBinding()]
param([switch]$EnableWindowsServices, [switch]$PrepareBuild)

$ErrorActionPreference = 'Stop'
if (-not $EnableWindowsServices -and -not $PrepareBuild) { return }
$taskVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies/midi/version.txt') -Raw).Trim()
$taskPackage = Join-Path $PSScriptRoot "dependencies/midi/MightBeUseful.Midi.Windows.$taskVersion.nupkg"
if (-not (Test-Path -LiteralPath $taskPackage)) { throw 'The pinned shared Windows MIDI package is missing from dependencies/midi.' }
$taskStaging = Join-Path $PSScriptRoot 'obj/shared-midi'
New-Item -ItemType Directory -Path $taskStaging -Force | Out-Null
$taskInstaller = Join-Path $taskStaging 'Initialize-SharedMidi.ps1'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [System.IO.Compression.ZipFile]::OpenRead($taskPackage)
try {
    $taskEntry = $taskArchive.GetEntry('bootstrap/Initialize-SharedMidi.ps1')
    if ($null -eq $taskEntry) { throw 'The shared MIDI package has no optional-component installer.' }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry, $taskInstaller, $true)
} finally { $taskArchive.Dispose() }
& $taskInstaller -EnableWindowsServices:$EnableWindowsServices -PrepareBuild:$PrepareBuild
if ($PrepareBuild) {
    $taskFeed = Join-Path $PSScriptRoot 'local-packages'
    New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskStaging 'packages') -Filter 'Windows.Devices.Midi2.*.nupkg' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $taskFeed -Force
    }
}
