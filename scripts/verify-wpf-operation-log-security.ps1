param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'
$exe = Join-Path $repoRoot "local-native\PhotoViewer.Wpf\bin\$Configuration\net10.0-windows\PhotoViewer.Wpf.exe"
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$testRoot = Join-Path $tempRoot ('aibos-operation-log-security-' + [guid]::NewGuid().ToString('N'))
$testRootPrefix = $testRoot + [IO.Path]::DirectorySeparatorChar
$localAppData = Join-Path $testRoot 'local-app-data'
$outside = Join-Path $testRoot 'outside'
$appDirectory = Join-Path $localAppData 'Aibos Image'
$logsDirectory = Join-Path $appDirectory 'Logs'
$sentinel = Join-Path $outside 'sentinel.txt'
$markerLine = '{"operation":"security_smoke","outcome":"accepted"}'
$oldLog = Join-Path $logsDirectory 'operations-2000-01-01.jsonl'
$recentLog = Join-Path $logsDirectory 'operations-2099-01-01.jsonl'
$dotnet10 = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $dotnet10 -PathType Leaf) { $dotnet10 } else { 'dotnet' }
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX64 = $env:DOTNET_ROOT_X64

function Assert-TestPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($testRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test path escaped its TEMP root: $fullPath"
    }
    return $fullPath
}

function Remove-TestJunction([string]$Path) {
    $fullPath = Assert-TestPath $Path
    if (-not [IO.Directory]::Exists($fullPath)) { return }
    $attributes = [IO.File]::GetAttributes($fullPath)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Refusing to remove a non-junction test path: $fullPath"
    }
    [IO.Directory]::Delete($fullPath, $false)
}

function Invoke-OperationLogSmoke([string]$Root, [string]$Expectation) {
    $process = Start-Process -FilePath $exe `
        -ArgumentList @(
            '--operation-log-security-smoke',
            ('"{0}"' -f $Root),
            '--expect',
            $Expectation) `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Operation-log security smoke expected $Expectation but exited $($process.ExitCode)."
    }
}

try {
    if ($dotnet -ne 'dotnet') {
        $runtimeRoot = Split-Path -Parent $dotnet
        $env:DOTNET_ROOT = $runtimeRoot
        $env:DOTNET_ROOT_X64 = $runtimeRoot
    }

    if (-not $SkipBuild) {
        $artifactRoot = Join-Path $testRoot 'build-artifacts'
        & $dotnet build $project -c $Configuration --artifacts-path $artifactRoot --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $builtExecutables = @(Get-ChildItem -LiteralPath $artifactRoot `
            -Filter 'PhotoViewer.Wpf.exe' -File -Recurse)
        if ($builtExecutables.Count -ne 1) {
            throw "Expected one isolated WPF executable, found $($builtExecutables.Count)."
        }
        $exe = $builtExecutables[0].FullName
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "WPF executable is unavailable: $exe"
    }

    [IO.Directory]::CreateDirectory($localAppData) | Out-Null
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText($sentinel, 'outside-sentinel')

    New-Item -ItemType Junction -Path $appDirectory -Target $outside | Out-Null
    Invoke-OperationLogSmoke $localAppData 'reject'
    if (@(Get-ChildItem -LiteralPath $outside -Filter 'operations-*.jsonl' -File).Count -ne 0) {
        throw 'A parent-directory junction received operation-log data.'
    }
    Remove-TestJunction $appDirectory

    [IO.Directory]::CreateDirectory($appDirectory) | Out-Null
    New-Item -ItemType Junction -Path $logsDirectory -Target $outside | Out-Null
    Invoke-OperationLogSmoke $localAppData 'reject'
    if (@(Get-ChildItem -LiteralPath $outside -Filter 'operations-*.jsonl' -File).Count -ne 0) {
        throw 'A Logs junction received operation-log data.'
    }
    Remove-TestJunction $logsDirectory

    [IO.Directory]::CreateDirectory($logsDirectory) | Out-Null
    $dailyLog = Join-Path $logsDirectory ('operations-{0:yyyy-MM-dd}.jsonl' -f [DateTime]::UtcNow)
    New-Item -ItemType HardLink -Path $dailyLog -Target $sentinel | Out-Null
    Invoke-OperationLogSmoke $localAppData 'reject'
    if ([IO.File]::ReadAllText($sentinel) -ne 'outside-sentinel') {
        throw 'A hard-linked daily log changed the outside sentinel.'
    }
    [IO.File]::Delete($dailyLog)

    [IO.File]::WriteAllText($oldLog, 'expired')
    [IO.File]::SetLastWriteTimeUtc($oldLog, [DateTime]::UtcNow.AddDays(-30))
    [IO.File]::WriteAllText($recentLog, 'preserve')
    Invoke-OperationLogSmoke $localAppData 'accept'

    if (-not (Test-Path -LiteralPath $dailyLog -PathType Leaf)) {
        throw 'The trusted daily operation log was not created.'
    }
    if ([IO.File]::ReadAllText($dailyLog).IndexOf($markerLine, [StringComparison]::Ordinal) -lt 0) {
        throw 'The trusted daily operation log did not contain the expected marker.'
    }
    $entries = @(Get-Content -LiteralPath $dailyLog | ForEach-Object {
        $_ | ConvertFrom-Json
    })
    $lifecycle = @($entries | Where-Object {
        $_.Operation -eq 'companion.process' -and $_.Outcome -eq 'unexpected_exit'
    })
    if ($lifecycle.Count -ne 1) {
        throw "Expected one companion lifecycle entry, found $($lifecycle.Count)."
    }
    if ($lifecycle[0].RelatedProcessId -ne 4321 `
        -or $lifecycle[0].ExitCode -ne -1 `
        -or $lifecycle[0].ErrorCode -ne 'terminated_or_aborted') {
        throw 'The companion lifecycle entry did not preserve the bounded numeric diagnostics.'
    }
    $forbiddenLifecycleFields = @($lifecycle[0].PSObject.Properties.Name | Where-Object {
        $_ -match 'prompt|source|path|secret|token|job'
    })
    if ($forbiddenLifecycleFields.Count -ne 0) {
        throw "The companion lifecycle entry exposed forbidden fields: $($forbiddenLifecycleFields -join ', ')."
    }
    if (Test-Path -LiteralPath $oldLog) {
        throw 'The expired direct log was not removed.'
    }
    if (-not (Test-Path -LiteralPath $recentLog -PathType Leaf)) {
        throw 'The recent direct log was removed unexpectedly.'
    }
    if ([IO.File]::ReadAllText($sentinel) -ne 'outside-sentinel') {
        throw 'The outside sentinel changed.'
    }

    [pscustomobject]@{
        ok = $true
        parentJunctionRejected = $true
        logsJunctionRejected = $true
        dailyHardLinkRejected = $true
        outsideWriteCount = 0
        trustedAppendSucceeded = $true
        companionLifecycleRecorded = $true
        companionLifecycleSensitiveFields = 0
        expiredDirectLogRemoved = $true
        recentDirectLogPreserved = $true
    } | ConvertTo-Json
}
finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
    foreach ($candidate in @($logsDirectory, $appDirectory)) {
        if ([IO.Directory]::Exists($candidate)) {
            $attributes = [IO.File]::GetAttributes($candidate)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Remove-TestJunction $candidate
            }
        }
    }
    if ([IO.Directory]::Exists($testRoot)) {
        $verifiedRoot = [IO.Path]::GetFullPath($testRoot)
        if (-not $verifiedRoot.StartsWith(
                $tempRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a test root outside TEMP: $verifiedRoot"
        }
        Remove-Item -LiteralPath $verifiedRoot -Recurse -Force
    }
}
