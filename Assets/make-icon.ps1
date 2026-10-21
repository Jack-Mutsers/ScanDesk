# Generates ScanDesk.ico (16-256 px) next to this script.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    function RR($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Max(1, $r * 2)
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); return $p
    }
    # Blue rounded tile
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillPath($bg, (RR (8 * $k) (8 * $k) (240 * $k) (240 * $k) (52 * $k)))
    # White sheet of paper
    $white = [System.Drawing.Brushes]::White
    $g.FillPath($white, (RR (64 * $k) (40 * $k) (128 * $k) (166 * $k) (12 * $k)))
    # Text lines on paper
    $line = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(191, 219, 254))
    if ($s -ge 32) {
        foreach ($y in 72, 98, 154, 180) { $g.FillRectangle($line, 86 * $k, $y * $k, 84 * $k, 10 * $k) }
    }
    # Scan beam
    $beam = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 211, 238))
    $g.FillPath($beam, (RR (40 * $k) (120 * $k) (176 * $k) ([Math]::Max(2, 18 * $k)) (9 * $k)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'ScanDesk.ico'), $out.ToArray())
