param([string]$ProjectRoot = (Join-Path $PSScriptRoot '..\src\TaskbarLyrics.App'))

Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path $ProjectRoot 'Assets\Icons'

function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Max(1, $radius * 2)
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-RoundedRect($graphics, $brush, [float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = New-RoundedPath $x $y $width $height $radius
    $graphics.FillPath($brush, $path)
    $path.Dispose()
}

function New-BrandPng([int]$size, [bool]$compact) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([Drawing.Color]::Transparent)
    $scale = $size / 256.0
    $plate = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 27, 36, 50))
    $bar = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 67, 81, 103))
    $muted = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 123, 139, 164))
    $accent = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 117, 167, 255))
    $note = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 255, 212, 81))
    Add-RoundedRect $graphics $plate (16*$scale) (16*$scale) (224*$scale) (224*$scale) (60*$scale)
    Add-RoundedRect $graphics $bar (48*$scale) (176*$scale) (160*$scale) (32*$scale) (16*$scale)
    if (-not $compact) {
        Add-RoundedRect $graphics $muted (56*$scale) (80*$scale) (136*$scale) (16*$scale) (8*$scale)
        Add-RoundedRect $graphics $muted (56*$scale) (120*$scale) (96*$scale) (16*$scale) (8*$scale)
    }
    Add-RoundedRect $graphics $accent (56*$scale) (80*$scale) (72*$scale) (16*$scale) (8*$scale)
    $graphics.FillEllipse($note, 155*$scale, 78*$scale, 42*$scale, 36*$scale)
    Add-RoundedRect $graphics $note (193*$scale) (45*$scale) (11*$scale) (53*$scale) (2*$scale)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    $plate.Dispose(); $bar.Dispose(); $muted.Dispose(); $accent.Dispose(); $note.Dispose()
    return ,$bytes
}

function Write-Ico([string]$path, [int[]]$sizes, [bool]$compactSmall) {
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        [void]$images.Add((New-BrandPng $size ($compactSmall -and $size -le 24)))
    }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Dispose(); $stream.Dispose()
}

[IO.File]::WriteAllBytes((Join-Path $iconDirectory 'logo-256.png'), (New-BrandPng 256 $false))
Write-Ico (Join-Path $iconDirectory 'app.ico') @(16,20,24,32,40,48,64,128,256) $false
Write-Ico (Join-Path $iconDirectory 'tray.ico') @(16,20,24,32,40,48) $true
Write-Output "Generated TaskbarLyrics brand assets in $iconDirectory"
