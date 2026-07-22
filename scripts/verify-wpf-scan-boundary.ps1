param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net8.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('photoviewer-wpf-scan-boundary-' + [guid]::NewGuid().ToString('N'))))
$systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $tempRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing fixture outside the system temp directory: $tempRoot"
}

$selectedRoot = Join-Path $tempRoot 'selected'
$regularFolder = Join-Path $selectedRoot 'regular'
$outsideRoot = Join-Path $tempRoot 'outside'
$outsideLink = Join-Path $selectedRoot 'outside-junction'
$cycleLink = Join-Path $regularFolder 'cycle-junction'
$resultPath = Join-Path $tempRoot 'result.json'

try {
    New-Item -ItemType Directory -Path $regularFolder -Force | Out-Null
    New-Item -ItemType Directory -Path $outsideRoot -Force | Out-Null
    $insideA = Join-Path $selectedRoot 'inside-a.png'
    $insideB = Join-Path $regularFolder 'inside-b.jpg'
    $outsideImage = Join-Path $outsideRoot 'must-not-index.png'
    [IO.File]::WriteAllText($insideA, 'inside-a')
    [IO.File]::WriteAllText($insideB, 'inside-b')
    [IO.File]::WriteAllText($outsideImage, 'outside')
    $sourceHashesBefore = @{
        insideA = (Get-FileHash -LiteralPath $insideA -Algorithm SHA256).Hash
        insideB = (Get-FileHash -LiteralPath $insideB -Algorithm SHA256).Hash
        outside = (Get-FileHash -LiteralPath $outsideImage -Algorithm SHA256).Hash
    }

    New-Item -ItemType Junction -Path $outsideLink -Target $outsideRoot -ErrorAction Stop | Out-Null
    New-Item -ItemType Junction -Path $cycleLink -Target $selectedRoot -ErrorAction Stop | Out-Null
    $outsideAttributes = [IO.File]::GetAttributes($outsideLink)
    $cycleAttributes = [IO.File]::GetAttributes($cycleLink)
    if (($outsideAttributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
        ($cycleAttributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw 'Fixture junctions were not marked as reparse points.'
    }

    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--scan-boundary-smoke', ('"{0}"' -f $resultPath), '--folder', ('"{0}"' -f $selectedRoot)) `
        -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "scan boundary smoke exited with $($process.ExitCode)" }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'scan boundary smoke did not write its result' }

    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $actualImages = @($result.images | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
    $expectedImages = @([IO.Path]::GetFullPath($insideA), [IO.Path]::GetFullPath($insideB))
    $failures = @()
    if ($result.ok -ne $true) { $failures += "result was not ok: $($result.message)" }
    if (@($actualImages).Count -ne 2) { $failures += "indexed image count was $(@($actualImages).Count), expected 2" }
    foreach ($expectedImage in $expectedImages) {
        if ($actualImages -notcontains $expectedImage) { $failures += "missing in-root image: $expectedImage" }
    }
    if ($actualImages -contains [IO.Path]::GetFullPath($outsideImage)) { $failures += 'outside junction image entered the catalog' }
    if (@($result.boundarySkips).Count -lt 2) { $failures += "boundary skip count was $(@($result.boundarySkips).Count), expected at least 2" }
    if (@($result.accessFailures).Count -ne 0) { $failures += "unexpected access failures: $(@($result.accessFailures).Count)" }
    if ([long]$result.elapsedMs -gt 5000) { $failures += "scan took $($result.elapsedMs)ms; cycle protection did not finish promptly" }

    $sourceHashesAfter = @{
        insideA = (Get-FileHash -LiteralPath $insideA -Algorithm SHA256).Hash
        insideB = (Get-FileHash -LiteralPath $insideB -Algorithm SHA256).Hash
        outside = (Get-FileHash -LiteralPath $outsideImage -Algorithm SHA256).Hash
    }
    foreach ($key in $sourceHashesBefore.Keys) {
        if ($sourceHashesBefore[$key] -ne $sourceHashesAfter[$key]) { $failures += "source changed: $key" }
    }
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }

    [pscustomobject]@{
        ok = $true
        indexed = @($actualImages).Count
        boundarySkips = @($result.boundarySkips).Count
        accessFailures = @($result.accessFailures).Count
        elapsedMs = [long]$result.elapsedMs
        outsideIndexed = $false
        sourceHashesUnchanged = $true
    } | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedFixture = [IO.Path]::GetFullPath($tempRoot)
        if (-not $resolvedFixture.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing cleanup outside the system temp directory: $resolvedFixture"
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
