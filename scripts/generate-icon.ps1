<#
.SYNOPSIS
    Erzeugt assets/icon.ico und die PNG-Varianten. Die Geometrie entspricht assets/icon.svg.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\assets' | Resolve-Path
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-IconPng([int] $size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)

    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(8, 8, 104, 104, 180, 90)
    $path.AddArc(144, 8, 104, 104, 270, 90)
    $path.AddArc(144, 144, 104, 104, 0, 90)
    $path.AddArc(8, 144, 104, 104, 90, 90)
    $path.CloseFigure()
    $fill = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.Point]::new(8, 8), [Drawing.Point]::new(248, 248),
        [Drawing.ColorTranslator]::FromHtml('#3B8EFF'), [Drawing.ColorTranslator]::FromHtml('#1B47B5'))
    $graphics.FillPath($fill, $path)

    $pen = [Drawing.Pen]::new([Drawing.Color]::White, 26)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    [Drawing.Point[]] $prompt = @([Drawing.Point]::new(52, 88), [Drawing.Point]::new(108, 128), [Drawing.Point]::new(52, 168))
    $graphics.DrawLines($pen, $prompt)

    $bolt = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FFC83D'))
    [Drawing.Point[]] $boltPoints = @(
        [Drawing.Point]::new(178, 44), [Drawing.Point]::new(122, 138), [Drawing.Point]::new(164, 138),
        [Drawing.Point]::new(142, 212), [Drawing.Point]::new(212, 110), [Drawing.Point]::new(168, 110))
    $graphics.FillPolygon($bolt, $boltPoints)
    $graphics.Dispose()

    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$images = foreach ($size in $sizes) { [pscustomobject]@{ Size = $size; Data = (New-IconPng $size) } }

$icon = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'icon.ico')))
$icon.Write([uint16] 0); $icon.Write([uint16] 1); $icon.Write([uint16] $images.Count)
$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $icon.Write([byte] $dimension); $icon.Write([byte] $dimension)
    $icon.Write([byte] 0); $icon.Write([byte] 0)
    $icon.Write([uint16] 1); $icon.Write([uint16] 32)
    $icon.Write([uint32] $image.Data.Length); $icon.Write([uint32] $offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $icon.Write($image.Data) }
$icon.Dispose()

foreach ($image in $images | Where-Object Size -In 128, 256) {
    [IO.File]::WriteAllBytes((Join-Path $assets "icon-$($image.Size).png"), $image.Data)
}
