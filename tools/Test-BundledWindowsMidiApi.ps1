[CmdletBinding()]
param(
    [string]$ClientExe = (Join-Path $PSScriptRoot '../publish/client/MidiForwarder.Client.exe'),
    [string]$ServerExe = (Join-Path $PSScriptRoot '../publish/server/MidiForwarder.Server.exe'),
    [switch]$CheckVirtualPorts
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Version -lt [version]'10.0.26200') { throw 'This verification requires Windows 11 25H2 or later with Windows MIDI Services.' }
$taskRoot = Join-Path $PSScriptRoot ('../artifacts/bundled-api-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$taskRoot = (Resolve-Path -LiteralPath $taskRoot).Path

function Invoke-StandaloneProbe([string]$Executable, [string]$Argument) {
    $taskInfo = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $taskInfo.ArgumentList.Add($Argument)
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskInfo.RedirectStandardOutput = $true
    $taskInfo.RedirectStandardError = $true
    $taskInfo.WorkingDirectory = [System.IO.Path]::GetTempPath()
    $taskInfo.Environment.Remove('MBU_MIDI_API_DIRECTORY') | Out-Null
    $taskInfo.Environment.Remove('MBU_MIDI_BACKEND') | Out-Null
    $taskInfo.Environment.Remove('MIDI_FORWARDER_MIDI_API') | Out-Null
    $taskInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $taskRoot 'bundle-cache'
    $taskProcess = [System.Diagnostics.Process]::Start($taskInfo)
    try {
        $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskError = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(35000)) { $taskProcess.Kill(); throw "Probe timed out: $Argument" }
        $taskText = $taskOut.GetAwaiter().GetResult().Trim()
        $taskErrorText = $taskError.GetAwaiter().GetResult().Trim()
        if ($taskProcess.ExitCode -ne 0) { throw "Probe failed ($Argument): $taskText $taskErrorText" }
        return $taskText
    } finally { $taskProcess.Dispose() }
}

foreach ($taskSource in @($ClientExe, $ServerExe)) {
    $taskDirectory = Join-Path $taskRoot ([System.IO.Path]::GetFileNameWithoutExtension($taskSource))
    New-Item -ItemType Directory -Path $taskDirectory | Out-Null
    $taskExe = Join-Path $taskDirectory ([System.IO.Path]::GetFileName($taskSource))
    Copy-Item -LiteralPath $taskSource -Destination $taskExe
    # Only the EXE is copied. A new .NET bundle cache and an unrelated working directory are used.
    $taskApi = Invoke-StandaloneProbe $taskExe '--midi-api-directory'
    $taskExpectedRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MIDI Forwarder/NativeApi'
    if (-not ([System.IO.Path]::GetFullPath($taskApi)).StartsWith(([System.IO.Path]::GetFullPath($taskExpectedRoot) + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The EXE is not using its own extracted API: $taskApi"
    }
    foreach ($taskFile in @('Windows.Devices.Midi2.dll','Windows.Devices.Midi2.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskApi $taskFile))) { throw "Bundled file is missing: $taskFile" }
    }
    $taskBackend = Invoke-StandaloneProbe $taskExe '--midi-backend'
    if ($taskBackend -notmatch 'Windows MIDI Services \(virtual loopback available\)') { throw "Current API probe failed: $taskBackend" }
    Write-Output "$([System.IO.Path]::GetFileName($taskExe)): $taskBackend"
    Write-Output "  Bundled API: $taskApi"
    if ($CheckVirtualPorts -and $taskSource -eq $ClientExe) {
        Write-Output (Invoke-StandaloneProbe $taskExe '--check-virtual-midi')
    }
}
Write-Output "Standalone verification passed. Evidence directory: $taskRoot"
