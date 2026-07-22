$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$executablePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\bin\Release\net8.0-windows\PhotoViewer.Wpf.exe'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$albumUiRoot = Join-Path $tempBase ('pvu-album-ui-' + [Guid]::NewGuid().ToString('N'))
$watch = [Diagnostics.Stopwatch]::StartNew()
$albumUiResult = $null

Push-Location $repoRoot
try {
    & dotnet build $projectPath -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "WPF Album store Release build failed with exit code $LASTEXITCODE"
    }

    New-Item -ItemType Directory -Path $albumUiRoot | Out-Null
    $albumUiResultPath = Join-Path $albumUiRoot 'result.json'
    $albumUiStorePath = Join-Path $albumUiRoot 'albums.json'
    $albumUiProcess = Start-Process -FilePath $executablePath -ArgumentList @(
        '--album-ui-smoke', $albumUiResultPath,
        '--album-path', $albumUiStorePath
    ) -Wait -PassThru -WindowStyle Hidden
    if ($albumUiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $albumUiResultPath)) {
        throw "WPF Album UI smoke failed with exit code $($albumUiProcess.ExitCode)"
    }
    $albumUiResult = Get-Content -Raw -LiteralPath $albumUiResultPath | ConvertFrom-Json
    if (!$albumUiResult.ok) {
        throw "WPF Album UI smoke reported a failed contract"
    }
}
finally {
    Pop-Location
    $resolvedAlbumUiRoot = [IO.Path]::GetFullPath($albumUiRoot)
    if ((Test-Path -LiteralPath $resolvedAlbumUiRoot) -and $resolvedAlbumUiRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedAlbumUiRoot -Recurse -Force
    }
    $watch.Stop()
}

[pscustomobject]@{
    ok = $true
    releaseBuild = $true
    runtime = 'wpf'
    browserCompatibilityNotRun = $true
    wpfUiContract = [bool]$albumUiResult.uiContract
    wpfCurrentOutsideMissing = [bool]$albumUiResult.unavailableExplicit
    activeAvailabilityCancellation = [bool]$albumUiResult.activeAvailabilityCancellation
    recycleReconciliationDoesNotWaitForAvailability = [bool]$albumUiResult.recycleReconciliationDoesNotWaitForAvailability
    wpfAlbumSourceRestored = [bool]$albumUiResult.catalogRestored
    collisionAwareShortcut = [bool]$albumUiResult.collisionAwareShortcut
    elapsedMs = $watch.ElapsedMilliseconds
} | ConvertTo-Json
