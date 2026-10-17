[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [System.Drawing.Bitmap]$Bitmap
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('MidiForwarder.Branding.LogoSourceBounds' -as [type])) {
    $drawingReferences = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Color].Assembly.Location)
    # Newer PowerShell/.NET versions split GDI+ interfaces into these assemblies.
    foreach ($assemblyName in @('System.Private.Windows.GdiPlus.dll', 'System.Private.Windows.Core.dll')) {
        $assemblyPath = Join-Path $PSHOME $assemblyName
        if (Test-Path -LiteralPath $assemblyPath) { $drawingReferences += $assemblyPath }
    }
    Add-Type -ReferencedAssemblies ($drawingReferences | Select-Object -Unique) -TypeDefinition @'
using System;
using System.Drawing;

namespace MidiForwarder.Branding
{
    public static class LogoSourceBounds
    {
        public static Rectangle GetRectangle(Bitmap bitmap)
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    // Ignore the near-transparent shadow when sizing the visible mark.
                    if (bitmap.GetPixel(x, y).A < 8) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < left) throw new ArgumentException("The logo has no visible artwork.");

            // Center the original artwork in a square with 2% padding on every side.
            int side = (int)Math.Ceiling(Math.Max(right - left + 1, bottom - top + 1) / 0.96);
            int x0 = (int)Math.Floor((left + right + 1 - side) / 2.0);
            int y0 = (int)Math.Floor((top + bottom + 1 - side) / 2.0);
            return new Rectangle(x0, y0, side, side);
        }
    }
}
'@
}

[MidiForwarder.Branding.LogoSourceBounds]::GetRectangle($Bitmap)
