[CmdletBinding()]
param([switch]$ClientOnly, [string]$SigningIdentity = '-', [string]$NotaryProfile)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
foreach ($role in $(if ($ClientOnly) { @('Client') } else { @('Client','Server') })) {
    & (Join-Path $repoRoot 'dependencies/midi/Publish-MacApplication.ps1') `
        -Project (Join-Path $repoRoot "src/MidiForwarder.$role.Mac/MidiForwarder.$role.Mac.csproj") `
        -Executable "MidiForwarder.$role" -Name "MIDI Forwarder $role" `
        -Identifier "org.midiforwarder.$($role.ToLowerInvariant())" -Version $version `
        -OutputDirectory (Join-Path $repoRoot 'publish/macos-arm64') `
        -Icon (Join-Path $repoRoot 'assets/midi-forwarder.icns') `
        -SigningIdentity $SigningIdentity -NotaryProfile $NotaryProfile
}
