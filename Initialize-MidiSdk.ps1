[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Microsoft currently distributes the preview package through GitHub releases.
$taskPackageDirectory = Join-Path $PSScriptRoot 'local-packages'
$taskPackage = Join-Path $taskPackageDirectory 'Windows.Devices.Midi2.0.99.88-preview.10.nupkg'
$taskHash = '6DAF121A3F76A4A1E0D46521C5072AC576477448C9332048383BB58960A4CE69'
New-Item -ItemType Directory -Force -Path $taskPackageDirectory | Out-Null
if ((Test-Path -LiteralPath $taskPackage) -and (Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash -eq $taskHash) {
    return
}
$taskDownload = $taskPackage + '.download'
Invoke-WebRequest 'https://github.com/microsoft/MIDI/releases/download/inbox-preview-10/Windows.Devices.Midi2.0.99.88-preview.10.nupkg' -OutFile $taskDownload
if ((Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash -ne $taskHash) {
    throw 'The Windows MIDI Services preview package checksum did not match. The package has not been installed.'
}
Move-Item -LiteralPath $taskDownload -Destination $taskPackage -Force
