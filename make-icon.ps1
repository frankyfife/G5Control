param([string]$Out = "$PSScriptRoot\app.ico", [string]$Preview)
# Draws the G5Control icon (TV on a blue-violet squircle) at several sizes and packs them into one .ico.
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 64.0, $size / 64.0)

    # Background squircle with diagonal gradient
    $bg = New-RoundedRect 1 1 62 62 15
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 64, 64),
        ([System.Drawing.Color]::FromArgb(255, 14, 165, 233)), ([System.Drawing.Color]::FromArgb(255, 109, 40, 217))
    $g.FillPath($grad, $bg)

    # Soft highlight on the upper half for depth
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, 66),
        ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hl, $bg)

    # Screen: white frame with a slightly tinted panel
    $white = [System.Drawing.Color]::White
    $screen = New-RoundedRect 11 15 42 27 4
    $panel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 255, 255, 255))
    $g.FillPath($panel, $screen)
    $stroke = if ($size -le 20) { 5.5 } else { 4.5 }
    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $screen)

    # Stand
    $standPen = New-Object System.Drawing.Pen $white, $stroke
    $standPen.StartCap = 'Round'; $standPen.EndCap = 'Round'
    $g.DrawLine($standPen, 32, 44, 32, 48)
    $g.DrawLine($standPen, 24, 50, 40, 50)

    # Small glare stroke on the panel (only where there is room for it)
    if ($size -ge 32) {
        $glare = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 255, 255, 255)), 2.5
        $glare.StartCap = 'Round'; $glare.EndCap = 'Round'
        $g.DrawLine($glare, 17, 33, 23, 23)
        $g.DrawLine($glare, 22, 35, 25, 30)
    }
    $g.Dispose()
    $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed frames
$ico = New-Object System.IO.MemoryStream
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $ico.ToArray())
"geschrieben: $Out ($($ico.Length) Bytes, Größen $($sizes -join ', '))"

if ($Preview) {
    # Side-by-side preview of each size (scaled up 4x, nearest neighbour) on dark and light backgrounds
    $show = 16, 24, 32, 64
    $sheet = New-Object System.Drawing.Bitmap 1100, 560
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 0, 1100, 280)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), 0, 280, 1100, 280)
    $x = 20
    foreach ($s in $show) {
        $b = New-IconBitmap $s
        $scale = [math]::Floor(256 / $s); if ($s -eq 64) { $scale = 4 }
        foreach ($y in 12, 292) { $g.DrawImage($b, $x, $y, $s * $scale, $s * $scale) }
        $x += $s * $scale + 20
        $b.Dispose()
    }
    $sheet.Save($Preview)
    "Vorschau: $Preview"
}
