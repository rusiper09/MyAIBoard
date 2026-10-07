# Renders the MyAIBoard icon (dark glass tile with three glowing source-colored bars)
# into a multi-size .ico. Re-run after changing the design:
#   powershell -File tools/make-icon.ps1
param([string]$OutFile = "$PSScriptRoot\..\src\MyAIBoard.App\Assets\MyAIBoard.ico")

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Glass tile: dark gradient body, top sheen.
    $tile = New-RoundedPath (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $size), ([System.Drawing.Color]::FromArgb(255, 58, 62, 74)), ([System.Drawing.Color]::FromArgb(255, 22, 24, 30))
    $g.FillPath($body, $tile)
    $sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, (8 * $s)), (New-Object System.Drawing.PointF 0, (130 * $s)), ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    # Gradient brushes tile past their end point, so clip the sheen to its own band.
    $g.SetClip((New-Object System.Drawing.RectangleF 0, 0, $size, (129 * $s)))
    $g.FillPath($sheen, $tile)
    $g.ResetClip()

    # Three memo bars: Claude coral, Codex mint, user blue. Each gets a soft glow when there is room for one.
    $bars = @(
        @{ Y = 68;  W = 150; Color = [System.Drawing.Color]::FromArgb(255, 240, 153, 122) },
        @{ Y = 116; W = 118; Color = [System.Drawing.Color]::FromArgb(255, 94, 219, 169) },
        @{ Y = 164; W = 134; Color = [System.Drawing.Color]::FromArgb(255, 138, 184, 255) }
    )
    $barHeight = [Math]::Max(2, 26 * $s)
    foreach ($bar in $bars) {
        $x = 53 * $s; $y = $bar.Y * $s; $w = $bar.W * $s
        if ($size -ge 32) {
            foreach ($spread in 10, 6, 3) {
                $glow = New-RoundedPath ($x - $spread * $s) ($y - $spread * $s) ($w + 2 * $spread * $s) ($barHeight + 2 * $spread * $s) (($barHeight / 2) + $spread * $s)
                $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, $bar.Color))), $glow)
            }
        }
        $pill = New-RoundedPath $x $y $w $barHeight ($barHeight / 2)
        $g.FillPath((New-Object System.Drawing.SolidBrush $bar.Color), $pill)
    }

    $g.Dispose()
    return $bmp
}

# 32-bit DIB entry: what System.Drawing.Icon (used by the tray icon) can read at small sizes.
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h), 'ReadOnly', 'Format32bppArgb')
    $pixels = New-Object byte[] ($w * $h * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [Math]::Ceiling($w / 32) * 4
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4 + $maskStride * $h)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($row = $h - 1; $row -ge 0; $row--) { $bw.Write($pixels, $row * $w * 4, $w * 4) } # bottom-up BGRA
    $bw.Write((New-Object byte[] ($maskStride * $h)))                                       # AND mask unused with alpha
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = Render $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = To-Dib $bmp
    }
    $bmp.Dispose()
    , $bytes
}

# ICO container: DIB entries for small sizes, PNG for 256px (supported since Windows Vista).
New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
$out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($OutFile))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32)
    $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $out.Write($img) }
$out.Close()
Write-Host "Wrote $((Resolve-Path $OutFile).Path)"
