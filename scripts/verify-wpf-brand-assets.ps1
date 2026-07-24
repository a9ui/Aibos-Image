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
$generatedRoot = Join-Path $brandRoot 'Generated'
$projectPath = Join-Path $projectRoot 'PhotoViewer.Wpf.csproj'
$windowPath = Join-Path $projectRoot 'MainWindow.xaml'
$icoPath = Join-Path $generatedRoot 'Aibos.App.ico'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -le 8) {
                continue
            }

            $minX = [Math]::Min($minX, $x)
            $minY = [Math]::Min($minY, $y)
            $maxX = [Math]::Max($maxX, $x)
            $maxY = [Math]::Max($maxY, $y)
        }
    }

    Assert-True ($maxX -ge $minX -and $maxY -ge $minY) 'PNG has no visible pixels.'
    return [System.Drawing.Rectangle]::FromLTRB($minX, $minY, $maxX + 1, $maxY + 1)
}

$expectedSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$smallOccupancy = @{
    16 = 13
    20 = 17
    24 = 20
}

foreach ($size in $expectedSizes) {
    foreach ($stem in @('Aibos.Mark', 'Aibos.Icon')) {
        $path = Join-Path $generatedRoot "$stem.$size.png"
        Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing generated PNG: $path"
        $bitmap = [System.Drawing.Bitmap]::FromFile($path)
        try {
            Assert-True ($bitmap.Width -eq $size -and $bitmap.Height -eq $size) `
                "$path is $($bitmap.Width)x$($bitmap.Height), expected ${size}x${size}."
        }
        finally {
            $bitmap.Dispose()
        }
    }
}

foreach ($size in @(16, 20, 24)) {
    $path = Join-Path $generatedRoot "Aibos.Mark.$size.png"
    $bitmap = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $bounds = Get-AlphaBounds -Bitmap $bitmap
        $target = [int]$smallOccupancy[$size]
        Assert-True ($bounds.Width -le $target -and $bounds.Height -le $target) `
            "Small optical master exceeds ${target}px occupancy at ${size}px."
        Assert-True ([Math]::Max($bounds.Width, $bounds.Height) -ge ($target - 1)) `
            "Small optical master under-fills the ${size}px canvas."
    }
    finally {
        $bitmap.Dispose()
    }
}

foreach ($size in @(16, 20, 24, 32, 48, 64)) {
    foreach ($variant in @('White', 'Black')) {
        $path = Join-Path $generatedRoot "Aibos.Mark.$variant.$size.png"
        Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing monochrome mark: $path"
    }
}

Assert-True (Test-Path -LiteralPath $icoPath -PathType Leaf) "Missing ICO: $icoPath"
$icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
Assert-True ($icoBytes.Length -ge 6) 'ICO header is truncated.'
Assert-True ([BitConverter]::ToUInt16($icoBytes, 0) -eq 0) 'ICO reserved field is invalid.'
Assert-True ([BitConverter]::ToUInt16($icoBytes, 2) -eq 1) 'ICO type is not icon.'
$frameCount = [BitConverter]::ToUInt16($icoBytes, 4)
Assert-True ($frameCount -eq $expectedSizes.Count) "ICO has $frameCount frames; expected $($expectedSizes.Count)."

$icoSizes = @()
for ($index = 0; $index -lt $frameCount; $index++) {
    $entryOffset = 6 + (16 * $index)
    $width = if ($icoBytes[$entryOffset] -eq 0) { 256 } else { [int]$icoBytes[$entryOffset] }
    $height = if ($icoBytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$icoBytes[$entryOffset + 1] }
    $planes = [BitConverter]::ToUInt16($icoBytes, $entryOffset + 4)
    $bitDepth = [BitConverter]::ToUInt16($icoBytes, $entryOffset + 6)
    $resourceLength = [BitConverter]::ToUInt32($icoBytes, $entryOffset + 8)
    $resourceOffset = [BitConverter]::ToUInt32($icoBytes, $entryOffset + 12)

    Assert-True ($width -eq $height) "ICO frame $index is not square."
    Assert-True ($planes -eq 1 -and $bitDepth -eq 32) "ICO frame $index is not 32-bit RGBA."
    Assert-True (($resourceOffset + $resourceLength) -le $icoBytes.Length) "ICO frame $index is out of bounds."
    Assert-True (
        $icoBytes[$resourceOffset] -eq 0x89 -and
        $icoBytes[$resourceOffset + 1] -eq 0x50 -and
        $icoBytes[$resourceOffset + 2] -eq 0x4E -and
        $icoBytes[$resourceOffset + 3] -eq 0x47
    ) "ICO frame $index is not PNG-backed."
    $icoSizes += $width
}

Assert-True (($icoSizes -join ',') -eq ($expectedSizes -join ',')) `
    "ICO frame order mismatch: $($icoSizes -join ',')."

$projectText = Get-Content -LiteralPath $projectPath -Raw
$windowText = Get-Content -LiteralPath $windowPath -Raw
$runtimeRelativePaths = @(
    'Brand\Generated\Aibos.App.ico',
    'Brand\Generated\Aibos.Mark.20.png',
    'Brand\Generated\Aibos.Mark.24.png',
    'Brand\Generated\Aibos.Mark.64.png'
)

foreach ($relativePath in $runtimeRelativePaths) {
    Assert-True ($projectText.Contains("<Resource Include=`"$relativePath`" />")) `
        "Runtime brand resource is not explicitly embedded: $relativePath"
}

Assert-True ($projectText.Contains('<ApplicationIcon>Brand\Generated\Aibos.App.ico</ApplicationIcon>')) `
    'The executable application icon is not configured.'
Assert-True (-not $projectText.Contains('.Source.png"')) `
    'Build-time ImageGen sources must not be embedded as runtime resources.'
Assert-True ($windowText.Contains('Icon="Brand/Generated/Aibos.App.ico"')) `
    'MainWindow does not use the multi-frame application icon.'

foreach ($size in @(20, 24, 64)) {
    Assert-True ($windowText.Contains("Source=`"Brand/Generated/Aibos.Mark.$size.png`"")) `
        "MainWindow is missing the ${size}px Gallery Fold mark."
}

$runtimePayload = 0L
foreach ($relativePath in $runtimeRelativePaths) {
    $runtimePayload += (Get-Item -LiteralPath (Join-Path $projectRoot $relativePath)).Length
}
Assert-True ($runtimePayload -le 512KB) `
    "Embedded brand payload is $runtimePayload bytes, above the 512 KiB budget."

[pscustomobject]@{
    Result = 'PASS'
    PngFrames = $expectedSizes.Count * 2
    IcoFrames = $frameCount
    IcoSizes = $icoSizes -join ','
    RuntimePayloadBytes = $runtimePayload
    RuntimePayloadKiB = [Math]::Round($runtimePayload / 1KB, 2)
    SmallOpticalMasters = '16,20,24'
    MonochromeVariants = 'white,black'
} | Format-List
