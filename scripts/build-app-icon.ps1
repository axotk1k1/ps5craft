# Regenerates Assets\app.ico (multi-size adaptive Windows icon) from repo-root ps5icon.png.
# Sizes <= 48 use a top crop (cube/console only); larger sizes use the full logo.
param(
    [string]$Source = "$PSScriptRoot\..\ps5icon.png",
    [string]$OutIco = "$PSScriptRoot\..\src\PS5Craft.App\Assets\app.ico",
    [string]$OutPng = "$PSScriptRoot\..\src\PS5Craft.App\Assets\ps5icon.png"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$assetsDir = Split-Path $OutIco -Parent
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
Copy-Item -LiteralPath $Source -Destination $OutPng -Force

function New-HighQualityBitmap([System.Drawing.Bitmap]$src, [int]$size, [bool]$graphicOnly) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    if ($graphicOnly) {
        $cropH = [int]($src.Height * 0.58)
        $cropY = [int]($src.Height * 0.02)
        $cropW = [Math]::Min($src.Width, $cropH)
        $cropX = [int](($src.Width - $cropW) / 2)
        $srcRect = New-Object System.Drawing.Rectangle $cropX, $cropY, $cropW, $cropH
        $dstRect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    } else {
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    }
    $g.Dispose()
    return $bmp
}

$src = New-Object System.Drawing.Bitmap $Source
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-HighQualityBitmap $src $s ($s -le 48)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}
$src.Dispose()

$fs = [IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($dim); $bw.Write($dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); $fs.Close()
Write-Host "Wrote $OutIco ($((Get-Item $OutIco).Length) bytes)"
