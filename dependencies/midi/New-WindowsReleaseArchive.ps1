#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$Name,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string[]]$NoticePath = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = Get-Item -LiteralPath $ExecutablePath
if ($exe.PSIsContainer -or $exe.Extension -ne '.exe') { throw 'Expected a published Windows executable.' }
# Use the binary's version, not a source version that may have advanced since publishing.
$version = $exe.VersionInfo.ProductVersion -replace '\+.*$', ''
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid executable product version: $version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive = Join-Path $output "$Name-$version-win-x64.zip"
$temporary = Join-Path $output ('.archive-' + [guid]::NewGuid().ToString('N') + '.zip')
# Explicit inputs prevent stale DLLs, SDKs and earlier archives entering a release.
$files = @($exe) + @($NoticePath | ForEach-Object { Get-Item -LiteralPath $_ })
$names = @{}
foreach ($file in $files) {
    if ($file.PSIsContainer -or $names.ContainsKey($file.Name)) { throw "Invalid or duplicate archive entry: $($file.Name)" }
    $names[$file.Name] = $true
}
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8NoBOM
    Write-Output $archive
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
