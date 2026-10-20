#requires -Version 7.0
[CmdletBinding()]
param([string]$ResultsDirectory)

# Exercise the normal publisher from a minimal checkout fixture. Build preparation
# and dotnet are stubs: these checks do not install SDKs, launch apps or open MIDI.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Run the Windows publisher checks on Windows.' }
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repo ('artifacts/publish-tests-' + [guid]::NewGuid().ToString('N'))
}
$root = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$state = @{}
function Read-Host { throw 'Publishing must not prompt for another action.' }
function dotnet {
    $arguments = @($args)
    $state.Builds++
    if ($state.Builds -eq $state.FailBuild) { $global:LASTEXITCODE = 23; return }
    $output = $arguments[[array]::IndexOf($arguments, '-o') + 1]
    $role = if (($arguments -join ' ') -match 'MidiForwarder.Client') { 'Client' } else { 'Server' }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # An OS file supplies real version metadata for any archive helper present.
    # The copied fixture is never executed and is not a distributable application.
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32/whoami.exe') -Destination (Join-Path $output "MidiForwarder.$role.exe")
    $global:LASTEXITCODE = 0
}
foreach ($case in @('default', 'client-failure', 'server-failure', 'missing-signing-configuration')) {
    $folder = Join-Path $root ($case + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $folder 'tools'), (Join-Path $folder 'dependencies/midi') -Force | Out-Null
    $entry = Join-Path $folder 'tools/Publish-Windows.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish-Windows.ps1') -Destination $entry
    foreach ($name in @('Sign-WindowsExecutable.ps1', 'New-WindowsReleaseArchive.ps1')) {
        $source = Join-Path $repo "dependencies/midi/$name"
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $folder 'dependencies/midi') }
    }
    Set-Content -LiteralPath (Join-Path $folder 'Initialize-SharedMidi.ps1') -Value 'param([switch]$PrepareBuild)'
    $state.Builds = 0
    $state.FailBuild = switch ($case) { 'client-failure' { 1 } 'server-failure' { 2 } default { 0 } }
    $options = @{ SigningCertificateThumbprint = '' }
    if ($case -eq 'missing-signing-configuration') { $options.Sign = $true }
    $errorText = $null
    try { & $entry @options | Out-Host } catch { $errorText = $_.Exception.Message }
    if ($case -eq 'default') {
        if ($null -ne $errorText -or $state.Builds -ne 2) { throw "Default publication failed: $errorText" }
        foreach ($role in @('Client', 'Server')) {
            $exe = Join-Path $folder "publish/$($role.ToLowerInvariant())/MidiForwarder.$role.exe"
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing $role output." }
        }
    }
    else {
        $expected = switch ($case) {
            'client-failure' { 'Client publish failed' }
            'server-failure' { 'Server publish failed' }
            default { 'exact 40-character certificate thumbprint' }
        }
        $expectedBuilds = switch ($case) { 'client-failure' { 1 } 'server-failure' { 2 } default { 0 } }
        if ($null -eq $errorText -or $errorText -notmatch $expected -or $state.Builds -ne $expectedBuilds) {
            throw "Wrong result for ${case}: builds=$($state.Builds), error=$errorText"
        }
    }
    $results.Add([pscustomobject]@{ Case = $case; Result = 'PASS'; BuildCalls = $state.Builds; Error = $errorText })
    Write-Host "PASS: $case"
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json')
Write-Host "Windows publisher checks passed. Isolated fixtures: $root"
