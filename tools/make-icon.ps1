# Generates src/Windfall.App/appicon.ico.
#
# The mark is an apple with the wind cut through it: two gusts taken out of the body, so
# wind and fruit are one shape. Deliberately neither Apple's logo (no bite, never a solid
# silhouette) nor its AirDrop mark, which is a specific set of concentric arcs behind a
# triangle.
#
# Written as a script rather than committing an opaque binary: the icon can be regenerated
# and reviewed as code. The numbers below, path data included, are the ones in
# assets/windfall-icon.svg and assets/windfall-favicon.svg, so the two can be compared line
# by line. WPF draws them because its path syntax is SVG's and it can subtract one shape
# from another with clean edges, which System.Drawing cannot.
#
# There are two drawings, as in the design. The master has two gusts and is used from
# 48 px up. Below that its gusts would be about a pixel tall, so the small drawing has one
# gust, cut two pixels tall on whole pixel rows at 16 px, and a stem and leaf roughly twice
# as thick.
#
# Sizes below 256 are stored as DIB (the classic BITMAPINFOHEADER + XOR bits + AND mask),
# not PNG. Modern Windows accepts PNG at any size, but older tooling — including
# System.Drawing.Icon, which is how you would verify the result — cannot read PNG entries
# at all. 256 stays PNG because a DIB at that size costs about 260 KB on its own.

Add-Type -AssemblyName System.Drawing, PresentationCore, WindowsBase

$bodyPath = 'M50 31C45 25 37 23 30 25C19 28 12 38 12 50C12 67 23 87 36 88C42 88.5 46 86 50 86C54 86 58 88.5 64 88C77 87 88 67 88 50C88 38 81 28 70 25C63 23 55 25 50 31Z'

# Each drawing says where the apple sits (SVG: translate(x y) rotate(12) scale(s)
# translate(-50 -48)), the gusts as x1, y1, x2, y2 and their width, the stem as a rounded
# rectangle (x, y, width, height, radius) turned 14 degrees about a pivot, the leaf, and the
# square the drawing fills. The master is placed on its 1024 square with
# translate(-144.5 -186.1) scale(13).
$master = @{
    At = 51, 50, 0.9
    Gusts = @(@(-20, 43, 47, 43), @(-20, 60, 35, 60))
    GustWidth = 8
    Stem = 48, 12, 7, 22, 3.5
    StemPivot = 51.5, 30
    Leaf = 'M53 23C56 13 66 8 78 9C76 19 66 26 53 23Z'
    Zoom = 13
    Shift = -144.5, -186.1
    Square = 1024
}
$small = @{
    At = 8.11, 7.18, 0.2
    Gusts = @(, @(-2, 9, 7, 9))
    GustWidth = 2
    Stem = 46.5, 14, 11, 20, 5.5
    StemPivot = 52, 30
    Leaf = 'M54 24C56 12 66 7 79 8C78 20 68 28 54 24Z'
    Zoom = 1
    Shift = 0, 0
    Square = 16
}

function New-IconBitmap([int]$size) {
    $d = if ($size -lt 48) { $small } else { $master }

    # WPF applies a group's children first to last, which is the reverse of reading an SVG
    # transform attribute left to right.
    $place = New-Object System.Windows.Media.TransformGroup
    $place.Children.Add((New-Object System.Windows.Media.TranslateTransform(-50, -48)))
    $place.Children.Add((New-Object System.Windows.Media.ScaleTransform($d.At[2], $d.At[2])))
    $place.Children.Add((New-Object System.Windows.Media.RotateTransform(12)))
    $place.Children.Add((New-Object System.Windows.Media.TranslateTransform($d.At[0], $d.At[1])))

    $body = [System.Windows.Media.Geometry]::Parse($bodyPath).Clone()
    $body.Transform = $place

    # The gusts are cut out of the body and are not turned with it: the wind stays level.
    $pen = New-Object System.Windows.Media.Pen([System.Windows.Media.Brushes]::Black, $d.GustWidth)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    foreach ($gust in $d.Gusts) {
        $line = New-Object System.Windows.Media.LineGeometry(
            (New-Object System.Windows.Point($gust[0], $gust[1])),
            (New-Object System.Windows.Point($gust[2], $gust[3])))
        $body = [System.Windows.Media.Geometry]::Combine(
            $body, $line.GetWidenedPathGeometry($pen), [System.Windows.Media.GeometryCombineMode]::Exclude, $null)
    }

    $s = $d.Stem
    $stem = New-Object System.Windows.Media.RectangleGeometry(
        (New-Object System.Windows.Rect($s[0], $s[1], $s[2], $s[3])), $s[4], $s[4])
    $tilt = New-Object System.Windows.Media.TransformGroup
    $tilt.Children.Add((New-Object System.Windows.Media.RotateTransform(14, $d.StemPivot[0], $d.StemPivot[1])))
    $tilt.Children.Add($place)
    $stem.Transform = $tilt

    $leaf = [System.Windows.Media.Geometry]::Parse($d.Leaf).Clone()
    $leaf.Transform = $place

    $scale = $size / $d.Square
    $fit = New-Object System.Windows.Media.TransformGroup
    $fit.Children.Add((New-Object System.Windows.Media.ScaleTransform($d.Zoom, $d.Zoom)))
    $fit.Children.Add((New-Object System.Windows.Media.TranslateTransform($d.Shift[0], $d.Shift[1])))
    $fit.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

    $red = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0xBF, 0x2E, 0x2A))
    $green = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x37, 0x72, 0x2F))

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform($fit)
    $dc.DrawGeometry($red, $null, $body)
    $dc.DrawGeometry($green, $null, $stem)
    $dc.DrawGeometry($green, $null, $leaf)
    $dc.Pop()
    $dc.Close()

    $render = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)

    # Handed on as a System.Drawing bitmap, which is what the ICO writer below reads.
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($render))
    $png = New-Object System.IO.MemoryStream
    $encoder.Save($png)
    $decoded = New-Object System.Drawing.Bitmap($png)
    $bmp = New-Object System.Drawing.Bitmap($decoded)   # a copy, so the stream can close
    $decoded.Dispose(); $png.Dispose()
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
$target = Join-Path $PSScriptRoot "..\src\Windfall.App\appicon.ico"
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose(); $out.Dispose()

Write-Output "wrote $((Resolve-Path $target).Path) ($($sizes.Count) sizes, $((Get-Item $target).Length) bytes)"
