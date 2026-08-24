param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$StaticOnly,
    [ValidateRange(10, 120)]
    [int]$OverallTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$partialPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoToolsV2Inventory.cs'
$smokePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.VideoToolsV2InventorySmoke.cs'
$appPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\App.xaml.cs'
$videoPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.Video.cs'
$selectorPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.Photoreal.cs'
$jobsPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs'
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$fixturePath = Join-Path $repoRoot 'contracts\fixtures\enhancement-video-tools-v2-reader-v1.json'

foreach ($path in @($partialPath, $smokePath, $appPath, $videoPath, $selectorPath, $jobsPath, $projectPath, $fixturePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Video Tools v2 inventory source is missing: $path"
    }
}

$partial = Get-Content -Raw -Encoding UTF8 -LiteralPath $partialPath
$smoke = Get-Content -Raw -Encoding UTF8 -LiteralPath $smokePath
$app = Get-Content -Raw -Encoding UTF8 -LiteralPath $appPath
$video = Get-Content -Raw -Encoding UTF8 -LiteralPath $videoPath
$selector = Get-Content -Raw -Encoding UTF8 -LiteralPath $selectorPath
$jobs = Get-Content -Raw -Encoding UTF8 -LiteralPath $jobsPath
foreach ($token in @(
    'TryBuildVideoToolsV2ManagedVideoVersion',
    'ResolveVideoToolsV2ManagedInventory',
    'MaxVideoToolsV2InventoryJobs',
    'FileAttributes.ReparsePoint',
    'staged-displayed-file',
    'sourceVideoJobId')) {
    if ($partial -notmatch [regex]::Escape($token)) {
        throw "Video Tools v2 inventory is missing $token"
    }
}
if ($app -notmatch '--video-tools-v2-inventory-smoke' -or $app -notmatch 'CaptureVideoToolsV2InventorySmoke') {
    throw 'The focused Video Tools v2 inventory smoke dispatch is missing.'
}
if ($selector -notmatch 'AI高画質化' -or $selector -notmatch 'AI編集' -or $selector -notmatch '生成') {
    throw 'The video version selector does not distinguish generation, Edit, and Finish.'
}
if ($jobs -notmatch 'CanUseVideoToolsV2Output' -or $jobs -notmatch 'open-output') {
    throw 'Exact succeeded Video Tools v2 open-output presentation is missing.'
}
if ($smoke -notmatch 'ambiguousOutputProtected' `
    -or $smoke -notmatch 'duplicateOutputAliasA' `
    -or $smoke -notmatch 'duplicateOutputAliasB') {
    throw 'The focused smoke is missing canonical duplicate-output ownership vectors.'
}

if ($StaticOnly) {
    [pscustomobject]@{ ok = $true; staticOnly = $true; writerEnabled = $false } |
        ConvertTo-Json -Depth 4
    return
}

$runRoot = Join-Path $env:TEMP ('aibos-wpf-video-tools-v2-inventory-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$resultPath = Join-Path $runRoot 'result.json'
$stdoutPath = Join-Path $runRoot 'stdout.log'
$stderrPath = Join-Path $runRoot 'stderr.log'
$process = $null
try {
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    if ($DotNetPath -eq 'dotnet') {
        $localDotNet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
        if (Test-Path -LiteralPath $localDotNet -PathType Leaf) { $DotNetPath = $localDotNet }
    }
    & $DotNetPath build $projectPath -c $Configuration --artifacts-path $buildRoot --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "WPF build failed with exit code $LASTEXITCODE" }
    $dll = Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter PhotoViewer.Wpf.dll |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($dll)) { throw 'Built WPF DLL was not found.' }
    $process = Start-Process -FilePath $DotNetPath -ArgumentList @(
        ('"{0}"' -f $dll), '--video-tools-v2-inventory-smoke', ('"{0}"' -f $resultPath),
        '--fixture', ('"{0}"' -f $fixturePath)) `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($OverallTimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw 'WPF Video Tools v2 inventory smoke timed out.'
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw ('Inventory smoke did not produce a result: ' + (Get-Content -Raw -LiteralPath $stderrPath))
    }
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $required = @(
        'exactInventory',
        'ancestry',
        'failClosed',
        'ambiguousOutputProtected',
        'labels',
        'openOutput',
        'passiveRead',
        'writerClosed')
    $failed = @($required | Where-Object { $result.$_ -ne $true })
    if ($process.ExitCode -ne 0 -or $result.ok -ne $true -or $failed.Count -gt 0) {
        throw ('Inventory smoke failed: ' + ($result | ConvertTo-Json -Depth 8 -Compress) + '; failed=' + ($failed -join ','))
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
