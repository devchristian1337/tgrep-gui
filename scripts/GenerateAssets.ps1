param(
    [string]$Source = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectDirectory = Split-Path $PSScriptRoot
$assetsDirectory = Join-Path $projectDirectory 'Assets'
New-Item -ItemType Directory -Force $assetsDirectory | Out-Null

if (-not $Source) {
    $png = Join-Path $assetsDirectory 'icon.png'
    $jpg = Join-Path $assetsDirectory 'icon.jpg'
    if (Test-Path -LiteralPath $png) { $Source = $png }
    elseif (Test-Path -LiteralPath $jpg) { $Source = $jpg }
    else { throw 'Icon source not found. Pass -Source <path>.' }
}
if (-not (Test-Path -LiteralPath $Source)) { throw "Icon source not found: $Source" }

$sourceFull = (Resolve-Path -LiteralPath $Source).Path
$extension = [IO.Path]::GetExtension($sourceFull).ToLowerInvariant()
if ($extension -eq '.jpeg') { $extension = '.jpg' }
if ($extension -notin @('.png', '.jpg', '.bmp', '.gif')) { $extension = '.png' }
$projectSource = Join-Path $assetsDirectory ('icon' + $extension)
if (-not (Test-Path -LiteralPath $projectSource) -or ((Resolve-Path -LiteralPath $projectSource).Path -ne $sourceFull)) {
    Copy-Item -LiteralPath $Source -Destination $projectSource -Force
}

function Get-ArgbBytes([System.Drawing.Bitmap]$bitmap) {
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes = New-Object byte[] ($data.Stride * $bitmap.Height)
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        return @{ Bytes = $bytes; Data = $data; Stride = $data.Stride }
    }
    catch {
        $bitmap.UnlockBits($data)
        throw
    }
}

function Remove-Checkerboard([System.Drawing.Bitmap]$bitmap) {
    $locked = Get-ArgbBytes $bitmap
    $bytes = $locked.Bytes
    $stride = $locked.Stride
    for ($i = 0; $i -lt $bytes.Length; $i += 4) {
        $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
        $max = [Math]::Max($r, [Math]::Max($g, $b))
        $min = [Math]::Min($r, [Math]::Min($g, $b))
        $sat = $max - $min
        $luma = [int]((0.299 * $r) + (0.587 * $g) + (0.114 * $b))
        if ($luma -ge 205 -and $sat -le 20) {
            $bytes[$i] = 0; $bytes[$i + 1] = 0; $bytes[$i + 2] = 0; $bytes[$i + 3] = 0
        }
        elseif ($luma -ge 185 -and $sat -le 30) {
            $t = ($luma - 185) / 70.0
            $alpha = [byte][Math]::Max(0, [Math]::Min(255, [int]((1 - $t) * 220)))
            $bytes[$i + 3] = $alpha
            if ($alpha -eq 0) { $bytes[$i] = 0; $bytes[$i + 1] = 0; $bytes[$i + 2] = 0 }
        }
    }
    [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $locked.Data.Scan0, $bytes.Length)
    $bitmap.UnlockBits($locked.Data)
}

function Get-ContentSquare([System.Drawing.Bitmap]$bitmap) {
    $locked = Get-ArgbBytes $bitmap
    $bytes = $locked.Bytes
    $stride = $locked.Stride
    $minX = $bitmap.Width; $minY = $bitmap.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            if ($bytes[$row + ($x * 4) + 3] -lt 16) { continue }
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    $bitmap.UnlockBits($locked.Data)
    if ($maxX -lt 0) { return $bitmap.Clone([System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) }
    $expand = [Math]::Max(6, [int]([Math]::Max($maxX - $minX + 1, $maxY - $minY + 1) * 0.03))
    $minX = [Math]::Max(0, $minX - $expand)
    $minY = [Math]::Max(0, $minY - $expand)
    $maxX = [Math]::Min($bitmap.Width - 1, $maxX + $expand)
    $maxY = [Math]::Min($bitmap.Height - 1, $maxY + $expand)
    $width = $maxX - $minX + 1
    $height = $maxY - $minY + 1
    $side = [Math]::Max($width, $height)
    $pad = [Math]::Max(12, [int]($side * 0.12))
    $side = $side + (2 * $pad)
    $square = [System.Drawing.Bitmap]::new($side, $side, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($square)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage(
        $bitmap,
        [System.Drawing.Rectangle]::new([int](($side - $width) / 2), [int](($side - $height) / 2), $width, $height),
        [System.Drawing.Rectangle]::new($minX, $minY, $width, $height),
        [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    return $square
}

function New-ScaledBitmap([System.Drawing.Bitmap]$source, [int]$size) {
    $scaled = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($scaled)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $graphics.Dispose()
    return $scaled
}

function Save-Icon([string]$path, [System.Drawing.Bitmap[]]$images) {
    $payloads = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($image in $images) {
        $stream = New-Object System.IO.MemoryStream
        $image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        [void]$payloads.Add($stream.ToArray())
        $stream.Dispose()
    }
    $header = New-Object 'System.Collections.Generic.List[byte]'
    function Add-UInt16([uint16]$value) { $header.Add($value -band 255); $header.Add(($value -shr 8) -band 255) }
    function Add-UInt32([uint32]$value) {
        $header.Add($value -band 255)
        $header.Add(($value -shr 8) -band 255)
        $header.Add(($value -shr 16) -band 255)
        $header.Add(($value -shr 24) -band 255)
    }
    Add-UInt16 0
    Add-UInt16 1
    Add-UInt16 ([uint16]$payloads.Count)
    $offset = 6 + (16 * $payloads.Count)
    for ($i = 0; $i -lt $payloads.Count; $i++) {
        $width = $images[$i].Width
        $height = $images[$i].Height
        $header.Add([byte]$(if ($width -ge 256) { 0 } else { $width }))
        $header.Add([byte]$(if ($height -ge 256) { 0 } else { $height }))
        $header.Add(0)
        $header.Add(0)
        Add-UInt16 1
        Add-UInt16 32
        Add-UInt32 ([uint32]$payloads[$i].Length)
        Add-UInt32 ([uint32]$offset)
        $offset += $payloads[$i].Length
    }
    $stream = [System.IO.File]::Create($path)
    try {
        $headerBytes = $header.ToArray()
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        foreach ($payload in $payloads) { $stream.Write($payload, 0, $payload.Length) }
    }
    finally { $stream.Dispose() }
}

function Test-HasTransparency([System.Drawing.Bitmap]$bitmap) {
    foreach ($point in @(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($bitmap.Width - 1, 0),
        [System.Drawing.Point]::new(0, $bitmap.Height - 1),
        [System.Drawing.Point]::new($bitmap.Width - 1, $bitmap.Height - 1)
    )) {
        if ($bitmap.GetPixel($point.X, $point.Y).A -lt 16) { return $true }
    }
    return $false
}

$sourceBytes = [IO.File]::ReadAllBytes($projectSource)
$sourceStream = New-Object IO.MemoryStream(,$sourceBytes)
$original = [System.Drawing.Bitmap]::FromStream($sourceStream)
$keyed = $original.Clone([System.Drawing.Rectangle]::new(0, 0, $original.Width, $original.Height), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$original.Dispose()
$sourceStream.Dispose()
if (-not (Test-HasTransparency $keyed)) { Remove-Checkerboard $keyed }
$square = Get-ContentSquare $keyed
$keyed.Dispose()

foreach ($asset in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
    $scaled = New-ScaledBitmap $square ([int]$asset[1])
    $scaled.Save((Join-Path $assetsDirectory ($asset[0] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $scaled.Dispose()
}

$iconImages = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) { $iconImages += New-ScaledBitmap $square $size }
Save-Icon (Join-Path $assetsDirectory 'AppIcon.ico') $iconImages
foreach ($image in $iconImages) { $image.Dispose() }
$square.Dispose()
