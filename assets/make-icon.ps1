# Draws Lumen's icon (gradient tile + magnifier) at several sizes and writes a PNG-compressed .ico.
param([Parameter(Mandatory)][string]$Path)
Add-Type -AssemblyName System.Drawing

function New-LumenBitmap([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $w = [single]($s - 0.5); $d = [single]([Math]::Max(2, $s * 0.44))
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($w - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($s, $s),
        [System.Drawing.Color]::FromArgb(255, 0x8E, 0x5B, 0xFF), [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF))
    $g.FillPath($grad, $tile)

    $penW = [single]([Math]::Max(1.5, $s * 0.095))
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cx = $s * 0.445; $cy = $s * 0.445; $r = $s * 0.205
    $g.DrawEllipse($pen, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
    $g.DrawLine($pen, [single]($cx + $r * 0.74), [single]($cy + $r * 0.74), [single]($s * 0.745), [single]($s * 0.745))
    if ($s -ge 32) {
        # small sparkle inside the lens
        $sp = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $k = $s * 0.055
        $g.FillEllipse($sp, [single]($cx - $r * 0.42 - $k / 2), [single]($cy - $r * 0.42 - $k / 2), [single]$k, [single]$k)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-LumenBitmap $s
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Path, $out.ToArray())
"Wrote $Path"
