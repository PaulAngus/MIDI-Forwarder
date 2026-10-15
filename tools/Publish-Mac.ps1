param([switch]$SkipBuild, [switch]$ClientOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot 'publish/macos-arm64'
$bundleRoot = Join-Path $outputRoot 'MIDI Forwarder Apple Silicon'
$roles = if ($ClientOnly) { @('Client') } else { @('Client', 'Server') }

& (Join-Path $PSScriptRoot 'Get-MacMidiDependency.ps1')
if (-not $SkipBuild) {
    foreach ($role in $roles) {
        $project = Join-Path $repoRoot "src/MidiForwarder.$role.Mac/MidiForwarder.$role.Mac.csproj"
        dotnet publish $project -c Release --configfile (Join-Path $repoRoot 'NuGet.Config')
        if ($LASTEXITCODE -ne 0) {
            throw "The macOS $role build failed."
        }
    }
}

New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
foreach ($role in $roles) {
    $publishFolder = Join-Path $outputRoot $role.ToLowerInvariant()
    $appFolder = Join-Path $bundleRoot "MIDI Forwarder $role.app"
    $contentsFolder = Join-Path $appFolder 'Contents'
    $binaryFolder = Join-Path $contentsFolder 'MacOS'
    $resourceFolder = Join-Path $contentsFolder 'Resources'
    New-Item -ItemType Directory -Path $binaryFolder, $resourceFolder -Force | Out-Null
    foreach ($fileName in @("MidiForwarder.$role", 'libAvaloniaNative.dylib', 'libHarfBuzzSharp.dylib', 'libSkiaSharp.dylib', 'librtmidi.7.dylib')) {
        Copy-Item -LiteralPath (Join-Path $publishFolder $fileName) -Destination $binaryFolder -Force
    }
    foreach ($nativeLibrary in Get-ChildItem -LiteralPath $publishFolder -Filter '*.dylib') {
        Copy-Item -LiteralPath $nativeLibrary.FullName -Destination $binaryFolder -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'assets/midi-forwarder.icns') -Destination $resourceFolder -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $resourceFolder -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'local-packages/rtmidi/6.0.0/osx-arm64/LICENSE') -Destination (Join-Path $resourceFolder 'RtMidi-LICENSE') -Force

    $plist = [System.Xml.Linq.XElement]::new('plist', [System.Xml.Linq.XAttribute]::new('version', '1.0'))
    $dictionary = [System.Xml.Linq.XElement]::new([System.Xml.Linq.XName]::Get('dict'))
    $properties = [ordered]@{
        CFBundleExecutable = "MidiForwarder.$role"
        CFBundleIconFile = 'midi-forwarder'
        CFBundleName = "MIDI Fwd $role"
        CFBundleDisplayName = "MIDI Forwarder $role"
        CFBundleIdentifier = "org.midiforwarder.$($role.ToLowerInvariant())"
        CFBundlePackageType = 'APPL'
        CFBundleVersion = '1.0.0'
        CFBundleShortVersionString = '1.0.0'
        LSMinimumSystemVersion = '15.0'
    }
    foreach ($property in $properties.GetEnumerator()) {
        $dictionary.Add([System.Xml.Linq.XElement]::new('key', $property.Key))
        $dictionary.Add([System.Xml.Linq.XElement]::new('string', $property.Value))
    }
    $dictionary.Add([System.Xml.Linq.XElement]::new('key', 'NSHighResolutionCapable'))
    $dictionary.Add([System.Xml.Linq.XElement]::new([System.Xml.Linq.XName]::Get('true')))
    # Menu-bar relays with hidden windows must not be throttled by App Nap while forwarding MIDI.
    $dictionary.Add([System.Xml.Linq.XElement]::new('key', 'LSAppNapIsDisabled'))
    $dictionary.Add([System.Xml.Linq.XElement]::new([System.Xml.Linq.XName]::Get('true')))
    $plist.Add($dictionary)
    $document = [System.Xml.Linq.XDocument]::new([System.Xml.Linq.XDeclaration]::new('1.0', 'UTF-8', $null), $plist)
    $document.Save((Join-Path $contentsFolder 'Info.plist'))
}

$prepareScript = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Prepare-Mac-Apps.command')).Replace("`r`n", "`n")
[System.IO.File]::WriteAllText((Join-Path $bundleRoot 'Prepare-Mac-Apps.command'), $prepareScript, [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mac-README.txt') -Destination (Join-Path $bundleRoot 'README.txt') -Force
& (Join-Path $PSScriptRoot 'Verify-MacPackage.ps1') -Roles $roles

# Tar preserves Unix executable permissions when this packaging script runs on Windows.
Add-Type -AssemblyName System.Formats.Tar
$archive = Join-Path $outputRoot 'MIDI-Forwarder-macos-arm64.tar.gz'
$fileStream = [System.IO.File]::Create($archive)
$gzip = [System.IO.Compression.GZipStream]::new($fileStream, [System.IO.Compression.CompressionLevel]::Optimal)
$writer = [System.Formats.Tar.TarWriter]::new($gzip, $true)
try {
    foreach ($file in Get-ChildItem -LiteralPath $bundleRoot -Recurse -File) {
        $relativePath = [System.IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relativePath)
        $executable = $file.Name -eq 'Prepare-Mac-Apps.command' -or ($file.Directory.Name -eq 'MacOS' -and $file.Extension -ne '.dylib')
        $entry.Mode = [System.IO.UnixFileMode]$(if ($executable) { 493 } else { 420 })
        $entry.DataStream = [System.IO.File]::OpenRead($file.FullName)
        try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
    }
} finally {
    $writer.Dispose()
    $gzip.Dispose()
    $fileStream.Dispose()
}
Write-Host "Packaged Apple Silicon apps: $archive"

if ($IsMacOS) {
    $destinations = @(
        (Join-Path $HOME 'Applications'),
        '/Applications'
    )
    foreach ($role in $roles) {
        $appFolder = Join-Path $bundleRoot "MIDI Forwarder $role.app"
        $executable = Join-Path $appFolder "Contents/MacOS/MidiForwarder.$role"
        & /bin/chmod +x $executable
        if ($LASTEXITCODE -ne 0) {
            throw "Could not mark the macOS $role executable as executable."
        }
        & /usr/bin/codesign --force --deep --sign - $appFolder
        if ($LASTEXITCODE -ne 0) {
            throw "Could not ad-hoc sign the macOS $role app."
        }
        & /usr/bin/codesign --verify --deep --strict $appFolder
        if ($LASTEXITCODE -ne 0) {
            throw "The macOS $role app signature is invalid."
        }

        foreach ($destination in $destinations) {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            $installedApp = Join-Path $destination "MIDI Forwarder $role.app"
            & /usr/bin/ditto $appFolder $installedApp
            if ($LASTEXITCODE -ne 0) {
                throw "Could not install the macOS $role app to $destination."
            }
            Write-Host "Installed: $installedApp"
        }
    }
}
