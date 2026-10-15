[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskSourcePath = Join-Path $PSScriptRoot 'Website/midi-forwarder-logo.png'
$taskAppAssets = Join-Path $PSScriptRoot '../assets'
$taskWebsiteAssets = Join-Path $PSScriptRoot 'Website'
New-Item -ItemType Directory -Path $taskAppAssets, $taskWebsiteAssets -Force | Out-Null

# Format/size exports only: preserve the chosen artwork and its transparent alpha.
$taskSource = [System.Drawing.Bitmap]::new($taskSourcePath)
try {
    $taskFrames = @()
    foreach ($taskSize in @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512)) {
        $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
        $taskStream = [System.IO.MemoryStream]::new()
        try {
            $taskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskAttributes = [System.Drawing.Imaging.ImageAttributes]::new()
            try {
                $taskAttributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                $taskGraphics.DrawImage($taskSource, [System.Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize), 0, 0, $taskSource.Width, $taskSource.Height, [System.Drawing.GraphicsUnit]::Pixel, $taskAttributes)
            } finally { $taskAttributes.Dispose() }
            $taskBitmap.Save($taskStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $taskBytes = $taskStream.ToArray()
            [System.IO.File]::WriteAllBytes((Join-Path $taskWebsiteAssets "midi-forwarder-$taskSize.png"), $taskBytes)
            if ($taskSize -eq 256) {
                [System.IO.File]::WriteAllBytes((Join-Path $taskAppAssets 'midi-forwarder-256.png'), $taskBytes)
            }
            if ($taskSize -le 256) { $taskFrames += [pscustomobject]@{ Size = $taskSize; Bytes = $taskBytes } }
        } finally {
            $taskGraphics.Dispose()
            $taskBitmap.Dispose()
            $taskStream.Dispose()
        }
    }

    $taskIconStream = [System.IO.MemoryStream]::new()
    $taskWriter = [System.IO.BinaryWriter]::new($taskIconStream)
    try {
        $taskWriter.Write([uint16]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]$taskFrames.Count)
        $taskOffset = 6 + 16 * $taskFrames.Count
        foreach ($taskFrame in $taskFrames) {
            $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1)
            $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskFrame.Bytes.Length)
            $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskFrame.Bytes.Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Bytes) }
        $taskWriter.Flush()
        [System.IO.File]::WriteAllBytes((Join-Path $taskAppAssets 'midi-forwarder.ico'), $taskIconStream.ToArray())
        [System.IO.File]::WriteAllBytes((Join-Path $taskWebsiteAssets 'favicon.ico'), $taskIconStream.ToArray())
    } finally {
        $taskWriter.Dispose()
        $taskIconStream.Dispose()
    }
} finally { $taskSource.Dispose() }

Write-Output 'Exported MIDI Forwarder app icon and website logo assets.'

& (Join-Path $PSScriptRoot 'Export-MacIcon.ps1')

