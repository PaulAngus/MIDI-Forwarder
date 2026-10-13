$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$dependencyFolder = Join-Path $repoRoot 'local-packages/rtmidi/6.0.0/osx-arm64'
$library = Join-Path $dependencyFolder 'librtmidi.7.dylib'
$librarySha256 = 'd8d17982fa524366253dd6f47b50cb7a0e09cf7a30a45ff46b93e0e51d6c82e0'
if ((Test-Path -LiteralPath $library) -and
    (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() -eq $librarySha256 -and
    (Test-Path -LiteralPath (Join-Path $dependencyFolder 'LICENSE'))) {
    return
}

# Homebrew's arm64 Sequoia bottle, pinned by SHA-256; no Homebrew installation is needed.
$sha256 = 'e575e9ccadbf67966dc23d94135d12403c70f18f0e4fdfc01f7901211663ad5b'
$downloadFolder = Join-Path $repoRoot 'artifacts/rtmidi-6.0.0-arm64'
New-Item -ItemType Directory -Path $downloadFolder -Force | Out-Null
$archive = Join-Path $downloadFolder 'rtmidi.tar.gz'
$token = (Invoke-RestMethod -Uri 'https://ghcr.io/token?service=ghcr.io&scope=repository:homebrew/core/rtmidi:pull').token
Invoke-WebRequest -Uri "https://ghcr.io/v2/homebrew/core/rtmidi/blobs/sha256:$sha256" `
    -Headers @{ Authorization = "Bearer $token" } -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256) {
    throw 'RtMidi dependency checksum mismatch.'
}

tar -xzf $archive -C $downloadFolder 'rtmidi/6.0.0_1/lib/librtmidi.7.0.0.dylib' 'rtmidi/6.0.0_1/LICENSE'
if ($LASTEXITCODE -ne 0) {
    throw 'Could not extract the RtMidi dependency.'
}
New-Item -ItemType Directory -Path $dependencyFolder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $downloadFolder 'rtmidi/6.0.0_1/lib/librtmidi.7.0.0.dylib') -Destination $library
Copy-Item -LiteralPath (Join-Path $downloadFolder 'rtmidi/6.0.0_1/LICENSE') -Destination (Join-Path $dependencyFolder 'LICENSE')
if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() -ne $librarySha256) {
    throw 'Extracted RtMidi library checksum mismatch.'
}
Write-Host "Installed pinned RtMidi dependency: $library"
