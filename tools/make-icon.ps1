# Regenerates assets\ClaudeBackdrop.ico (PNG-compressed sizes 16-256).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 32.0
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $r = 12 * $s; $o = 1 * $s; $e = 31 * $s
    $path.AddArc($o, $o, $r, $r, 180, 90)
    $path.AddArc($e - $r, $o, $r, $r, 270, 90)
    $path.AddArc($e - $r, $e - $r, $r, $r, 0, 90)
    $path.AddArc($o, $e - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $fill = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(0, 0, $size, $size),
        [Drawing.Color]::FromArgb(0xE8, 0x8A, 0x63), [Drawing.Color]::FromArgb(0x7A, 0x5C, 0xD6), 45)
    $g.FillPath($fill, $path)
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, [Math]::Max(1.2, 2.6 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $c = 16 * $s
    for ($i = 0; $i -lt 8; $i++) {
        $a = $i * [Math]::PI / 4
        $g.DrawLine($pen, $c + [Math]::Cos($a) * 4 * $s, $c + [Math]::Sin($a) * 4 * $s, $c + [Math]::Cos($a) * 10 * $s, $c + [Math]::Sin($a) * 10 * $s)
    }
    $g.Dispose()
    $ms = [IO.MemoryStream]::new()
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($size in $sizes) { , (Draw $size) }
$out = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
$target = Join-Path $PSScriptRoot '..\assets\ClaudeBackdrop.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[IO.File]::WriteAllBytes($target, $out.ToArray())
Get-Item $target
