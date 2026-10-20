#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [switch]$Sign,
    [string]$SigningCertificateThumbprint = $env:MBU_WINDOWS_SIGNING_THUMBPRINT,
    [string]$SignToolPath = $env:MBU_SIGNTOOL_PATH,
    [uri]$TimestampServer = 'http://time.certum.pl',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Sign -and [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    if ($CheckOnly) { Write-Host 'Windows signing is not configured; producing an unsigned development build.' }
    return
}
if (-not $IsWindows) { throw 'Windows signing requires Windows and the certificate/token software on the signing PC.' }
$thumbprint = ($SigningCertificateThumbprint -replace '[\s\u200e\u200f]', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'Signing requires an exact 40-character certificate thumbprint. Set MBU_WINDOWS_SIGNING_THUMBPRINT or pass -SigningCertificateThumbprint.'
}
if (-not $TimestampServer.IsAbsoluteUri -or $TimestampServer.Scheme -notin @('http', 'https')) {
    throw 'TimestampServer must be an absolute HTTP or HTTPS RFC 3161 timestamp URL.'
}
try { $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" }
catch { throw "Signing certificate $thumbprint was not found in CurrentUser\My. Activate/register the certificate using the card software first." }
if (-not $certificate.HasPrivateKey) { throw 'The selected certificate has no associated private key. Connect/register the signing card using its software.' }
$now = Get-Date
if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) { throw 'The selected signing certificate is not currently valid.' }
if (@($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
    throw 'The selected certificate does not permit code signing.'
}

if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
    $command = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { $SignToolPath = $command.Source }
    else {
        $sdkRoots = @()
        $installed = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
        if ($installed -and $installed.PSObject.Properties['KitsRoot10']) { $sdkRoots += $installed.KitsRoot10 }
        if (${env:ProgramFiles(x86)}) { $sdkRoots += Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10' }
        foreach ($sdkRoot in ($sdkRoots | Select-Object -Unique)) {
            $bin = Join-Path $sdkRoot 'bin'
            if (-not (Test-Path -LiteralPath $bin -PathType Container)) { continue }
            $versions = Get-ChildItem -LiteralPath $bin -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending
            foreach ($version in $versions) {
                $candidate = Join-Path $version.FullName 'x64/signtool.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $SignToolPath = $candidate; break }
            }
            if ($SignToolPath) { break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
    throw 'SignTool was not found. Install the Windows SDK signing tools on the build PC, or set MBU_SIGNTOOL_PATH / -SignToolPath to signtool.exe.'
}
$SignToolPath = (Get-Item -LiteralPath $SignToolPath).FullName
if ($CheckOnly) {
    Write-Host "Windows signing configured: $($certificate.Subject) [$thumbprint]. The card may request its PIN during signing."
    return
}

$executable = Get-Item -LiteralPath $ExecutablePath
if ($executable.PSIsContainer -or $executable.Extension -ne '.exe') { throw 'Expected one published Windows EXE to sign.' }
# Work on a sibling copy: a token, timestamp or trust failure must not replace
# the published executable with a partly signed file. No PIN or private key is read.
$temporary = Join-Path $executable.DirectoryName ('.sign-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    Copy-Item -LiteralPath $executable.FullName -Destination $temporary
    & $SignToolPath sign /s My /sha1 $thumbprint /fd SHA256 /tr $TimestampServer.AbsoluteUri /td SHA256 $temporary
    if ($LASTEXITCODE -ne 0) { throw "SignTool signing/timestamping failed ($LASTEXITCODE). No archive or distribution copy should be made." }
    & $SignToolPath verify /pa /all /tw /v $temporary
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed ($LASTEXITCODE)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $temporary
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw 'The signed EXE must have a trusted signature from the selected certificate and a verified timestamp.'
    }
    Move-Item -LiteralPath $temporary -Destination $executable.FullName -Force
    Write-Host "Signed, timestamped and verified: $($executable.FullName)"
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
