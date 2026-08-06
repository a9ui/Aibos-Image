param(
    [string]$Configuration = 'Release',
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$dll = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.dll"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$tempPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('photoviewer-wpf-png-metadata-verifier-' + [guid]::NewGuid().ToString('N'))))
Assert-True $runRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) 'Verifier root must stay under TEMP.'
$resultPath = Join-Path $runRoot 'result.json'

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    & $DotnetPath build $project -c $Configuration --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE." }

    $process = Start-Process -FilePath $DotnetPath `
        -ArgumentList @(('"{0}"' -f $dll), '--png-metadata-smoke', ('"{0}"' -f $resultPath)) `
        -WindowStyle Hidden -PassThru -Wait
    $details = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath } else { 'no result file' }
    Assert-True ($process.ExitCode -eq 0) "PNG metadata process exited $($process.ExitCode): $details"
    Assert-True (Test-Path -LiteralPath $resultPath -PathType Leaf) 'PNG metadata smoke produced no JSON.'

    $result = $details | ConvertFrom-Json
    Assert-True ($result.ok -eq $true) "PNG metadata smoke failed: $details"
    Assert-True ($result.duplicateFirstChunkOwned -eq $true) 'A later duplicate parameters chunk replaced the first chunk.'
    Assert-True ($result.emptyFirstChunkOwned -eq $true) 'A later parameters chunk replaced an empty first chunk.'
    Assert-True ($result.unicodeITextRead -eq $true) 'Uncompressed Unicode iTXt parameters were not read consistently.'
    Assert-True ($result.comfyGraphRead -eq $true) 'Legacy ComfyUI prompt graph metadata was not recovered consistently.'
    Assert-True ($result.comfyLoraOffRead -eq $true) 'A LoRA-off ComfyUI graph did not remain explicitly LoRA-off.'
    Assert-True ($result.parametersOverrideComfy -eq $true) 'A ComfyUI graph overrode the authoritative parameters chunk.'

    [pscustomobject]@{
        allPassed = $true
        message = 'Catalog, Preview, Modal, and Copy agree for A1111 parameters and legacy ComfyUI prompt graphs.'
        processId = $process.Id
        duplicateCatalogPrompt = $result.duplicateCatalogPrompt
        emptyFirstCatalogPrompt = $result.emptyFirstCatalogPrompt
        duplicateFirstChunkOwned = $result.duplicateFirstChunkOwned
        emptyFirstChunkOwned = $result.emptyFirstChunkOwned
        unicodeITextRead = $result.unicodeITextRead
        comfyGraphRead = $result.comfyGraphRead
        comfyLoraOffRead = $result.comfyLoraOffRead
        parametersOverrideComfy = $result.parametersOverrideComfy
    } | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
        if (-not $resolvedRunRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedRunRoot"
        }
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
