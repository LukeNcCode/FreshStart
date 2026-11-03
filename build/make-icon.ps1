param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "src\FreshStart.App\Assets\app.ico"
}

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([System.Drawing.RectangleF]$rect, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $arc = [System.Drawing.RectangleF]::new($rect.X, $rect.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $rect.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $rect.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $rect.X
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $inset = [float]($size * 0.025)
        $innerSize = $size - ($inset * 2)
        $rect = [System.Drawing.RectangleF]::new($inset, $inset, $innerSize, $innerSize)
        $radius = [float]($size * 0.22)
        $path = New-RoundedPath $rect $radius
        try {
            $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                $rect,
                [System.Drawing.Color]::FromArgb(255, 0, 122, 255),
                [System.Drawing.Color]::FromArgb(255, 42, 200, 255),
                45.0)
            try {
                $graphics.FillPath($gradient, $path)
            }
            finally {
                $gradient.Dispose()
            }
        }
        finally {
            $path.Dispose()
        }

        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        try {
            $bars = @(
                @{ X = 0.25; Y = 0.305; W = 0.50; H = 0.085 },
                @{ X = 0.25; Y = 0.500; W = 0.34; H = 0.085 }
            )
            foreach ($bar in $bars) {
                $barRect = [System.Drawing.RectangleF]::new(
                    [float]($size * $bar.X),
                    [float]($size * $bar.Y),
                    [float]($size * $bar.W),
                    [float]($size * $bar.H))
                $barPath = New-RoundedPath $barRect ([float]($size * 0.043))
                try {
                    $graphics.FillPath($white, $barPath)
                }
                finally {
                    $barPath.Dispose()
                }
            }

            $dot = [float]($size * 0.138)
            $graphics.FillEllipse($white, [float]($size * 0.64), [float]($size * 0.64), $dot, $dot)
        }
        finally {
            $white.Dispose()
        }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Data = New-IconPng $size
    }
}

$headerSize = 6 + (16 * $images.Count)
$offset = $headerSize
$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Data.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Data)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $output.ToArray())
}
finally {
    $writer.Dispose()
    $output.Dispose()
}

Write-Output "Wrote $OutputPath"
