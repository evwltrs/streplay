# Draws thunderstore/icon.png (256x256, as Thunderstore requires). Run: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'

# background: dark diagonal gradient
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size), ([System.Drawing.Color]::FromArgb(255, 24, 20, 34)), ([System.Drawing.Color]::FromArgb(255, 10, 9, 14))
$g.FillRectangle($bg, 0, 0, $size, $size)

$accent = [System.Drawing.Color]::FromArgb(255, 255, 43, 214)
$accentSoft = [System.Drawing.Color]::FromArgb(70, 255, 43, 214)

# slanted accent band behind the mark
$band = New-Object System.Drawing.Drawing2D.GraphicsPath
$band.AddPolygon([System.Drawing.Point[]]@((New-Object System.Drawing.Point 0, 150), (New-Object System.Drawing.Point 256, 96), (New-Object System.Drawing.Point 256, 132), (New-Object System.Drawing.Point 0, 186)))
$g.FillPath((New-Object System.Drawing.SolidBrush $accentSoft), $band)

# rewind/play mark: a circular arrow around a play triangle
$pen = New-Object System.Drawing.Pen $accent, 14
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'
$g.DrawArc($pen, 58, 34, 140, 140, -60, 300)
# arrow head at the start of the arc
$ax = 128 + 70 * [Math]::Cos([Math]::PI * -60 / 180); $ay = 104 + 70 * [Math]::Sin([Math]::PI * -60 / 180)
$head = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF ($ax + 22), ($ay - 4)),
    (New-Object System.Drawing.PointF ($ax - 6), ($ay - 22)),
    (New-Object System.Drawing.PointF ($ax - 2), ($ay + 14)))
$g.FillPolygon((New-Object System.Drawing.SolidBrush $accent), $head)
# play triangle
$tri = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF 110, 72),
    (New-Object System.Drawing.PointF 110, 136),
    (New-Object System.Drawing.PointF 162, 104))
$g.FillPolygon([System.Drawing.Brushes]::White, $tri)

# REC dot
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 70, 70))), 206, 20, 26, 26)

# wordmark
$font = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'
$g.DrawString('STREPLAY', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 196, $size, 44), $fmt)

$out = Join-Path $PSScriptRoot '..\thunderstore\icon.png'
$bmp.Save((Resolve-Path (Split-Path $out)).Path + '\icon.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "wrote $out"
