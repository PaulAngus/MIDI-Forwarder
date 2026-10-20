[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Identifier,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('arm64','x64')][string]$Architecture = 'arm64',
    [string]$Icon,
    [string]$SigningIdentity = '-',
    [string]$NotaryProfile
)
$ErrorActionPreference = 'Stop'
if ($NotaryProfile -and ($SigningIdentity -eq '-' -or -not $IsMacOS)) {
    throw 'Notarization requires a Developer ID identity on the signing Mac.'
}
$release = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ($Version + '-osx-' + $Architecture + '-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $release ($Name + '.app')
$binary = Join-Path $app 'Contents/MacOS'
$resources = Join-Path $app 'Contents/Resources'
New-Item -ItemType Directory -Path $binary,$resources -Force | Out-Null
function Checked([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed ($LASTEXITCODE)." }
}
# Keep intermediates and restored packages independent of the checkout and
# release paths, which can exceed Windows path limits during cross-builds.
$buildRoot = (Get-Item -LiteralPath ([IO.Path]::GetTempPath())).FullName
$build = Join-Path $buildRoot ('mbu-mac-' + [guid]::NewGuid().ToString('N'))
# NuGet's cache is reusable: build-task processes can retain locks on its DLLs
# after dotnet exits. Only the per-publish intermediates are removed below.
$packages = Join-Path $buildRoot 'mbu-mac-packages'
New-Item -ItemType Directory -Path $build | Out-Null
try {
    Checked 'dotnet' @('publish', $Project, '-c','Release','-r',"osx-$Architecture",'--self-contained','true',
        '--artifacts-path',$build,'-o',$binary,'-p:PublishSingleFile=false',
        "-p:RestorePackagesPath=$packages",
        '-p:EnableCompressionInSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false')
}
finally {
    $buildDirectory = Get-Item -LiteralPath $build -Force
    if ($buildDirectory.Parent.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) -ne
            $buildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -or
        ($buildDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove an unexpected build directory: $($buildDirectory.FullName)"
    }
    Remove-Item -LiteralPath $buildDirectory.FullName -Recurse -Force
}
foreach ($required in @($Executable, "$Executable.runtimeconfig.json", 'libcoreclr.dylib',
    'libAvaloniaNative.dylib', 'libSkiaSharp.dylib', 'libHarfBuzzSharp.dylib',
    'Melanchall_DryWetMidi_Native64.dylib')) {
    if (-not (Test-Path -LiteralPath (Join-Path $binary $required))) { throw "Incomplete Mac payload: $required" }
}
if ($Executable.StartsWith('MidiForwarder.') -and -not (Test-Path (Join-Path $binary 'librtmidi.7.dylib'))) {
    throw 'Forwarder is missing its private RtMidi library.'
}
$values = [ordered]@{
    CFBundleExecutable=$Executable; CFBundleName=$Name; CFBundleDisplayName=$Name
    CFBundleIdentifier=$Identifier; CFBundlePackageType='APPL'
    CFBundleVersion=$Version; CFBundleShortVersionString=$Version; LSMinimumSystemVersion='26.0'
    NSLocalNetworkUsageDescription='Connect to MIDI devices and MIDI Forwarder on your local network.'
}
if ($Icon) { Copy-Item -LiteralPath $Icon -Destination (Join-Path $resources 'AppIcon.icns'); $values.CFBundleIconFile='AppIcon.icns' }
$body = ($values.GetEnumerator() | ForEach-Object { '<key>' + $_.Key + '</key><string>' + [Security.SecurityElement]::Escape($_.Value) + '</string>' }) -join "`n"
Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8 -Value ('<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict>' + $body + '<key>NSHighResolutionCapable</key><true/><key>LSAppNapIsDisabled</key><true/></dict></plist>')
$projectDirectory = Split-Path ([IO.Path]::GetFullPath($Project)) -Parent
# Notices shipped with this pinned helper, independently of the developer's checkout.
Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Name -Match 'LICENSE|NOTICES' | Copy-Item -Destination $resources
if (-not $IsMacOS) {
    Set-Content -LiteralPath (Join-Path $release 'SIGNING-REQUIRED.txt') -Value 'Signing input only. Sign on the development Mac before distribution. End users must not run setup/signing scripts.'
    Write-Output $app
    return
}
$entitlements = Join-Path $release 'entitlements.plist'
$signOptions = if ($SigningIdentity -eq '-') { @() } else { @('--options','runtime') }
Set-Content $entitlements '<?xml version="1.0"?><plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>'
$managed = Join-Path $resources 'Managed'
New-Item -ItemType Directory -Path $managed | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $binary -File) {
    $type = & /usr/bin/file -b $file.FullName
    if ($type -match 'Mach-O') {
        $slices = & /usr/bin/lipo -archs $file.FullName
        $expected = if ($Architecture -eq 'x64') { 'x86_64' } else { 'arm64' }
        if ($LASTEXITCODE -ne 0 -or $expected -notin $slices.Trim().Split(' ')) { throw "Missing $expected slice: $($file.Name)" }
        $imports = & /usr/bin/otool -L $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect native dependencies: $($file.Name)" }
        $installIds = @(& /usr/bin/otool -D $file.FullName | Select-Object -Skip 1 | ForEach-Object { $_.Trim() })
        # Universal binaries have a heading for every architecture. Only the
        # indented records describe libraries; headings contain the binary path.
        foreach ($line in $imports | Where-Object { $_ -match '^\s+\S' }) {
            $dependency = ($line.Trim() -split '\s+\(')[0]
            # A dylib's own install ID is not a dependency; inspect load commands
            # separately with audit_native.py before approving the release.
            if ($dependency.StartsWith('/') -and
                -not $dependency.StartsWith('/System/Library/') -and
                -not $dependency.StartsWith('/usr/lib/') -and
                $dependency -notin $installIds) {
                throw "External native dependency: $dependency"
            }
        }
        Checked '/bin/chmod' @('+x',$file.FullName)
        if ($file.Name -ne $Executable) {
            Checked '/usr/bin/codesign' (@('--force','--sign',$SigningIdentity,'--entitlements',$entitlements,$file.FullName) + $signOptions)
            Checked '/usr/bin/codesign' @('--verify','--strict',$file.FullName)
        }
        if ($file.Name -ne $Executable) { Checked '/bin/ln' @('-s',('../../MacOS/' + $file.Name),(Join-Path $managed $file.Name)) }
    } else {
        Move-Item -LiteralPath $file.FullName -Destination $managed
        Checked '/bin/ln' @('-s',('../Resources/Managed/' + $file.Name),$file.FullName)
    }
}
foreach ($directory in Get-ChildItem -LiteralPath $binary -Directory) {
    Move-Item -LiteralPath $directory.FullName -Destination $managed
    Checked '/bin/ln' @('-s',('../Resources/Managed/' + $directory.Name),$directory.FullName)
}
Checked '/usr/bin/codesign' (@('--force','--sign',$SigningIdentity,'--entitlements',$entitlements,$app) + $signOptions)
Checked '/usr/bin/codesign' @('--verify','--deep','--strict',$app)
$archive = Join-Path $release ($Name.Replace(' ','-') + "-$Version-osx-$Architecture.zip")
Checked '/usr/bin/ditto' @('-c','-k','--keepParent',$app,$archive)
if ($NotaryProfile) {
    Checked '/usr/bin/xcrun' @('notarytool','submit',$archive,'--keychain-profile',$NotaryProfile,'--wait')
    Checked '/usr/bin/xcrun' @('stapler','staple',$app)
    Checked '/usr/bin/xcrun' @('stapler','validate',$app)
    Checked '/usr/sbin/spctl' @('--assess','--type','execute',$app)
    Checked '/usr/bin/ditto' @('-c','-k','--keepParent',$app,$archive)
}
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8NoBOM
Write-Output $archive
if ($SigningIdentity -eq '-') { Write-Warning 'Ad hoc lab build; public distribution still requires Developer ID signing and notarization.' }
