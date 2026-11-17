# Draws Squeaker's icon (a pointer with a little squeak) and writes src\Squeaker\app.ico. Run again only to change the icon.
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path -Parent $PSScriptRoot) "src\Squeaker\app.ico"

function Draw([int]$n) {
    $bmp = New-Object Drawing.Bitmap $n, $n
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $n / 64.0
    $w = $n - 1; $d = 30 * $s
    $box = New-Object Drawing.Drawing2D.GraphicsPath
    $box.AddArc(0, 0, $d, $d, 180, 90); $box.AddArc($w - $d, 0, $d, $d, 270, 90)
    $box.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $box.AddArc(0, $w - $d, $d, $d, 90, 90); $box.CloseFigure()
    $fill = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $n, $n), ([Drawing.Color]::FromArgb(255, 159, 10)), ([Drawing.Color]::FromArgb(255, 45, 110))
    $g.FillPath($fill, $box)
    # the pointer
    $pts = @(@(19, 11), @(19, 49), @(28.5, 40), @(34.5, 53), @(41, 50), @(35, 37.5), @(47.5, 37.5)) | ForEach-Object { New-Object Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $arrow = New-Object Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon([Drawing.PointF[]]$pts)
    $shadow = New-Object Drawing.Drawing2D.Matrix; $shadow.Translate(1.2 * $s, 1.8 * $s)
    $sh = $arrow.Clone(); $sh.Transform($shadow)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 90, 0, 30))), $sh)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(58, 12, 30)), ([math]::Max(1, 2.6 * $s)); $pen.LineJoin = 'Round'
    $g.FillPath([Drawing.Brushes]::White, $arrow)
    $g.DrawPath($pen, $arrow)
    # the squeak
    if ($n -ge 32) {
        $arc = New-Object Drawing.Pen ([Drawing.Color]::White), ([math]::Max(1.5, 3 * $s)); $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
        $g.DrawArc($arc, 39 * $s, 10 * $s, 12 * $s, 12 * $s, -80, 75)
        $g.DrawArc($arc, 35 * $s, 4 * $s, 22 * $s, 22 * $s, -80, 75)
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = $sizes | ForEach-Object { , (Draw $_) }
$file = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $file
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write([byte]($sizes[$i] % 256)); $bw.Write([byte]($sizes[$i] % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
[IO.File]::WriteAllBytes($out, $file.ToArray())
"Wrote $out"
