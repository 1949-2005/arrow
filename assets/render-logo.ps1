# Renders the logo (assets/logo.svg's drawing) into every file that carries it: assets/logo.png (512), assets/logo.ico
# (16-256, PNG images) and extension/icons/icon-16|32|48|128.png. Each size is drawn on its own, not scaled from a big
# one: below 48 px the lines get thicker so the paper plane keeps its fold. Run after changing the logo:
#   powershell -ExecutionPolicy Bypass -File assets\render-logo.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$graphite = [Drawing.Color]::FromArgb(0x3B, 0x3C, 0x3D)

# The plane in logo.svg's 512 space: outline, fold, and the shaded lower half
$outline = @(@(71.5, 87.5), @(437.5, 255.5), @(71.5, 423.5))
$fold = @(@(71.5, 87.5), @(183.5, 255.5))
$lower = @(@(183.5, 255.5), @(437.5, 255.5), @(71.5, 423.5))
# logo.svg's transform: the plane at half size, its box centred a little left of the tile's middle (it points right).
# Up to 24 px it is a little smaller (0.44): its corners would run into the tile's edge.
function Points($list, $k, $scale) {
    $tx = 124 - 254.5 * $scale; $ty = 128 - 255.5 * $scale
    [Drawing.PointF[]]($list | ForEach-Object { New-Object Drawing.PointF ((($_[0] * $scale) + $tx) * $k), ((($_[1] * $scale) + $ty) * $k) })
}

function Render([int]$px) {
    $bmp = New-Object Drawing.Bitmap $px, $px, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $px / 256.0
    # the white rounded tile and its faint edge (1 px at every size)
    $r = 58 * $k; $e = 0.5; $w = $px - 1
    $tile = New-Object Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($e, $e, 2 * $r, 2 * $r, 180, 90); $tile.AddArc($w - $e - 2 * $r + 0.5, $e, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($w - $e - 2 * $r + 0.5, $w - $e - 2 * $r + 0.5, 2 * $r, 2 * $r, 0, 90); $tile.AddArc($e, $w - $e - 2 * $r + 0.5, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath([Drawing.Brushes]::White, $tile)
    $edge = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($(if ($px -le 32) { 46 } else { 36 }), 0, 0, 0)), 1
    $g.DrawPath($edge, $tile)
    # the plane: logo.svg's 16 (8 at half size) in tile units, thicker below 48 px
    $stroke = if ($px -le 24) { 0.5 + 0.05 * $px } elseif ($px -le 32) { 1.0 + 0.04 * $px } else { [Math]::Max(1.9, 8 * $k) }
    $pen = New-Object Drawing.Pen $graphite, $stroke
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $brush = New-Object Drawing.SolidBrush $graphite
    $sc = if ($px -le 24) { 0.44 } else { 0.5 }
    $g.FillPolygon($brush, (Points $lower $k $sc))
    $g.DrawPolygon($pen, (Points $lower $k $sc))
    $g.DrawPolygon($pen, (Points $outline $k $sc))
    $g.DrawLines($pen, (Points $fold $k $sc))
    $g.Dispose()
    return $bmp
}

function PngBytes($bmp) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return , $ms.ToArray() } # the comma: one byte[], not its bytes

# logo.png and the extension's icons
(Render 512).Save((Join-Path $root 'assets\logo.png'), [Drawing.Imaging.ImageFormat]::Png)
foreach ($px in 16, 32, 48, 128) { (Render $px).Save((Join-Path $root "extension\icons\icon-$px.png"), [Drawing.Imaging.ImageFormat]::Png) }

# logo.ico: every size Windows asks for at 100-200 % scaling, each a PNG image
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = $sizes | ForEach-Object { , (PngBytes (Render $_)) }
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
[IO.File]::WriteAllBytes((Join-Path $root 'assets\logo.ico'), $ms.ToArray())
"rendered: logo.png, logo.ico ($($sizes -join ', ')), extension icons"
