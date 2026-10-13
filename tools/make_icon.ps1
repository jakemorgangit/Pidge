<#
  Draws the Pidge app icon (a pigeon on the brand gradient) and writes
  src/Pidge/Assets/Pidge.ico (16-256 px) and Pidge.png (256 px).
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$assets = Join-Path $PSScriptRoot "..\src\Pidge\Assets"

function Draw([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $n / 256.0
    $g.ScaleTransform($s, $s)

    # Rounded-square background with the brand gradient.
    $r = 56
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $bg.AddArc(255 - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $bg.AddArc(255 - $r * 2, 255 - $r * 2, $r * 2, $r * 2, 0, 90); $bg.AddArc(0, 255 - $r * 2, $r * 2, $r * 2, 90, 90); $bg.CloseFigure()
    $c1 = [System.Drawing.Color]::FromArgb(124, 108, 255); $c2 = [System.Drawing.Color]::FromArgb(76, 201, 240)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 256, 256), $c1, $c2)
    $g.FillPath($grad, $bg)

    $white = [System.Drawing.Brushes]::White
    $P = { param($x, $y) New-Object System.Drawing.PointF ([single]$x), ([single]$y) }

    # Body: puffed chest on the right, long tapering tail down to the left.
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddBezier((& $P 24 150), (& $P 60 118), (& $P 108 104), (& $P 150 106))
    $body.AddBezier((& $P 150 106), (& $P 190 108), (& $P 206 138), (& $P 200 162))
    $body.AddBezier((& $P 200 162), (& $P 192 190), (& $P 156 200), (& $P 118 196))
    $body.AddBezier((& $P 118 196), (& $P 86 192), (& $P 58 176), (& $P 24 150))
    $body.CloseFigure()
    $g.FillPath($white, $body)

    # Small head held forward on a thick neck.
    $neck = New-Object System.Drawing.Drawing2D.GraphicsPath
    $neck.AddPolygon([System.Drawing.PointF[]]@((& $P 150 108), (& $P 166 74), (& $P 196 80), (& $P 202 140)))
    $g.FillPath($white, $neck)
    $g.FillEllipse($white, 160, 52, 46, 46)

    # Iridescent neck band, the pigeon giveaway.
    $band = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 72, 214, 178)), 9
    $band.StartCap = 'Round'; $band.EndCap = 'Round'
    $g.DrawBezier($band, (& $P 168 96), (& $P 178 104), (& $P 192 104), (& $P 202 98))

    # Beak.
    $beak = New-Object System.Drawing.Drawing2D.GraphicsPath
    $beak.AddPolygon([System.Drawing.PointF[]]@((& $P 202 68), (& $P 226 78), (& $P 202 84)))
    $g.FillPath($white, $beak)

    # Eye and wing in the background colour.
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 120, 230))
    $g.FillEllipse($ink, 182, 66, 11, 11)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 124, 140, 236)), 9
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawBezier($pen, (& $P 62 142), (& $P 96 128), (& $P 132 136), (& $P 160 164))
    $g.DrawBezier($pen, (& $P 88 160), (& $P 110 156), (& $P 128 162), (& $P 142 176))

    # Little feet.
    $feet = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 7
    $feet.StartCap = 'Round'; $feet.EndCap = 'Round'
    $g.DrawLine($feet, 124, 196, 118, 218)
    $g.DrawLine($feet, 150, 194, 152, 218)

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = $sizes | ForEach-Object { , (Draw $_) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $w.Write([byte]($sz % 256)); $w.Write([byte]($sz % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($b in $images) { $w.Write($b) }
[IO.File]::WriteAllBytes((Join-Path $assets "Pidge.ico"), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets "Pidge.png"), (Draw 256))
"Wrote Pidge.ico and Pidge.png"
