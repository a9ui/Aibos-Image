[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$SkipProjectBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\ExactVideoFrameSelection.cs'
$projectPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Exact video frame selection source is missing: $sourcePath"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "WPF project is missing: $projectPath"
}

$source = Get-Content -Raw -Encoding UTF8 -LiteralPath $sourcePath
if ($source -match 'System\.IO|HttpClient|SendEnhancement|Enqueue|SaveState|File\.|Directory\.|Process\.|Task\.') {
    throw 'Exact video frame selection crossed an I/O, transport, process, persistence, or dispatch boundary.'
}
if ($source -match 'MaximumSelectionSeconds|300\s*frame|five.second' `
    -or $source -notmatch 'MaximumSourceFrames\s*=\s*18_000' `
    -or $source -notmatch 'MaximumSourceDurationMs\s*=\s*300_000' `
    -or $source -notmatch 'endFrameExclusive - 1' `
    -or $source -notmatch 'selectedFrameCount - 1\) / 2') {
    throw 'The common selector leaked an Edit-only limit or lost its source bounds and half-open preview rule.'
}

$dotnet = Get-Command $DotNetPath -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet is required for the standalone exact-frame oracle: $DotNetPath"
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'aibos-exact-video-frame-selection-' + [guid]::NewGuid().ToString('N'))
$smokeProjectPath = Join-Path $smokeRoot 'ExactVideoFrameSelectionSmoke.csproj'
$programPath = Join-Path $smokeRoot 'Program.cs'

try {
    [IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
    $escapedSourcePath = [Security.SecurityElement]::Escape($sourcePath)
    $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$escapedSourcePath" Link="ExactVideoFrameSelection.cs" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText(
        $smokeProjectPath,
        $projectXml,
        [Text.UTF8Encoding]::new($false))

    $program = @'
using PhotoViewer.Wpf;

static class Oracle
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ExactVideoFrameSource Source(
        int frames,
        int fpsNumerator,
        int fpsDenominator,
        int durationMs) => new(
            frames,
            fpsNumerator,
            fpsDenominator,
            durationMs,
            new ExactVideoPtsMetadata(
                1,
                90_000,
                -900,
                Digest));

    private static ExactVideoFrameSelection Plan(
        ExactVideoFrameSource source,
        int start,
        int end)
    {
        Assert(
            ExactVideoFrameSelector.TrySelect(
                source,
                start,
                end,
                out ExactVideoFrameSelection selection,
                out ExactVideoFrameSelectionError error),
            $"expected success, got {error}");
        return selection;
    }

    private static void Reject(
        ExactVideoFrameSource source,
        int start,
        int end,
        ExactVideoFrameSelectionError expected)
    {
        Assert(
            !ExactVideoFrameSelector.TrySelect(
                source,
                start,
                end,
                out _,
                out ExactVideoFrameSelectionError error)
            && error == expected,
            $"expected rejection {expected}, got {error}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        ExactVideoFrameSelection one = Plan(Source(24, 24, 1, 1_000), 7, 8);
        Assert(one.SelectedFrameCount == 1, "one-frame count");
        Assert(one.StartPreviewFrame == 7
            && one.MiddlePreviewFrame == 7
            && one.EndPreviewFrame == 7, "one-frame previews");
        Assert(one.Duration == new ExactVideoRational(1, 24), "one-frame duration");

        ExactVideoFrameSelection odd = Plan(Source(24, 24, 1, 1_000), 10, 15);
        Assert(odd.StartPreviewFrame == 10
            && odd.MiddlePreviewFrame == 12
            && odd.EndPreviewFrame == 14, "odd previews");

        ExactVideoFrameSelection even = Plan(Source(24, 24, 1, 1_000), 10, 14);
        Assert(even.StartPreviewFrame == 10
            && even.MiddlePreviewFrame == 11
            && even.EndPreviewFrame == 13, "even previews use lower middle");

        ExactVideoFrameSelection fifteen = Plan(
            Source(360, 24, 1, 15_000),
            0,
            360);
        Assert(fifteen.SelectedFrameCount == 360
            && fifteen.Duration == new ExactVideoRational(15, 1)
            && fifteen.EndPreviewFrame == 359, "15-second source");

        ExactVideoFrameSelection longVideo = Plan(
            Source(17_400, 60, 1, 290_000),
            0,
            17_400);
        Assert(longVideo.SelectedFrameCount == 17_400
            && longVideo.Duration == new ExactVideoRational(290, 1)
            && longVideo.MiddlePreviewFrame == 8_699
            && longVideo.EndPreviewFrame == 17_399, "290-second source");

        Assert(
            ExactVideoFrameSelector.FitsPolicy(
                fifteen,
                360,
                new ExactVideoRational(15, 1)),
            "caller-owned long policy");
        Assert(
            !ExactVideoFrameSelector.FitsPolicy(
                fifteen,
                300,
                new ExactVideoRational(5, 1)),
            "caller-owned Edit policy");
        ExactVideoFrameSelection fiveAt60 = Plan(
            Source(600, 60, 1, 10_000),
            120,
            420);
        Assert(
            ExactVideoFrameSelector.FitsPolicy(
                fiveAt60,
                300,
                new ExactVideoRational(5, 1)),
            "inclusive caller policy boundary");

        Reject(Source(0, 24, 1, 1_000), 0, 1,
            ExactVideoFrameSelectionError.SourceOutOfBounds);
        Reject(null!, 0, 1,
            ExactVideoFrameSelectionError.SourceOutOfBounds);
        Reject(Source(18_001, 60, 1, 300_000), 0, 1,
            ExactVideoFrameSelectionError.SourceOutOfBounds);
        Reject(Source(60, 60, 1, 300_001), 0, 1,
            ExactVideoFrameSelectionError.SourceOutOfBounds);
        Reject(Source(25, 25, 1, 1_000), 0, 1,
            ExactVideoFrameSelectionError.UnsupportedFps);
        Reject(Source(24, 48, 2, 1_000), 0, 1,
            ExactVideoFrameSelectionError.UnsupportedFps);
        Reject(Source(24, 24, 1, 1_000) with {
            Pts = new ExactVideoPtsMetadata(0, 90_000, 0, Digest)
        }, 0, 1, ExactVideoFrameSelectionError.InvalidPtsMetadata);
        Reject(Source(24, 24, 1, 1_000) with {
            Pts = new ExactVideoPtsMetadata(1, 90_000, 0, Digest.ToUpperInvariant())
        }, 0, 1, ExactVideoFrameSelectionError.InvalidPtsMetadata);

        ExactVideoFrameSource safe = Source(60, 60, 1, 1_000);
        Reject(safe, -1, 1, ExactVideoFrameSelectionError.InvalidRange);
        Reject(safe, 1, 1, ExactVideoFrameSelectionError.InvalidRange);
        Reject(safe, 0, 61, ExactVideoFrameSelectionError.InvalidRange);
        Reject(safe, int.MinValue, int.MaxValue,
            ExactVideoFrameSelectionError.InvalidRange);
        Reject(Source(int.MaxValue, 60, 1, 1_000), 0, int.MaxValue,
            ExactVideoFrameSelectionError.SourceOutOfBounds);
        Assert(
            ExactVideoFrameSelector.TryValidateSource(
                safe,
                out ExactVideoFrameSelectionError sourceError)
            && sourceError == ExactVideoFrameSelectionError.None,
            "reusable source validator");

        ExactVideoFrameSelection repeat = Plan(
            Source(17_400, 60, 1, 290_000),
            123,
            16_999);
        ExactVideoFrameSelection repeatAgain = Plan(
            Source(17_400, 60, 1, 290_000),
            123,
            16_999);
        Assert(repeat == repeatAgain, "determinism");

        Assert(
            !ExactVideoFrameSelector.FitsPolicy(
                repeat,
                int.MaxValue,
                new ExactVideoRational(long.MaxValue, long.MaxValue)),
            "overflow-safe rational comparison");
        Assert(
            !ExactVideoFrameSelector.FitsPolicy(
                null,
                int.MaxValue,
                new ExactVideoRational(long.MaxValue, 1)),
            "null policy input fails closed");
    }
}

internal static class Program
{
    private static void Main()
    {
        Oracle.Run();
        Console.WriteLine("Exact video frame selection oracle passed.");
    }
}
'@
    [IO.File]::WriteAllText(
        $programPath,
        $program,
        [Text.UTF8Encoding]::new($false))

    $runArguments = @(
        'run',
        '--project', $smokeProjectPath,
        '--configuration', $Configuration,
        '--no-launch-profile',
        '--nologo'
    )
    if ($NoRestore) {
        $runArguments += '--no-restore'
    }
    & $dotnet.Source @runArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Exact video frame selection oracle failed with exit code $LASTEXITCODE."
    }

    if (-not $SkipProjectBuild) {
        $wpfArtifacts = Join-Path $smokeRoot 'wpf-artifacts'
        $buildArguments = @(
            'build', $projectPath,
            '--configuration', $Configuration,
            '--artifacts-path', $wpfArtifacts,
            '--nologo'
        )
        if ($NoRestore) {
            $buildArguments += '--no-restore'
        }
        & $dotnet.Source @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "WPF Release build failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot).TrimEnd('\')
    $expectedPrefix = $resolvedTemp + '\aibos-exact-video-frame-selection-'
    if ($resolvedSmoke.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) `
        -and $resolvedSmoke -ne $resolvedTemp `
        -and (Test-Path -LiteralPath $resolvedSmoke)) {
        Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}
