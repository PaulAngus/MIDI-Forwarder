[CmdletBinding()]
param(
    [switch]$Sign,
    [string]$SigningCertificateThumbprint = $env:MBU_WINDOWS_SIGNING_THUMBPRINT,
    [string]$SignToolPath = $env:MBU_SIGNTOOL_PATH,
    [uri]$TimestampServer = 'http://time.certum.pl'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Publish-Windows.ps1 requires Windows. Use Publish-Mac.ps1 on the signing Mac.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$signScript = Join-Path $repoRoot 'dependencies/midi/Sign-WindowsExecutable.ps1'
$signing = @{ Sign = $Sign; SigningCertificateThumbprint = $SigningCertificateThumbprint; SignToolPath = $SignToolPath; TimestampServer = $TimestampServer }
& $signScript @signing -CheckOnly
# Build preparation downloads and verifies the pinned SDK; it installs no runtime.
& (Join-Path $repoRoot 'Initialize-SharedMidi.ps1') -PrepareBuild
foreach ($role in @('Client', 'Server')) {
    $project = Join-Path $repoRoot "src/MidiForwarder.$role/MidiForwarder.$role.csproj"
    $build = Join-Path $repoRoot ('artifacts/publish-' + [guid]::NewGuid().ToString('N'))
    $output = Join-Path $repoRoot ('publish/' + $role.ToLowerInvariant())
    dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path $build -o $output
    if ($LASTEXITCODE -ne 0) { throw "$role publish failed ($LASTEXITCODE)." }
    & $signScript @signing -ExecutablePath (Join-Path $output "MidiForwarder.$role.exe")
    Write-Output (Join-Path $output "MidiForwarder.$role.exe")
}
