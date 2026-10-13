param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DiaryOfMine.Desktop\Assets'),
    [string]$SourceImage = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DiaryOfMine.Desktop\Assets\DiaryOfMine.jpg')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param(
        [System.Drawing.Image]$Source,
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $cropSize = [Math]::Min($Source.Width, $Source.Height)
        $cropX = [int](($Source.Width - $cropSize) / 2)
        $cropY = [int](($Source.Height - $cropSize) / 2)
        $sourceRect = [System.Drawing.Rectangle]::new($cropX, $cropY, $cropSize, $cropSize)
        $targetRect = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
        $graphics.DrawImage($Source, $targetRect, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)

        return $bitmap
    }
    finally {
        $graphics.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $SourceImage)) {
    throw "Source image not found: $SourceImage"
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$source = [System.Drawing.Image]::FromFile($SourceImage)
try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $entries = @()
    foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $source $size
        try {
            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $entries += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
            }
            finally {
                $stream.Dispose()
            }

            if ($size -eq 256) {
                $bitmap.Save((Join-Path $OutputDirectory 'DiaryOfMine.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

$iconPath = Join-Path $OutputDirectory 'DiaryOfMine.ico'
$fileStream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -eq 256) { [byte]0 } else { [byte]$entry.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Bytes) }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output $iconPath
