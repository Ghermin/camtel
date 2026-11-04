# Generates the download page's social preview image (docs/og.png).
# Usage: powershell -File scripts/gen-web-images.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$docs = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs'

# The icons themselves (SVG, PNG, ICO, Android) come from the playground:
# bored-dev-playground/brand/icons/camtel.svg, then "node build.js camtel" in bored-dev-playground/brand.
$icon = [System.Drawing.Image]::FromFile((Join-Path $docs 'icon-512.png'))
function Draw-Logo([System.Drawing.Graphics]$g, [float]$x, [float]$y, [float]$size) {
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($icon, $x, $y, $size, $size)
}

# Social preview (1200x630).
$bmp = New-Object System.Drawing.Bitmap 1200, 630
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(16, 19, 27))
Draw-Logo $g 110 175 280
$title = New-Object System.Drawing.Font 'Segoe UI Semibold', 88, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$sub = New-Object System.Drawing.Font 'Segoe UI', 40, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('CamTel', $title, [System.Drawing.Brushes]::White, 450, 190)
$phone = 'Ton t' + [char]0xE9 + 'l' + [char]0xE9 + 'phone devient'  # ASCII-only source: PowerShell 5 reads it as ANSI
$g.DrawString($phone, $sub, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 180, 200))), 456, 315)
$g.DrawString('ta webcam Windows', $sub, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(91, 155, 243))), 456, 368)
$bmp.Save((Join-Path $docs 'og.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

$icon.Dispose()
Get-ChildItem $docs | Select-Object Name, Length
