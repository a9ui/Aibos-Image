param(
    [string]$DotnetPath = '',
    [string]$EnvelopeFixturePath = '',
    [string]$MaintenanceMarkerFixturePath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$storeSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\EnhancementEnqueueInboxStore.cs'
$policySource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\EnhancementEnqueueProbePolicy.cs'
$companionSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementCompanion.cs'
$publicationSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.DurableEnqueuePublication.cs'
$jobsSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.EnhancementJobs.cs'
$videoSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.VideoGeneration.cs'
$sharedLockSource = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\AlbumStore.cs'
$contractPath = Join-Path $repoRoot 'contracts\enhancement-enqueue-inbox-v1.json'
if (-not (Test-Path -LiteralPath $storeSource -PathType Leaf)) {
    throw 'Durable enqueue inbox store source is missing.'
}
if (-not (Test-Path -LiteralPath $policySource -PathType Leaf)) {
    throw 'Durable enqueue probe policy source is missing.'
}
foreach ($sourcePath in @($companionSource, $publicationSource, $jobsSource, $videoSource, $sharedLockSource)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Durable enqueue WPF source is missing: $sourcePath"
    }
}
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw 'Durable enqueue inbox contract is missing.'
}

$companionText = Get-Content -LiteralPath $companionSource -Raw
$publicationText = Get-Content -LiteralPath $publicationSource -Raw
$storeText = Get-Content -LiteralPath $storeSource -Raw
$jobsText = Get-Content -LiteralPath $jobsSource -Raw
$videoText = Get-Content -LiteralPath $videoSource -Raw
$sharedLockText = Get-Content -LiteralPath $sharedLockSource -Raw
if (($jobsText -notmatch 'bool requiresPinnedVideoSource = job\.IsVideoOperation\s*&& !job\.IsExactCurrentVideoToolsV2\s*&& !job\.IsExactCurrentVideoTrimV1') -or
    ($jobsText -notmatch 'onBeforeDurablePublish:\s*requiresPinnedVideoSource') -or
    ($jobsText -notmatch 'PinVideoRetrySourceForDurablePublish\(job\)')) {
    throw 'Single video retry does not pin its source through durable publication.'
}
$savedPromptStart = $jobsText.IndexOf(
    'private async Task RerunMiniMaxH3VideoWithSavedPromptCoreAsync',
    [StringComparison]::Ordinal)
$savedPromptEnd = if ($savedPromptStart -ge 0) {
    $jobsText.IndexOf(
        'private bool TryOpenMiniMaxH3VideoRerunSourceInViewer',
        $savedPromptStart,
        [StringComparison]::Ordinal)
} else {
    -1
}
if ($savedPromptStart -lt 0 -or $savedPromptEnd -le $savedPromptStart) {
    throw 'Saved-Prompt video rerun method boundary is missing.'
}
$savedPromptText = $jobsText.Substring(
    $savedPromptStart,
    $savedPromptEnd - $savedPromptStart)
if (($savedPromptText -notmatch 'TryCaptureVideoSourceStamp') -or
    ($savedPromptText -notmatch 'AcquireVideoDurablePublishLease') -or
    ($savedPromptText -notmatch 'PinVideoSourceForDurablePublish\(sourceStamp\)') -or
    ($savedPromptText -notmatch 'RecordPendingVideoSourceDependency') -or
    ($savedPromptText -notmatch '_pendingVideoSourceDependencies\.Remove')) {
    throw 'Saved-Prompt video rerun no longer pins and overlays its source through durable publication.'
}
if (($companionText -notmatch 'DurablePublishLeaseFactory\?\.Invoke\(\)') -or
    ($companionText -notmatch 'job\.IsVideoOperation\s*\?\s*\(\) => PinVideoRetrySourceForDurablePublish\(job\)')) {
    throw 'Batch video retry does not acquire every source lease before envelope publication.'
}
if (($videoText -notmatch 'PinVideoRetrySourceForDurablePublish') -or
    ($videoText -notmatch 'FileShare\.Read,')) {
    throw 'Video durable-publication pin no longer denies delete sharing.'
}
if (($companionText -notmatch 'AcquireEnhancementJobsWriteLeaseForDurablePublish') -or
    ($storeText -notmatch 'Path\.Combine\(jobsDirectory, "jobs\.json"\)') -or
    ($companionText -notmatch 'return onBeforeDurablePublish\?\.Invoke\(item\)') -or
    ($publicationText -notmatch 'EnhancementEnqueueInboxStore\.Publish\(') -or
    ($sharedLockText -notmatch 'TryAcquireSharedDirectoryWriteLease')) {
    throw 'WPF publication no longer owns the shared Companion Jobs writer lock before source callbacks.'
}

$dotnet = if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
}
else {
    [IO.Path]::GetFullPath($DotnetPath)
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'A local .NET SDK host is required for the focused smoke.'
}

$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $tempPrefix ('aibos-enqueue-inbox-smoke-' + [guid]::NewGuid().ToString('N'))))
if (-not $smokeRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Durable enqueue verifier root must stay under TEMP.'
}
$resultPath = Join-Path $smokeRoot 'result.json'
$projectPath = Join-Path $smokeRoot 'Smoke.csproj'
$programPath = Join-Path $smokeRoot 'Program.cs'
$nugetConfigPath = Join-Path $smokeRoot 'NuGet.Config'
$taskPreviousDotnetRoot = $env:DOTNET_ROOT
$taskPreviousDotnetRootX64 = $env:DOTNET_ROOT_X64
$taskPreviousNugetScratch = $env:NUGET_SCRATCH
$markerLease = $null

try {
    if (-not [string]::IsNullOrWhiteSpace($MaintenanceMarkerFixturePath)) {
        $MaintenanceMarkerFixturePath = [IO.Path]::GetFullPath($MaintenanceMarkerFixturePath)
        $markerLease = [IO.FileStream]::new($MaintenanceMarkerFixturePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($markerLease.Length -gt 128 * 1024) { throw 'Supplied marker fixture exceeds its bound.' }
    }
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:NUGET_SCRATCH = Join-Path $smokeRoot 'nuget-scratch'
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    $escapedStoreSource = [System.Security.SecurityElement]::Escape($storeSource)
    $escapedPolicySource = [System.Security.SecurityElement]::Escape($policySource)
    $escapedSharedLockSource = [System.Security.SecurityElement]::Escape($sharedLockSource)
    [System.IO.File]::WriteAllText(
        $projectPath,
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$escapedStoreSource" Link="EnhancementEnqueueInboxStore.cs" />
    <Compile Include="$escapedPolicySource" Link="EnhancementEnqueueProbePolicy.cs" />
    <Compile Include="$escapedSharedLockSource" Link="AlbumStore.cs" />
  </ItemGroup>
</Project>
"@,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        $programPath,
        @'
using System.ComponentModel;
using System.Text.Json;
using PhotoViewer.Wpf;

string contractPath = args.First();
string resultPath = args[1];
string root = Path.Combine(Path.GetDirectoryName(resultPath)!, "fixture");
string jobsPath = Path.Combine(root, "enhance", "jobs.json");
string pending = EnhancementEnqueueInboxStore.GetPendingDirectory(jobsPath);
bool passiveReadOnly = !Directory.Exists(pending);

EnhancementEnqueueInboxItem create = EnhancementEnqueueInboxStore.CreateItem(
    new { operation = "upscale", presetId = "synthetic" },
    "next",
    0,
    requestId: "11111111-1111-4111-8111-111111111111");
EnhancementEnqueueInboxItem retry = EnhancementEnqueueInboxStore.CreateItem(
    null,
    "last",
    1,
    kind: "retry",
    retryJobId: "synthetic-job",
    requestId: "22222222-2222-4222-8222-222222222222");
EnhancementEnqueueInboxItem video = EnhancementEnqueueInboxStore.CreateItem(
    new { operation = "video", presetId = "synthetic" },
    "last",
    2,
    requestId: "44444444-4444-4444-8444-444444444444",
    includeQueuePlacementInBody: false);
EnhancementEnqueueInboxPublishResult published = EnhancementEnqueueInboxStore.Publish(
    jobsPath,
    [create, retry, video],
    "202601020304050000000-33333333333343338333333333333333",
    DateTimeOffset.Parse("2026-01-02T03:04:05Z"));

byte[] original = File.ReadAllBytes(published.Path);
File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(resultPath)!, "shared-envelope.json"), original);
using JsonDocument document = JsonDocument.Parse(original);
JsonElement envelope = document.RootElement;
JsonElement[] items = envelope.GetProperty("items").EnumerateArray().ToArray();
bool schemaOk = envelope.GetProperty("protocolVersion").GetInt32() == 1
    && envelope.GetProperty("backendGeneration").GetString() == "json-v1"
    && envelope.GetProperty("batchId").GetString() == published.BatchId
    && items.Length == 3
    && items[0].GetProperty("bodyJson").GetString() == create.BodyJson
    && items[1].GetProperty("bodyJson").GetString() == retry.BodyJson
    && items[2].GetProperty("bodyJson").GetString() == video.BodyJson
    && create.BodyJson.Contains("\"queuePlacement\":\"next\"", StringComparison.Ordinal)
    && retry.BodyJson == "{\"queuePlacement\":\"last\"}"
    && !video.BodyJson.Contains("queuePlacement", StringComparison.Ordinal)
    && create.RequestHash == "c3b78ea3147b650dffdd43706782e5599558a0a312377e81ddbdb611720063d8"
    && create.RequestHash == EnhancementEnqueueInboxStore.ComputeRequestHash(
        create.Kind,
        create.RetryJobId,
        create.BodyJson)
    && retry.RequestHash == EnhancementEnqueueInboxStore.ComputeRequestHash(
        retry.Kind,
        retry.RetryJobId,
        retry.BodyJson);
bool noTemporaryFile = Directory.EnumerateFiles(pending, "*.tmp").Any() == false;
EnhancementEnqueueInboxPublishResult generatedFirst =
    EnhancementEnqueueInboxStore.Publish(jobsPath, [create, retry, video]);
EnhancementEnqueueInboxPublishResult generatedSecond =
    EnhancementEnqueueInboxStore.Publish(jobsPath, [create, retry, video]);
string firstBatchFile = Path.GetFileNameWithoutExtension(generatedFirst.Path);
string secondBatchFile = Path.GetFileNameWithoutExtension(generatedSecond.Path);
bool sortableBatchIds = string.CompareOrdinal(firstBatchFile, secondBatchFile) < 0
    && firstBatchFile.Length == 54
    && secondBatchFile.Length == 54;
bool writeThroughMove = EnhancementEnqueueInboxStore.MoveFileWriteThroughFlagForSmoke == 0x00000008;

using JsonDocument contractDocument = JsonDocument.Parse(File.ReadAllText(contractPath));
JsonElement contract = contractDocument.RootElement;
JsonElement bounds = contract.GetProperty("bounds");
JsonElement capability = contract.GetProperty("capability");
JsonElement[] vectors = contract.GetProperty("vectors").EnumerateArray().ToArray();
JsonElement createVector = vectors.Single(vector =>
    vector.GetProperty("id").GetString() == "synthetic-create-last").GetProperty("item");
JsonElement retryVector = vectors.Single(vector =>
    vector.GetProperty("id").GetString() == "synthetic-retry-last").GetProperty("item");
string retrySourceDismissal = contract
    .GetProperty("consumer")
    .GetProperty("failedRetrySourceDismissal")
    .GetString() ?? "";
JsonElement managedVideoSourceReservation = contract.GetProperty(
    "managedVideoSourceReservation");
bool contractOk = contract.GetProperty("schemaVersion").GetInt32()
        == EnhancementEnqueueInboxStore.ProtocolVersion
    && capability.GetProperty("protocolVersion").GetInt32()
        == EnhancementEnqueueInboxStore.ProtocolVersion
    && capability.GetProperty("backendGeneration").GetString()
        == EnhancementEnqueueInboxStore.BackendGeneration
    && bounds.GetProperty("maximumEnvelopeUtf8Bytes").GetInt32()
        == EnhancementEnqueueInboxStore.MaximumEnvelopeBytes
    && bounds.GetProperty("maximumItemsPerEnvelope").GetInt32()
        == EnhancementEnqueueInboxStore.MaximumItemsPerEnvelope
    && bounds.GetProperty("maximumBodyJsonUtf8Bytes").GetInt32()
        == EnhancementEnqueueInboxStore.MaximumBodyJsonBytes
    && bounds.GetProperty("maximumActivePollCommittedFiles").GetInt32()
        == EnhancementEnqueueInboxStore.MaximumActiveEnvelopes
    && bounds.GetProperty("maximumActivePollDirectoryEntries").GetInt32()
        == EnhancementEnqueueInboxStore.MaximumActiveDirectoryEntries
    && bounds.GetProperty("maximumActivePollUtf8Bytes").GetInt64()
        == EnhancementEnqueueInboxStore.MaximumActiveEnvelopeBytes
    && retrySourceDismissal.Contains(
        "failed or canceled history row",
        StringComparison.Ordinal)
    && !retrySourceDismissal.Contains(
        "canceled source history remains visible",
        StringComparison.Ordinal)
    && managedVideoSourceReservation
        .GetProperty("maximumDependencyScanCommittedFiles").GetInt32() == 128
    && managedVideoSourceReservation
        .GetProperty("maximumDependencyScanDirectoryEntries").GetInt32() == 256
    && managedVideoSourceReservation
        .GetProperty("maximumDependencyScanItems").GetInt32() == 4096
    && managedVideoSourceReservation
        .GetProperty("maximumDependencyScanUtf8Bytes").GetInt32() == 67108864
    && managedVideoSourceReservation.GetProperty("passiveRead").GetString()!
        .Contains("does not create directories", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("needsAction").GetString()!
        .Contains("definitive 4xx no-job outcome", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("publicationTransfer").GetString()!
        .Contains("exact source handle", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("writerInterlock").GetString()!
        .Contains("jobs.json", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("scope").GetString()!
        .Contains("sourceVideoJobId", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("authoritativeDeleteGuard").GetString()!
        .Contains("managed video-output DELETE", StringComparison.Ordinal)
    && managedVideoSourceReservation.GetProperty("publicationTransfer").GetString()!
        .Contains("Video Tools create", StringComparison.Ordinal)
    && createVector.GetProperty("requestHash").GetString()
        == EnhancementEnqueueInboxStore.ComputeRequestHash(
            createVector.GetProperty("kind").GetString()!,
            null,
            createVector.GetProperty("bodyJson").GetString()!)
    && retryVector.GetProperty("requestHash").GetString()
        == EnhancementEnqueueInboxStore.ComputeRequestHash(
            retryVector.GetProperty("kind").GetString()!,
            retryVector.GetProperty("retryJobId").GetString(),
            retryVector.GetProperty("bodyJson").GetString()!);

bool placementFences = false;
try
{
    _ = EnhancementEnqueueInboxStore.CreateItem(
        null,
        "next",
        0,
        kind: "retry",
        retryJobId: "synthetic-job");
}
catch (ArgumentException)
{
    try
    {
        _ = EnhancementEnqueueInboxStore.CreateItem(
            new { operation = "video" },
            "next",
            0,
            includeQueuePlacementInBody: false);
    }
    catch (ArgumentException)
    {
        placementFences = true;
    }
}

bool overwriteRefused = false;
try
{
    _ = EnhancementEnqueueInboxStore.Publish(
        jobsPath,
        [create, retry, video],
        published.BatchId,
        DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
}
catch (Win32Exception)
{
    overwriteRefused = File.ReadAllBytes(published.Path).SequenceEqual(original);
}

int filesBeforeSizeFences = Directory.EnumerateFiles(pending, "*.json").Count();
bool bodySizeFence = false;
try
{
    _ = EnhancementEnqueueInboxStore.CreateItem(
        new { payload = new string('x', EnhancementEnqueueInboxStore.MaximumBodyJsonBytes + 1) },
        "last",
        0);
}
catch (EnhancementEnqueuePayloadTooLargeException)
{
    bodySizeFence = Directory.EnumerateFiles(pending, "*.json").Count() == filesBeforeSizeFences;
}

bool envelopeSizeFence = false;
try
{
    EnhancementEnqueueInboxItem[] oversizedEnvelope = Enumerable.Range(0, 3)
        .Select(index => EnhancementEnqueueInboxStore.CreateItem(
            new { payload = new string((char)('a' + index), 6 * 1024 * 1024) },
            "last",
            index))
        .ToArray();
    _ = EnhancementEnqueueInboxStore.Publish(jobsPath, oversizedEnvelope);
}
catch (EnhancementEnqueuePayloadTooLargeException)
{
    envelopeSizeFence = Directory.EnumerateFiles(pending, "*.json").Count() == filesBeforeSizeFences;
}

// Only synthetic fixtures are used. Both phases count, including unpublished
// directory entries, and source callbacks cannot run after capacity refusal.
string CapacityJobs(string name) => Path.Combine(root, name, "enhance", "jobs.sqlite");
string[] CapacityPhases(string path)
{
    string pendingPath = EnhancementEnqueueInboxStore.GetPendingDirectory(path);
    string processingPath = Path.Combine(Path.GetDirectoryName(pendingPath)!, "processing");
    Directory.CreateDirectory(pendingPath);
    Directory.CreateDirectory(processingPath);
    return [pendingPath, processingPath];
}
int pinCount = 0;
bool lockHeldDuringPin = true;
bool lockHeldDuringRelease = true;
int sourceReleases = 0;
string countJobs = CapacityJobs("count");
string[] countPhases = CapacityPhases(countJobs);
for (int index = 0; index < 127; index++)
    File.WriteAllText(Path.Combine(countPhases[index % 2], $"existing-{index}.json"), "{}");
string writerLock = Path.Combine(Path.GetDirectoryName(countJobs)!, "jobs.json.lock", "owner.json");
using var publishStart = new ManualResetEventSlim(false);
Task<bool>[] publishers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
{
    publishStart.Wait();
    try
    {
        EnhancementEnqueueInboxStore.Publish(countJobs, [create], beforePublish: () =>
        {
            Interlocked.Increment(ref pinCount);
            lockHeldDuringPin &= File.Exists(writerLock);
            return new CallbackLease(() =>
            {
                lockHeldDuringRelease &= File.Exists(writerLock);
                Interlocked.Increment(ref sourceReleases);
            });
        });
        return true;
    }
    catch (EnhancementEnqueueInboxCapacityException) { return false; }
})).ToArray();
publishStart.Set();
Task.WaitAll(publishers);
bool concurrentCountFence = publishers.Count(task => task.Result) == 1
    && pinCount == 1 && sourceReleases == 1
    && lockHeldDuringPin && lockHeldDuringRelease && !File.Exists(writerLock)
    && countPhases.Sum(directory => Directory.GetFiles(directory, "*.json").Length) == 128;

string entriesJobs = CapacityJobs("entries");
string[] entriesPhases = CapacityPhases(entriesJobs);
for (int index = 0; index < 255; index++)
    File.WriteAllText(Path.Combine(entriesPhases[index % 2], $"uncommitted-{index}.tmp"), "retain");
EnhancementEnqueueInboxStore.Publish(entriesJobs, [create]);
bool entryFence = false;
try
{
    EnhancementEnqueueInboxStore.Publish(entriesJobs, [create], beforePublish: () =>
        throw new InvalidOperationException("Capacity refusal must precede the source callback."));
}
catch (EnhancementEnqueueInboxCapacityException)
{
    entryFence = entriesPhases.Sum(directory => Directory.GetFileSystemEntries(directory).Length) == 256
        && entriesPhases.Sum(directory => Directory.GetFiles(directory, "*.tmp").Length) == 255;
}

string bytesJobs = CapacityJobs("bytes");
string[] bytesPhases = CapacityPhases(bytesJobs);
string byteBatchId = "202601020304050000000-55555555555545558555555555555555";
DateTimeOffset byteCreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
EnhancementEnqueueInboxItem unicodeItem = EnhancementEnqueueInboxStore.CreateItem(
    new { payload = "日本語😀" }, "last", 0);
var sizing = EnhancementEnqueueInboxStore.Publish(CapacityJobs("sizing"), [unicodeItem], byteBatchId, byteCreatedAt);
long publicationBytes = new FileInfo(sizing.Path).Length;
long bytesLeft = EnhancementEnqueueInboxStore.MaximumActiveEnvelopeBytes - publicationBytes;
for (int index = 0; bytesLeft > 0; index++)
{
    long size = Math.Min(bytesLeft, EnhancementEnqueueInboxStore.MaximumEnvelopeBytes);
    using var filler = new FileStream(Path.Combine(bytesPhases[index % 2], $"bytes-{index}.json"), FileMode.CreateNew);
    filler.SetLength(size);
    bytesLeft -= size;
}
EnhancementEnqueueInboxStore.Publish(bytesJobs, [unicodeItem], byteBatchId, byteCreatedAt);
bool byteFence = false;
try
{
    EnhancementEnqueueInboxStore.Publish(bytesJobs, [create], beforePublish: () =>
        throw new InvalidOperationException("Byte capacity refusal must precede the source callback."));
}
catch (EnhancementEnqueueInboxCapacityException)
{
    byteFence = bytesPhases.Sum(directory => Directory.GetFiles(directory, "*.json")
        .Sum(file => new FileInfo(file).Length)) == EnhancementEnqueueInboxStore.MaximumActiveEnvelopeBytes;
}

bool failedSourceLeavesNoReservation = false;
string failedSourceJobs = CapacityJobs("failed-source");
try
{
    EnhancementEnqueueInboxStore.Publish(failedSourceJobs, [create], beforePublish: () =>
        throw new InvalidOperationException("Synthetic missing source."));
}
catch (InvalidOperationException)
{
    failedSourceLeavesNoReservation = !Directory.Exists(EnhancementEnqueueInboxStore.GetPendingDirectory(failedSourceJobs))
        && !Directory.Exists(Path.Combine(Path.GetDirectoryName(failedSourceJobs)!, "jobs.json.lock"));
}

string metadataJobs = CapacityJobs("unreadable-metadata");
string[] metadataPhases = CapacityPhases(metadataJobs);
for (int index = 0; index < 128; index++)
    File.WriteAllText(Path.Combine(metadataPhases[index % 2], $"metadata-{index}.json"), "{}");
int metadataPins = 0;
bool metadataRefused = false;
bool metadataFailureInjected = false;
// Limit fault injection to the one API result that conflates absence with an
// attribute lookup failure. The enumerated file remains present and unchanged.
EnhancementEnqueueInboxStore.EntryExistsForSmoke = entry =>
{
    if (entry.Name == "metadata-0.json")
    {
        metadataFailureInjected = File.Exists(entry.FullName);
        return false;
    }
    return entry.Exists;
};
try
{
    EnhancementEnqueueInboxStore.Publish(metadataJobs, [create], beforePublish: () =>
    {
        metadataPins++;
        return null;
    });
}
catch (IOException) { metadataRefused = true; }
finally { EnhancementEnqueueInboxStore.EntryExistsForSmoke = null; }
int metadataFilesAfter = metadataPhases.Sum(directory => Directory.GetFiles(directory, "*.json").Length);
bool unreadableEntryFence = metadataRefused && metadataFailureInjected && metadataPins == 0
    && metadataFilesAfter == 128
    && metadataPhases.SelectMany(directory => Directory.GetFiles(directory))
        .All(file => Path.GetExtension(file) == ".json" && File.ReadAllText(file) == "{}")
    && !Directory.Exists(Path.Combine(Path.GetDirectoryName(metadataJobs)!, "jobs.json.lock"));

bool maintenanceFence = true;
var maintenanceVectors = contract.GetProperty("maintenanceGate").GetProperty("presenceVectors").EnumerateArray()
    .Select(vector => (Id: vector.GetProperty("id").GetString()!, Contents: vector.GetProperty("contents").GetString()!)).ToList();
if (args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]))
{
    if (new FileInfo(args[2]).Length > 128 * 1024) throw new IOException("Supplied marker fixture exceeds its bound.");
    maintenanceVectors.Add(("supplied-cutover", File.ReadAllText(args[2])));
}
foreach (var vector in maintenanceVectors)
{
    string repairJobs = CapacityJobs("maintenance-" + vector.Id);
    string[] repairPhases = CapacityPhases(repairJobs);
    string marker = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(repairPhases[0])!)!,
        EnhancementEnqueueInboxStore.MaintenanceMarkerFileName);
    string contents = vector.Contents;
    File.WriteAllText(marker, contents);
    int repairPins = 0;
    bool refused = false;
    try
    {
        EnhancementEnqueueInboxStore.Publish(repairJobs, [create], beforePublish: () =>
        {
            repairPins++;
            return null;
        });
    }
    catch (EnhancementEnqueueInboxMaintenanceException) { refused = true; }
    maintenanceFence &= refused && repairPins == 0 && File.ReadAllText(marker) == contents
        && repairPhases.All(directory => !Directory.EnumerateFileSystemEntries(directory).Any())
        && !Directory.Exists(Path.Combine(Path.GetDirectoryName(repairJobs)!, "jobs.json.lock"));
}

using JsonDocument durableHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument duplicateCapabilitiesHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"}},"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument duplicateDurableCapabilityHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"},"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument duplicateReadyHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"ready":true,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument duplicateProtocolVersionHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument duplicateBackendGenerationHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":true,"protocolVersion":1,"backendGeneration":"json-v1","backendGeneration":"json-v1"}}}""");
using JsonDocument capabilityAbsentHealth = JsonDocument.Parse(
    """{"capabilities":{"legacyQueue":true}}""");
using JsonDocument emptyCapabilitiesHealth = JsonDocument.Parse(
    """{"capabilities":{}}""");
using JsonDocument malformedDurableHealth = JsonDocument.Parse(
    """{"capabilities":{"durableEnqueueInboxV1":{"ready":false,"protocolVersion":1,"backendGeneration":"json-v1"}}}""");
using JsonDocument emptyHealth = JsonDocument.Parse("{}");
bool triStatePolicy =
    EnhancementEnqueueProbePolicy.Classify(true, 200, durableHealth.RootElement)
        == EnhancementEnqueueBackendMode.Durable
    && EnhancementEnqueueProbePolicy.Classify(true, 200, capabilityAbsentHealth.RootElement)
        == EnhancementEnqueueBackendMode.Unknown
    && EnhancementEnqueueProbePolicy.Classify(true, 200, emptyCapabilitiesHealth.RootElement)
        == EnhancementEnqueueBackendMode.Unknown
    && EnhancementEnqueueProbePolicy.Classify(true, 200, malformedDurableHealth.RootElement)
        == EnhancementEnqueueBackendMode.Unknown
    && EnhancementEnqueueProbePolicy.Classify(true, 200, emptyHealth.RootElement)
        == EnhancementEnqueueBackendMode.Unknown
    && EnhancementEnqueueProbePolicy.Classify(false, 500, null)
        == EnhancementEnqueueBackendMode.Unknown
    && EnhancementEnqueueProbePolicy.Classify(false, 404, null)
        == EnhancementEnqueueBackendMode.Unknown;
bool duplicateCapabilityKeysRejected = new[]
    {
        duplicateCapabilitiesHealth.RootElement,
        duplicateDurableCapabilityHealth.RootElement,
        duplicateReadyHealth.RootElement,
        duplicateProtocolVersionHealth.RootElement,
        duplicateBackendGenerationHealth.RootElement,
    }
    .All(payload => EnhancementEnqueueProbePolicy.Classify(true, 200, payload)
        == EnhancementEnqueueBackendMode.Unknown);

const string receiptRequestId = "55555555-5555-4555-8555-555555555555";
using JsonDocument validReceipt = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","clientRequestId":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateReceipt = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateIdempotencyKeyReceipt = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateClientRequestIdReceipt = JsonDocument.Parse(
    """{"receipt":{"clientRequestId":"55555555-5555-4555-8555-555555555555","clientRequestId":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument conflictingRequestIdentityReceipt = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","clientRequestId":"66666666-6666-4666-8666-666666666666","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateReceiptJobId = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateJobEnvelope = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job"}}""");
using JsonDocument duplicateReturnedJobId = JsonDocument.Parse(
    """{"receipt":{"idempotencyKey":"55555555-5555-4555-8555-555555555555","jobId":"synthetic-receipt-job"},"job":{"id":"synthetic-receipt-job","id":"synthetic-receipt-job"}}""");
bool duplicateReceiptKeysRejected =
    EnhancementEnqueueProbePolicy.HasMatchingDurableReceipt(
        validReceipt.RootElement,
        receiptRequestId)
    && new[]
        {
            duplicateReceipt.RootElement,
            duplicateIdempotencyKeyReceipt.RootElement,
            duplicateClientRequestIdReceipt.RootElement,
            conflictingRequestIdentityReceipt.RootElement,
            duplicateReceiptJobId.RootElement,
            duplicateJobEnvelope.RootElement,
            duplicateReturnedJobId.RootElement,
        }
        .All(payload => !EnhancementEnqueueProbePolicy.HasMatchingDurableReceipt(
            payload,
            receiptRequestId));
bool onlyExactDurableCapabilityAllowsNudge =
    !EnhancementEnqueueProbePolicy.AllowsImmediateNudge(EnhancementEnqueueBackendMode.Unknown)
    && EnhancementEnqueueProbePolicy.AllowsImmediateNudge(EnhancementEnqueueBackendMode.Durable)
    && !EnhancementEnqueueProbePolicy.AllowsFeatureValidation(EnhancementEnqueueBackendMode.Unknown)
    && EnhancementEnqueueProbePolicy.AllowsFeatureValidation(EnhancementEnqueueBackendMode.Durable);
var chunkSizes = new List<int>();
for (int remaining = 2_001; remaining > 0;)
{
    int count = EnhancementEnqueueProbePolicy.NextEnvelopeItemCount(remaining);
    chunkSizes.Add(count);
    remaining -= count;
}
bool chunking = chunkSizes.SequenceEqual([1_000, 1_000, 1]);

var result = new
{
    pass = passiveReadOnly
        && schemaOk
        && noTemporaryFile
        && placementFences
        && overwriteRefused
        && sortableBatchIds
        && writeThroughMove
        && contractOk
        && bodySizeFence
        && envelopeSizeFence
        && concurrentCountFence
        && entryFence
        && byteFence
        && failedSourceLeavesNoReservation
        && unreadableEntryFence
        && maintenanceFence
        && triStatePolicy
        && duplicateCapabilityKeysRejected
        && duplicateReceiptKeysRejected
        && onlyExactDurableCapabilityAllowsNudge
        && chunking,
    passiveReadOnly,
    schemaOk,
    noTemporaryFile,
    placementFences,
    overwriteRefused,
    sortableBatchIds,
    writeThroughMove,
    contractOk,
    bodySizeFence,
    envelopeSizeFence,
    concurrentCountFence,
    entryFence,
    byteFence,
    failedSourceLeavesNoReservation,
    unreadableEntryFence,
    maintenanceFence,
    metadataFailureInjected,
    metadataPins,
    metadataFilesAfter,
    triStatePolicy,
    duplicateCapabilityKeysRejected,
    duplicateReceiptKeysRejected,
    onlyExactDurableCapabilityAllowsNudge,
    chunking,
    itemCount = items.Length,
};
File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
return result.pass ? 0 : 1;

sealed class CallbackLease(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

namespace PhotoViewer.Wpf
{
    // AlbumStore's root-discovery seam is unrelated to its actual writer lock.
    internal static class MainWindow
    {
        internal static string ResolveSharedProjectRootForSmoke(string start) => start;
    }
}
'@,
        [System.Text.UTF8Encoding]::new($false))

    [System.IO.File]::WriteAllText(
        $nugetConfigPath,
        '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>',
        [System.Text.UTF8Encoding]::new($false))

    & $dotnet restore $projectPath --configfile $nugetConfigPath --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Durable enqueue inbox focused smoke restore failed.'
    }
    & $dotnet run --project $projectPath --configuration Release --no-restore -- $contractPath $resultPath $MaintenanceMarkerFixturePath
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $resultPath) {
            Get-Content -LiteralPath $resultPath -Raw | Write-Host
        }
        throw 'Durable enqueue inbox focused smoke failed.'
    }
    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $result.pass) {
        throw 'Durable enqueue inbox focused smoke returned a failing result.'
    }
    if (-not [string]::IsNullOrWhiteSpace($EnvelopeFixturePath)) {
        # Opt-in export lets another protocol reader check these exact bytes.
        # Never overwrite an existing artifact or depend on another repository.
        [IO.File]::Copy((Join-Path $smokeRoot 'shared-envelope.json'), [IO.Path]::GetFullPath($EnvelopeFixturePath), $false)
    }
    Write-Host ('PASS durable enqueue inbox: passive={0}, schema={1}, atomic={2}, items={3}, capacity={4}/{5}/{6}, sourceFailure={7}, unreadableEntry={8}, maintenance={9}' -f `
        $result.passiveReadOnly,
        $result.schemaOk,
        $result.overwriteRefused,
        $result.itemCount,
        $result.concurrentCountFence,
        $result.entryFence,
        $result.byteFence,
        $result.failedSourceLeavesNoReservation,
        $result.unreadableEntryFence,
        $result.maintenanceFence)
}
finally {
    if ($null -ne $markerLease) { $markerLease.Dispose() }
    $env:DOTNET_ROOT = $taskPreviousDotnetRoot
    $env:DOTNET_ROOT_X64 = $taskPreviousDotnetRootX64
    $env:NUGET_SCRATCH = $taskPreviousNugetScratch
    if (Test-Path -LiteralPath $smokeRoot) {
        $resolvedSmokeRoot = [IO.Path]::GetFullPath($smokeRoot)
        if (-not $resolvedSmokeRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a verifier path outside TEMP: $resolvedSmokeRoot"
        }
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
