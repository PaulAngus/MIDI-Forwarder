[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssets = Join-Path $PSScriptRoot '../assets'
$taskSourcePath = Join-Path $PSScriptRoot 'Website/midi-forwarder-logo.png'
$taskSource = [System.Drawing.Bitmap]::new($taskSourcePath)
$taskStream = [System.IO.MemoryStream]::new()
$taskWriter = [System.IO.BinaryWriter]::new($taskStream)
function Write-BigEndianUInt32([System.IO.BinaryWriter]$Writer, [uint32]$Value) {
    $taskBytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) { [array]::Reverse($taskBytes) }
    $Writer.Write([byte[]]$taskBytes)
}
try {
    $taskSourceRectangle = & (Join-Path $PSScriptRoot 'Get-LogoSourceRectangle.ps1') -Bitmap $taskSource
    $taskFrames = @()
    foreach ($taskEntry in @(@('icp4',16),@('icp5',32),@('icp6',64),@('ic07',128),@('ic08',256),@('ic09',512),@('ic10',1024))) {
        $taskSize = [int]$taskEntry[1]
        $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
        $taskFrameStream = [System.IO.MemoryStream]::new()
        try {
            $taskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskGraphics.DrawImage($taskSource, [System.Drawing.Rectangle]::new(0,0,$taskSize,$taskSize), $taskSourceRectangle.X, $taskSourceRectangle.Y, $taskSourceRectangle.Width, $taskSourceRectangle.Height, [System.Drawing.GraphicsUnit]::Pixel)
            $taskBitmap.Save($taskFrameStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $taskFrames += [pscustomobject]@{ Type = $taskEntry[0]; Bytes = $taskFrameStream.ToArray() }
        } finally { $taskFrameStream.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
    }
    $taskLength = 8
    foreach ($taskFrame in $taskFrames) { $taskLength += 8 + $taskFrame.Bytes.Length }
    $taskWriter.Write([System.Text.Encoding]::ASCII.GetBytes('icns'))
    Write-BigEndianUInt32 $taskWriter ([uint32]$taskLength)
    foreach ($taskFrame in $taskFrames) {
        $taskWriter.Write([System.Text.Encoding]::ASCII.GetBytes($taskFrame.Type))
        Write-BigEndianUInt32 $taskWriter ([uint32](8 + $taskFrame.Bytes.Length))
        $taskWriter.Write([byte[]]$taskFrame.Bytes)
    }
    $taskWriter.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'midi-forwarder.icns'), $taskStream.ToArray())
} finally { $taskWriter.Dispose(); $taskStream.Dispose(); $taskSource.Dispose() }
Write-Output 'Exported MIDI Forwarder macOS icon (16 through 1024 pixels).'

