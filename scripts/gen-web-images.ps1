# Generates the download page's PNG icons and social preview image (docs/).
# Usage: powershell -File scripts/gen-web-images.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$docs = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs'
$blue = [System.Drawing.Color]::FromArgb(47, 128, 237)

function Draw-Logo([System.Drawing.Graphics]$g, [float]$x, [float]$y, [float]$size) {
    $u = $size / 108.0
    $g.SmoothingMode = 'AntiAlias'
    $r = 24 * $u; $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $size - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $size - $d, $y + $size - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $blue), $path)
    $white = [System.Drawing.Brushes]::White
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddRectangle((New-Object System.Drawing.RectangleF ($x + 30 * $u), ($y + 40 * $u), (36 * $u), (28 * $u)))
    $g.FillPath($white, $body)
    $lens = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($x + 64 * $u), ($y + 48 * $u)),
        (New-Object System.Drawing.PointF ($x + 76 * $u), ($y + 42 * $u)),
        (New-Object System.Drawing.PointF ($x + 76 * $u), ($y + 66 * $u)),
        (New-Object System.Drawing.PointF ($x + 64 * $u), ($y + 60 * $u)))
    $g.FillPolygon($white, $lens)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $blue), ($x + 41 * $u), ($y + 47 * $u), (14 * $u), (14 * $u))
}

function Save-Icon([int]$size, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Draw-Logo $g 0 0 $size
    $bmp.Save((Join-Path $docs $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

Save-Icon 180 'apple-touch-icon.png'
Save-Icon 192 'icon-192.png'
Save-Icon 512 'icon-512.png'

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

Copy-Item (Join-Path (Split-Path -Parent $PSScriptRoot) 'windows\CamTel\camtel.ico') (Join-Path $docs 'favicon.ico') -Force
Get-ChildItem $docs | Select-Object Name, Length
