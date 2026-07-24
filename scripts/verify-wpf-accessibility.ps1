param(
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path $env:TEMP 'photoviewer-wpf-accessibility.json'),
    [string]$FallbackOutputPath = (Join-Path $env:TEMP 'photoviewer-wpf-accessibility-fallback.json'),
    [string]$ScreenshotPath = (Join-Path $env:TEMP 'photoviewer-wpf-high-contrast.png'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$fallbackResultPath = [IO.Path]::GetFullPath($FallbackOutputPath)
$shotPath = [IO.Path]::GetFullPath($ScreenshotPath)

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

foreach ($path in @($resultPath, $fallbackResultPath, $shotPath)) {
    $directory = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$smoke = Start-Process -FilePath $exe -ArgumentList @(
    '--high-contrast-smoke', ('"{0}"' -f $resultPath)
) -WindowStyle Hidden -PassThru -Wait
if ($smoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw "Accessibility smoke failed with exit code $($smoke.ExitCode)."
}
$result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
$required = @('colorResources', 'liveBrushes', 'paletteFlag', 'actionAccessibility', 'dialogFocusContracts', 'restored', 'brushesRestored')
$missing = @($required | Where-Object { $result.$_ -ne $true })
if ($result.ok -ne $true -or $missing.Count -gt 0) {
    throw "Accessibility contract failed: $($missing -join ', ')."
}

$fallbackSmoke = Start-Process -FilePath $exe -ArgumentList @(
    '--accessibility-fallback-smoke', ('"{0}"' -f $fallbackResultPath)
) -WindowStyle Hidden -PassThru -Wait
if ($fallbackSmoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $fallbackResultPath -PathType Leaf)) {
    throw "Accessibility fallback smoke failed with exit code $($fallbackSmoke.ExitCode)."
}
$fallback = Get-Content -Raw -LiteralPath $fallbackResultPath | ConvertFrom-Json
$fallbackRequired = @(
    'followsSystem',
    'fullOverrideSaved',
    'fullOverrideEffective',
    'standardMotionActive',
    'reduceOverrideSaved',
    'activeMotionCanceled',
    'reducedMotionInstant',
    'reducedTransparencyOpaque',
    'localOverridesPersisted',
    'unknownLocalPreserved',
    'localOverridesReloaded',
    'nullFollowsInjectedSystem',
    'explicitFullOverridesSystem',
    'highContrastWins',
    'highContrastExitRestoresOverride',
    'sharedSettingsUntouched'
)
$fallbackMissing = @($fallbackRequired | Where-Object { $fallback.$_ -ne $true })
if ($fallback.ok -ne $true -or $fallbackMissing.Count -gt 0 -or @($fallback.nonOpaqueKeys).Count -gt 0) {
    throw "Accessibility fallback contract failed: $($fallbackMissing -join ', ')."
}

$capture = Start-Process -FilePath $exe -ArgumentList @(
    '--shot', ('"{0}"' -f $shotPath),
    '--screen', 'landing',
    '--show-app-settings',
    '--force-high-contrast',
    '--shot-width', '1280',
    '--shot-height', '820'
) -WindowStyle Hidden -PassThru -Wait
if ($capture.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $shotPath -PathType Leaf)) {
    throw "High-contrast screenshot failed with exit code $($capture.ExitCode)."
}
$bytes = [IO.File]::ReadAllBytes($shotPath)
if ($bytes.Length -lt 8 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4e -or $bytes[3] -ne 0x47) {
    throw 'High-contrast screenshot is not a valid PNG.'
}

[pscustomobject]@{
    ok = $true
    highContrast = $result
    fallback = $fallback
    screenshot = $shotPath
    screenshotBytes = $bytes.Length
} | ConvertTo-Json -Depth 8
