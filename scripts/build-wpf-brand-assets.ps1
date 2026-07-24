[CmdletBinding()]
param(
    [string]$ProjectDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) {
    $ProjectDirectory = Join-Path $PSScriptRoot '..\local-native\PhotoViewer.Wpf'
}

$projectRoot = [System.IO.Path]::GetFullPath($ProjectDirectory)
$brandRoot = Join-Path $projectRoot 'Brand'
$outputRoot = Join-Path $brandRoot 'Generated'
$smallMarkSource = Join-Path $brandRoot 'Aibos.Mark.Small.Source.png'
$largeMarkSource = Join-Path $brandRoot 'Aibos.Mark.Large.Source.png'
$largeIconSource = Join-Path $brandRoot 'Aibos.Icon.Large.Source.png'

foreach ($requiredPath in @($smallMarkSource, $largeMarkSource, $largeIconSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required brand source is missing: $requiredPath"
    }
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -le 2) {
                continue
            }

            $minX = [Math]::Min($minX, $x)
            $minY = [Math]::Min($minY, $y)
            $maxX = [Math]::Max($maxX, $x)
            $maxY = [Math]::Max($maxY, $y)
        }
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        throw 'Brand source has no visible pixels.'
    }

    return [System.Drawing.Rectangle]::FromLTRB($minX, $minY, $maxX + 1, $maxY + 1)
}

function Export-FittedPng {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][int]$CanvasSize,
        [Parameter(Mandatory)][int]$Occupancy,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    try {
        $bounds = Get-AlphaBounds -Bitmap $source
        $scale = [Math]::Min(
            [double]$Occupancy / [double]$bounds.Width,
            [double]$Occupancy / [double]$bounds.Height
        )
        $width = [Math]::Max(1, [int][Math]::Round($bounds.Width * $scale))
        $height = [Math]::Max(1, [int][Math]::Round($bounds.Height * $scale))
        $left = [int][Math]::Floor(($CanvasSize - $width) / 2)
        $top = [int][Math]::Floor(($CanvasSize - $height) / 2)

        $canvas = [System.Drawing.Bitmap]::new(
            $CanvasSize,
            $CanvasSize,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
        )
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                $destination = [System.Drawing.Rectangle]::new($left, $top, $width, $height)
                $graphics.DrawImage(
                    $source,
                    $destination,
                    $bounds.X,
                    $bounds.Y,
                    $bounds.Width,
                    $bounds.Height,
                    [System.Drawing.GraphicsUnit]::Pixel
                )
            }
            finally {
                $graphics.Dispose()
            }

            if (Test-Path -LiteralPath $OutputPath) {
                Remove-Item -LiteralPath $OutputPath -Force
            }
            $canvas.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $canvas.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Export-MonochromePng {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][System.Drawing.Color]$Color,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    try {
        $output = [System.Drawing.Bitmap]::new(
            $source.Width,
            $source.Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
        )
        try {
            for ($y = 0; $y -lt $source.Height; $y++) {
                for ($x = 0; $x -lt $source.Width; $x++) {
                    $alpha = $source.GetPixel($x, $y).A
                    $output.SetPixel(
                        $x,
                        $y,
                        [System.Drawing.Color]::FromArgb($alpha, $Color.R, $Color.G, $Color.B)
                    )
                }
            }

            if (Test-Path -LiteralPath $OutputPath) {
                Remove-Item -LiteralPath $OutputPath -Force
            }
            $output.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $output.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Export-PngBackedIco {
    param(
        [Parameter(Mandatory)][string[]]$FramePaths,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $frames = @(
        foreach ($framePath in $FramePaths) {
            $bytes = [System.IO.File]::ReadAllBytes($framePath)
            $image = [System.Drawing.Image]::FromFile($framePath)
            try {
                [pscustomobject]@{
                    Width = $image.Width
                    Height = $image.Height
                    Bytes = $bytes
                }
            }
            finally {
                $image.Dispose()
            }
        }
    )

    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force
    }

    $stream = [System.IO.File]::Open(
        $OutputPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$frames.Count)

            $offset = 6 + (16 * $frames.Count)
            foreach ($frame in $frames) {
                $writer.Write([byte]$(if ($frame.Width -eq 256) { 0 } else { $frame.Width }))
                $writer.Write([byte]$(if ($frame.Height -eq 256) { 0 } else { $frame.Height }))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Bytes.Length)
                $writer.Write([uint32]$offset)
                $offset += $frame.Bytes.Length
            }

            foreach ($frame in $frames) {
                $writer.Write($frame.Bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$markFrames = [ordered]@{
    16 = 13
    20 = 17
    24 = 20
    32 = 26
    40 = 33
    48 = 39
    64 = 52
    96 = 78
    128 = 104
    256 = 208
}

foreach ($entry in $markFrames.GetEnumerator()) {
    $size = [int]$entry.Key
    $source = if ($size -le 24) { $smallMarkSource } else { $largeMarkSource }
    Export-FittedPng `
        -SourcePath $source `
        -CanvasSize $size `
        -Occupancy ([int]$entry.Value) `
        -OutputPath (Join-Path $outputRoot "Aibos.Mark.$size.png")
}

$iconFrames = [ordered]@{
    16 = @{ Source = $smallMarkSource; Occupancy = 13 }
    20 = @{ Source = $smallMarkSource; Occupancy = 17 }
    24 = @{ Source = $smallMarkSource; Occupancy = 20 }
    32 = @{ Source = $largeIconSource; Occupancy = 30 }
    40 = @{ Source = $largeIconSource; Occupancy = 38 }
    48 = @{ Source = $largeIconSource; Occupancy = 46 }
    64 = @{ Source = $largeIconSource; Occupancy = 62 }
    96 = @{ Source = $largeIconSource; Occupancy = 92 }
    128 = @{ Source = $largeIconSource; Occupancy = 124 }
    256 = @{ Source = $largeIconSource; Occupancy = 248 }
}

$iconFramePaths = @()
foreach ($entry in $iconFrames.GetEnumerator()) {
    $size = [int]$entry.Key
    $outputPath = Join-Path $outputRoot "Aibos.Icon.$size.png"
    Export-FittedPng `
        -SourcePath ([string]$entry.Value.Source) `
        -CanvasSize $size `
        -Occupancy ([int]$entry.Value.Occupancy) `
        -OutputPath $outputPath
    $iconFramePaths += $outputPath
}

foreach ($size in @(16, 20, 24, 32, 48, 64)) {
    $markPath = Join-Path $outputRoot "Aibos.Mark.$size.png"
    Export-MonochromePng `
        -SourcePath $markPath `
        -Color ([System.Drawing.Color]::White) `
        -OutputPath (Join-Path $outputRoot "Aibos.Mark.White.$size.png")
    Export-MonochromePng `
        -SourcePath $markPath `
        -Color ([System.Drawing.Color]::Black) `
        -OutputPath (Join-Path $outputRoot "Aibos.Mark.Black.$size.png")
}

$icoPath = Join-Path $outputRoot 'Aibos.App.ico'
Export-PngBackedIco -FramePaths $iconFramePaths -OutputPath $icoPath

$generatedFiles = Get-ChildItem -LiteralPath $outputRoot -File
$payloadBytes = ($generatedFiles | Measure-Object -Property Length -Sum).Sum

[pscustomobject]@{
    OutputDirectory = $outputRoot
    FileCount = $generatedFiles.Count
    PayloadBytes = $payloadBytes
    PayloadKiB = [Math]::Round($payloadBytes / 1KB, 2)
    IconFrames = ($iconFrames.Keys -join ',')
} | Format-List
