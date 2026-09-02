# Generates src/WinDrop.App/appicon.ico.
#
# Deliberately not a copy of Apple's AirDrop mark, which is a specific set of concentric
# arcs behind a triangle. This is a generic upward arrow on the same blue-violet gradient
# the app uses for peer avatars, so the icon matches the UI without borrowing an identity.
#
# Written as a script rather than committing an opaque binary: the icon can be regenerated
# and reviewed as code.
#
# Sizes below 256 are stored as DIB (the classic BITMAPINFOHEADER + XOR bits + AND mask),
# not PNG. Modern Windows accepts PNG at any size, but older tooling — including
# System.Drawing.Icon, which is how you would verify the result — cannot read PNG entries
# at all. 256 stays PNG because a DIB at that size costs about 260 KB on its own.

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Gradient disc, matching the avatar fill in App.xaml.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 0x5E, 0x5C, 0xE6),
        [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillEllipse($brush, 0, 0, $size - 1, $size - 1)

    # Upward arrow: shaft plus head, sized as fractions so it scales cleanly.
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $cx = $size / 2.0
    $shaftW = [Math]::Max(2.0, $size * 0.14)
    $g.FillRectangle($white, [float]($cx - $shaftW / 2), [float]($size * 0.42), [float]$shaftW, [float]($size * 0.32))

    $headHalf = $size * 0.22
    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]$cx, [float]($size * 0.22))),
        (New-Object System.Drawing.PointF([float]($cx - $headHalf), [float]($size * 0.48))),
        (New-Object System.Drawing.PointF([float]($cx + $headHalf), [float]($size * 0.48)))
    )
    $g.FillPolygon($white, $points)

    $white.Dispose(); $brush.Dispose(); $g.Dispose()
    return ,$bmp
}

function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($stream)

    # BITMAPINFOHEADER. Height is doubled: the format expects the colour bits and the
    # AND mask stacked, and declares their combined height here.
    $w.Write([UInt32]40)
    $w.Write([Int32]$size)
    $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]0)          # BI_RGB
    $w.Write([UInt32]0)          # size of image, may be zero for BI_RGB
    0..3 | ForEach-Object { $w.Write([UInt32]0) }

    # Colour bits, bottom-up, BGRA.
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([Byte]$c.B); $w.Write([Byte]$c.G); $w.Write([Byte]$c.R); $w.Write([Byte]$c.A)
        }
    }

    # AND mask: zeroed, since the alpha channel already carries transparency. Rows are
    # padded to a 4-byte boundary.
    $stride = [Math]::Floor(($size + 31) / 32) * 4
    $blank = New-Object Byte[] $stride
    for ($y = 0; $y -lt $size; $y++) { $w.Write($blank) }

    $w.Flush()
    $bytes = $stream.ToArray()
    $w.Dispose(); $stream.Dispose()
    # Comma operator: without it PowerShell unrolls the array on output and the caller
    # receives a stream of bytes rather than a byte[], which BinaryWriter then mangles.
    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @{}

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size

    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries[$size] = $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $entries[$size] = ConvertTo-Dib $bmp
    }

    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

$writer.Write([UInt16]0)              # reserved
$writer.Write([UInt16]1)              # type: icon
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)

foreach ($size in $sizes) {
    $data = $entries[$size]
    $dim = if ($size -ge 256) { 0 } else { $size }   # 256 is encoded as 0

    $writer.Write([Byte]$dim)
    $writer.Write([Byte]$dim)
    $writer.Write([Byte]0)            # palette entries
    $writer.Write([Byte]0)            # reserved
    $writer.Write([UInt16]1)          # colour planes
    $writer.Write([UInt16]32)         # bits per pixel
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($size in $sizes) { $writer.Write($entries[$size]) }

$writer.Flush()
$target = Join-Path $PSScriptRoot "..\src\WinDrop.App\appicon.ico"
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose(); $out.Dispose()

Write-Output "wrote $((Resolve-Path $target).Path) ($($sizes.Count) sizes, $((Get-Item $target).Length) bytes)"
