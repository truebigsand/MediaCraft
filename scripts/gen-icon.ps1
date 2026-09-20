# gen-icon.ps1 -- generates Assets/app.ico (multi-size, 32bpp DIB entries) + scripts/icon-preview.png
# ASCII ONLY: Windows PowerShell 5.1 reads BOM-less UTF-8 as GBK and would corrupt non-ASCII text.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src\MediaCraft\Assets'
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir | Out-Null }
$icoPath = Join-Path $assetsDir 'app.ico'
$previewPath = Join-Path $PSScriptRoot 'icon-preview.png'

# Brand colour: #2E7D32 (same accent as the UI)
$brandColor = [System.Drawing.Color]::FromArgb(255, 46, 125, 50)

function New-RoundedRectPath {
    param([single]$x, [single]$y, [single]$w, [single]$h, [single]$r)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$size)
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset = [single]([Math]::Max(0, [int]($size * 0.03)))
        $side = [single]($size - 2 * $inset)
        $radius = [single]($size * 0.22)
        $path = New-RoundedRectPath -x $inset -y $inset -w $side -h $side -r $radius
        $brush = New-Object System.Drawing.SolidBrush($brandColor)
        try { $g.FillPath($brush, $path) } finally { $brush.Dispose(); $path.Dispose() }

        $fontSize = [single]($size * 0.60)
        $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $format = New-Object System.Drawing.StringFormat
        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        try {
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $format.LineAlignment = [System.Drawing.StringAlignment]::Center
            $rectF = New-Object System.Drawing.RectangleF(0, 0, [single]$size, [single]$size)
            $g.DrawString('M', $font, $textBrush, $rectF, $format)
        } finally {
            $font.Dispose(); $format.Dispose(); $textBrush.Dispose()
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$bmp)
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buffer = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
    } finally {
        $bmp.UnlockBits($data)
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER: biHeight is doubled (XOR bitmap + AND mask)
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    # XOR bitmap: bottom-up BGRA rows
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($buffer, ($y * $stride), ($w * 4))
    }
    # AND mask: all zero (32bpp alpha channel decides transparency), rows padded to 4 bytes
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $h)
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    # Unary comma: without it PowerShell unrolls the byte[] into individual bytes
    return ,$ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -size $size
    try {
        $entries += [pscustomobject]@{ Size = $size; Bytes = (Get-DibBytes -bmp $bmp) }
        if ($size -eq 256) { $bmp.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    } finally {
        $bmp.Dispose()
    }
}

$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([int16]0)                  # reserved
    $writer.Write([int16]1)                  # type: icon
    $writer.Write([int16]$entries.Count)     # image count
    $offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $dim = 0
        if ($entry.Size -lt 256) { $dim = $entry.Size }
        $writer.Write([byte]$dim)            # width  (0 = 256)
        $writer.Write([byte]$dim)            # height (0 = 256)
        $writer.Write([byte]0)               # palette count
        $writer.Write([byte]0)               # reserved
        $writer.Write([int16]1)              # planes
        $writer.Write([int16]32)             # bit count
        $writer.Write([int]$entry.Bytes.Length)
        $writer.Write([int]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) {
        # Explicit cast: the binder must pick BinaryWriter.Write(byte[]), not a scalar overload
        $writer.Write([byte[]]$entry.Bytes)
    }
    $writer.Flush()
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

# Validate by reading it back
$icon = New-Object System.Drawing.Icon($icoPath)
$check = "OK size=$($icon.Width)x$($icon.Height)"
$icon.Dispose()
Write-Output "generated: $icoPath ($((Get-Item $icoPath).Length) bytes) $check"
Write-Output "preview  : $previewPath"
