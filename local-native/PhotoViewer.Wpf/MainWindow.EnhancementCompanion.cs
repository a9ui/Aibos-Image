using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementCompanionReadyTimeoutMilliseconds = 120_000;
    private const int EnhancementCompanionProbeDelayMilliseconds = 450;
    private const string EnhancementCompanionLauncherFileName =
        "enhancement_companion.js";
    private const string LegacyNextCompanionLauncherFileName =
        "prod_launcher.js";
    private const string PhotorealPromptControlsCapability = "photorealPromptControlsV2";
    private const string AtomicImageEnqueueNextCapability = "atomicImageEnqueueNext";
    private const string PhotorealSourceUpscaleCapability = "photorealSourceUpscale";
    private const string RecoveredPhotorealSourceUpscaleCapability =
        "recoveredPhotorealSourceUpscaleV1";
    private const string PhotorealSeedControlCapability =
        "photorealSeedControlV1";
    private const string VideoSeedControlCapability = "videoSeedControlV1";
    private const int DurableEnqueueActionDeadlineMilliseconds = 2_000;

    private readonly SemaphoreSlim _enhancementCompanionLaunchGate = new(1, 1);
    private readonly CancellationTokenSource _enhancementCompanionLifetimeCts = new();
    private Process? _ownedEnhancementCompanion;
    private string? _enhancementCompanionLaunchError;
    private int _enhancementCompanionLaunchAttemptCount;
    private Func<Uri, (bool Started, string Error)>? _startEnhancementCompanionForSmoke;
    private sealed record EnhancementEnqueueProbe(
        EnhancementEnqueueBackendMode Mode,
        JsonElement? HealthPayload,
        long ActionDeadlineTick);
    private sealed record DurableEnhancementBatchResponse(
        EnhancementApiResponse[] Responses,
        int NudgeCount,
        int PublishedCount);

    private async Task<EnhancementApiResponse> EnsureEnhancementCompanionReadyForExplicitActionAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        string readinessRoute = string.IsNullOrWhiteSpace(sourceIdentity)
            ? "api/enhance/jobs"
            : $"api/enhance/jobs?sourceId={Uri.EscapeDataString(sourceIdentity)}";
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            readinessRoute,
            token: token);
        // Any HTTP status proves that a loopback server already owns the
        // endpoint. Only a transport failure may authorize a new process.
        if (IsReadyEnhancementCompanionResponse(response)
            || !_usingDefaultModalEnhancementSender
            || response.StatusCode > 0)
            return response;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            _enhancementCompanionLifetimeCts.Token);
        CancellationToken linkedToken = linkedCts.Token;
        await _enhancementCompanionLaunchGate.WaitAsync(linkedToken);
        try
        {
            response = await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: linkedToken);
            if (IsReadyEnhancementCompanionResponse(response)
                || response.StatusCode > 0)
                return response;

            if (_ownedEnhancementCompanion is null || _ownedEnhancementCompanion.HasExited)
            {
                StopOwnedEnhancementCompanion();
                if (!TryStartOwnedEnhancementCompanion(out string startError))
                {
                    _enhancementCompanionLaunchError = startError;
                    return new EnhancementApiResponse(false, 0, null, startError);
                }
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnhancementCompanionReadyTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                linkedToken.ThrowIfCancellationRequested();
                if (_ownedEnhancementCompanion is { HasExited: true } exited)
                {
                    string error = $"The local AI companion stopped before it became ready (exit {exited.ExitCode}).";
                    _enhancementCompanionLaunchError = error;
                    StopOwnedEnhancementCompanion();
                    return new EnhancementApiResponse(false, 0, null, error);
                }

                await Task.Delay(EnhancementCompanionProbeDelayMilliseconds, linkedToken);
                response = await SendEnhancementApiAsync(
                    HttpMethod.Get,
                    readinessRoute,
                    token: linkedToken);
                if (IsReadyEnhancementCompanionResponse(response))
                {
                    _enhancementCompanionLaunchError = null;
                    return response;
                }
            }

            string timeoutError = "The local AI companion did not become ready within two minutes.";
            _enhancementCompanionLaunchError = timeoutError;
            StopOwnedEnhancementCompanion();
            return new EnhancementApiResponse(false, 0, null, timeoutError);
        }
        catch (OperationCanceledException)
        {
            StopOwnedEnhancementCompanion();
            return new EnhancementApiResponse(false, 0, null, "Starting the local AI companion was canceled.");
        }
        finally
        {
            _enhancementCompanionLaunchGate.Release();
        }
    }

    private async Task<EnhancementApiResponse> EnsurePhotorealCompanionReadyForExplicitActionAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
        => await EnsureImageEnhancementCompanionReadyForExplicitActionAsync(
            "photoreal",
            enqueueNext: false,
            sourceIdentity: sourceIdentity,
            token: token);

    private async Task<EnhancementApiResponse> EnsureEnhancementCapabilityForExplicitActionAsync(
        string capability,
        string capabilityLabel,
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        EnhancementApiResponse readiness =
            await EnsureEnhancementCompanionReadyForExplicitActionAsync(
                sourceIdentity,
                token);
        if (!readiness.Ok)
            return readiness;

        EnhancementApiResponse health = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            token: token);
        if (health.Ok
            && health.Payload is JsonElement payload
            && HasEnhancementCapability(payload, capability))
        {
            return readiness;
        }

        return new EnhancementApiResponse(
            false,
            426,
            health.Payload,
            $"The running H25 companion does not support {capabilityLabel}. Restart H25 first; no job was added.");
    }

    private async Task<EnhancementApiResponse> EnsureImageEnhancementCompanionReadyForExplicitActionAsync(
        string operation,
        bool enqueueNext,
        string? sourceIdentity = null,
        bool requiresPhotorealSourceUpscale = false,
        bool requiresRecoveredPhotorealSourceUpscale = false,
        bool requiresPhotorealSeedControl = false,
        CancellationToken token = default)
    {
        EnhancementApiResponse readiness =
            await EnsureEnhancementCompanionReadyForExplicitActionAsync(sourceIdentity, token);
        if (!readiness.Ok)
            return readiness;

        bool needsPhotorealControls = string.Equals(
            operation,
            "photoreal",
            StringComparison.Ordinal);
        if (!needsPhotorealControls
            && !enqueueNext
            && !requiresPhotorealSourceUpscale
            && !requiresRecoveredPhotorealSourceUpscale
            && !requiresPhotorealSeedControl)
            return readiness;

        EnhancementApiResponse health = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            token: token);
        bool photorealSupported = !needsPhotorealControls;
        bool enqueueNextSupported = !enqueueNext;
        bool photorealSourceUpscaleSupported =
            !requiresPhotorealSourceUpscale;
        bool recoveredPhotorealSourceUpscaleSupported =
            !requiresRecoveredPhotorealSourceUpscale;
        bool photorealSeedControlSupported = !requiresPhotorealSeedControl;
        if (health.Ok && health.Payload is JsonElement payload)
        {
            photorealSupported = photorealSupported
                || HasEnhancementCapability(payload, PhotorealPromptControlsCapability);
            enqueueNextSupported = enqueueNextSupported
                || HasEnhancementCapability(payload, AtomicImageEnqueueNextCapability);
            photorealSourceUpscaleSupported = photorealSourceUpscaleSupported
                || HasEnhancementCapability(
                    payload,
                    PhotorealSourceUpscaleCapability);
            recoveredPhotorealSourceUpscaleSupported =
                recoveredPhotorealSourceUpscaleSupported
                || HasEnhancementCapability(
                    payload,
                    RecoveredPhotorealSourceUpscaleCapability);
            photorealSeedControlSupported = photorealSeedControlSupported
                || HasEnhancementCapability(
                    payload,
                    PhotorealSeedControlCapability);
        }
        if (photorealSupported
            && enqueueNextSupported
            && photorealSourceUpscaleSupported
            && recoveredPhotorealSourceUpscaleSupported
            && photorealSeedControlSupported)
        {
            return readiness;
        }

        var missingCapabilities = new List<string>(5);
        if (!photorealSupported)
            missingCapabilities.Add("this photoreal settings format");
        if (!enqueueNextSupported)
            missingCapabilities.Add("atomic enqueue-next");
        if (!photorealSourceUpscaleSupported)
            missingCapabilities.Add("photoreal-output upscaling");
        if (!recoveredPhotorealSourceUpscaleSupported)
            missingCapabilities.Add("Recovered photoreal-output upscaling");
        if (!photorealSeedControlSupported)
            missingCapabilities.Add("fixed photoreal seeds");
        string missing = string.Join(", ", missingCapabilities);
        return new EnhancementApiResponse(
            false,
            426,
            health.Payload,
            $"The running H25 companion does not support {missing}. Restart H25 first; no job was added.");
    }

    private static bool HasPhotorealPromptControlsCapability(JsonElement payload)
        => HasEnhancementCapability(payload, PhotorealPromptControlsCapability);

    private static bool HasEnhancementCapability(JsonElement payload, string capability)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty(capability, out JsonElement supported)
            && supported.ValueKind == JsonValueKind.True;

    private static Func<JsonElement, string?>? CreateImageEnhancementHealthValidator(
        string operation,
        bool enqueueNext,
        bool requiresPhotorealSourceUpscale = false,
        bool requiresRecoveredPhotorealSourceUpscale = false,
        bool requiresPhotorealSeedControl = false)
    {
        bool needsPhotorealControls = string.Equals(
            operation,
            "photoreal",
            StringComparison.Ordinal);
        if (!needsPhotorealControls
            && !enqueueNext
            && !requiresPhotorealSourceUpscale
            && !requiresRecoveredPhotorealSourceUpscale
            && !requiresPhotorealSeedControl)
        {
            return null;
        }

        return payload =>
        {
            var missingCapabilities = new List<string>(5);
            if (needsPhotorealControls
                && !HasEnhancementCapability(payload, PhotorealPromptControlsCapability))
            {
                missingCapabilities.Add("this photoreal settings format");
            }
            if (enqueueNext
                && !HasEnhancementCapability(payload, AtomicImageEnqueueNextCapability))
            {
                missingCapabilities.Add("atomic enqueue-next");
            }
            if (requiresPhotorealSourceUpscale
                && !HasEnhancementCapability(payload, PhotorealSourceUpscaleCapability))
            {
                missingCapabilities.Add("photoreal-output upscaling");
            }
            if (requiresRecoveredPhotorealSourceUpscale
                && !HasEnhancementCapability(
                    payload,
                    RecoveredPhotorealSourceUpscaleCapability))
            {
                missingCapabilities.Add("Recovered photoreal-output upscaling");
            }
            if (requiresPhotorealSeedControl
                && !HasEnhancementCapability(payload, PhotorealSeedControlCapability))
            {
                missingCapabilities.Add("fixed photoreal seeds");
            }
            return missingCapabilities.Count == 0
                ? null
                : $"The running H25 companion does not support {string.Join(", ", missingCapabilities)}. Restart H25 first; no job was added.";
        };
    }

    private static Func<JsonElement, string?> CreateEnhancementCapabilityHealthValidator(
        string capability,
        string capabilityLabel)
        => payload => HasEnhancementCapability(payload, capability)
            ? null
            : $"The running H25 companion does not support {capabilityLabel}. Restart H25 first; no job was added.";

    private async Task<EnhancementEnqueueProbe> ProbeEnhancementEnqueueBackendAsync(
        CancellationToken token)
    {
        long deadline = Environment.TickCount64 + DurableEnqueueActionDeadlineMilliseconds;
        EnhancementApiResponse health = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            token: token,
            timeoutMilliseconds: DurableEnqueueActionDeadlineMilliseconds);
        return new EnhancementEnqueueProbe(
            EnhancementEnqueueProbePolicy.Classify(
                health.Ok,
                health.StatusCode,
                health.Payload),
            health.Payload,
            deadline);
    }

    private static int RemainingEnhancementEnqueueActionMilliseconds(
        EnhancementEnqueueProbe probe)
        => (int)Math.Clamp(
            probe.ActionDeadlineTick - Environment.TickCount64,
            0,
            DurableEnqueueActionDeadlineMilliseconds);

    private static EnhancementApiResponse? ValidateEnhancementEnqueueProbe(
        EnhancementEnqueueProbe probe,
        Func<JsonElement, string?>? healthValidator,
        bool requireExactHealthValidation)
    {
        if (healthValidator is null)
            return null;
        if (!EnhancementEnqueueProbePolicy.AllowsFeatureValidation(probe.Mode))
        {
            return requireExactHealthValidation
                ? new EnhancementApiResponse(
                    false,
                    426,
                    probe.HealthPayload,
                    "The running H25 companion cannot prove support for this request. Restart H25 first; no job was added.")
                : null;
        }
        if (probe.HealthPayload is not JsonElement healthPayload)
        {
            return new EnhancementApiResponse(
                false,
                426,
                null,
                "The running H25 companion cannot prove support for this request. Restart H25 first; no job was added.");
        }

        string? error = healthValidator(healthPayload);
        return string.IsNullOrWhiteSpace(error)
            ? null
            : new EnhancementApiResponse(false, 426, healthPayload, error);
    }

    private async Task<EnhancementApiResponse> SendEnhancementEnqueueAsync(
        object? body,
        string queuePlacement = "last",
        string? retryJobId = null,
        bool includeQueuePlacementInBody = true,
        CancellationToken token = default,
        Func<JsonElement, string?>? healthValidator = null,
        bool requireExactHealthValidation = false,
        string? recoverySourceIdentity = null)
    {
        string route = retryJobId is null
            ? "api/enhance/jobs"
            : $"api/enhance/jobs/{Uri.EscapeDataString(retryJobId)}/retry";
        EnhancementEnqueueProbe probe =
            await ProbeEnhancementEnqueueBackendAsync(token);
        EnhancementApiResponse? validationFailure =
            ValidateEnhancementEnqueueProbe(
                probe,
                healthValidator,
                requireExactHealthValidation);
        if (validationFailure is not null)
            return validationFailure;
        EnhancementEnqueueInboxItem item;
        try
        {
            item = EnhancementEnqueueInboxStore.CreateItem(
                body,
                queuePlacement,
                batchIndex: 0,
                kind: retryJobId is null ? "create" : "retry",
                retryJobId: retryJobId,
                includeQueuePlacementInBody: includeQueuePlacementInBody);
            _ = EnhancementEnqueueInboxStore.Publish(
                ResolvedEnhancementJobsPath,
                [item]);
        }
        catch (Exception ex) when (
            ex is IOException
                or Win32Exception
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or JsonException)
        {
            return new EnhancementApiResponse(
                false,
                0,
                null,
                "The AI queue reservation could not be saved locally. Nothing was submitted; try again.");
        }

        if (!EnhancementEnqueueProbePolicy.AllowsImmediateNudge(probe.Mode))
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                recoverySourceIdentity);
            return SavedForDeliveryResponse(item);
        }

        int remaining = RemainingEnhancementEnqueueActionMilliseconds(probe);
        if (remaining <= 0)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                recoverySourceIdentity);
            return SavedForDeliveryResponse(item);
        }
        EnhancementApiResponse nudge = await SendEnhancementApiAsync(
            HttpMethod.Post,
            route,
            token: token,
            exactBodyJson: item.BodyJson,
            idempotencyKey: item.RequestId,
            timeoutMilliseconds: remaining);
        EnhancementApiResponse normalized =
            NormalizeDurableEnqueueResponse(nudge, item);
        if (normalized.SavedForDelivery)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                recoverySourceIdentity);
        }
        return normalized;
    }

    private async Task<DurableEnhancementBatchResponse>
        TrySendDurableEnhancementBatchAsync(
            IReadOnlyList<object?> bodies,
            string queuePlacement = "last",
            CancellationToken token = default,
            Action? onFirstPublish = null,
            Func<bool>? shouldStopBeforeFirstPublish = null)
    {
        if (bodies.Count == 0)
            return new DurableEnhancementBatchResponse([], 0, 0);
        EnhancementEnqueueProbe probe =
            await ProbeEnhancementEnqueueBackendAsync(token);
        EnhancementApiResponse unsavedFailure = new(
            false,
            0,
            null,
            "The AI queue reservations could not be saved locally. Nothing was submitted; try again.");
        EnhancementApiResponse stoppedResponse = new(
            false,
            499,
            null,
            "Batch submission was stopped before the reservation was saved.");
        var responses = Enumerable.Repeat(unsavedFailure, bodies.Count).ToArray();
        var publishedItems = new List<(int GlobalIndex, EnhancementEnqueueInboxItem Item)>(
            bodies.Count);
        bool firstPublishReported = false;
        bool abortRemainingPublishes = false;

        bool PublishRange(int start, int count)
        {
            if (!firstPublishReported
                && shouldStopBeforeFirstPublish?.Invoke() == true)
            {
                for (int index = start; index < bodies.Count; index++)
                    responses[index] = stoppedResponse;
                abortRemainingPublishes = true;
                return false;
            }

            try
            {
                EnhancementEnqueueInboxItem[] chunk = Enumerable.Range(0, count)
                    .Select(localIndex => EnhancementEnqueueInboxStore.CreateItem(
                        bodies[start + localIndex],
                        queuePlacement,
                        localIndex))
                    .ToArray();
                _ = EnhancementEnqueueInboxStore.Publish(
                    ResolvedEnhancementJobsPath,
                    chunk);
                for (int localIndex = 0; localIndex < chunk.Length; localIndex++)
                {
                    int globalIndex = start + localIndex;
                    responses[globalIndex] = SavedForDeliveryResponse(chunk[localIndex]);
                    publishedItems.Add((globalIndex, chunk[localIndex]));
                }
                if (!firstPublishReported)
                {
                    firstPublishReported = true;
                    onFirstPublish?.Invoke();
                }
                return true;
            }
            catch (EnhancementEnqueuePayloadTooLargeException) when (count > 1)
            {
                int firstCount = count / 2;
                bool firstPublished = PublishRange(start, firstCount);
                if (!firstPublished || abortRemainingPublishes)
                    return false;
                return PublishRange(start + firstCount, count - firstCount);
            }
            catch (EnhancementEnqueuePayloadTooLargeException ex)
            {
                responses[start] = new EnhancementApiResponse(
                    false,
                    413,
                    null,
                    ex.Message);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or Win32Exception
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or JsonException)
            {
                abortRemainingPublishes = true;
                return false;
            }
        }

        for (int start = 0; start < bodies.Count; start += EnhancementEnqueueInboxStore.MaximumItemsPerEnvelope)
        {
            int count = EnhancementEnqueueProbePolicy.NextEnvelopeItemCount(
                bodies.Count - start);
            if (!PublishRange(start, count))
                break;
        }

        if (!EnhancementEnqueueProbePolicy.AllowsImmediateNudge(probe.Mode)
            && publishedItems.Count > 0)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(null);
        }

        int nudgeCount = 0;
        if (EnhancementEnqueueProbePolicy.AllowsImmediateNudge(probe.Mode))
        {
            foreach ((int globalIndex, EnhancementEnqueueInboxItem item) in publishedItems)
            {
                int remaining = RemainingEnhancementEnqueueActionMilliseconds(probe);
                if (remaining <= 0 || token.IsCancellationRequested)
                    break;

                nudgeCount++;
                EnhancementApiResponse nudge = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    "api/enhance/jobs",
                    token: token,
                    exactBodyJson: item.BodyJson,
                    idempotencyKey: item.RequestId,
                    timeoutMilliseconds: remaining);
                responses[globalIndex] = NormalizeDurableEnqueueResponse(nudge, item);
            }
            if (publishedItems.Any(entry =>
                    responses[entry.GlobalIndex].SavedForDelivery))
            {
                KickEnhancementCompanionRecoveryAfterDurablePublish(null);
            }
        }

        return new DurableEnhancementBatchResponse(
            responses,
            nudgeCount,
            publishedItems.Count);
    }

    private void KickEnhancementCompanionRecoveryAfterDurablePublish(
        string? sourceIdentity)
    {
        if (!_usingDefaultModalEnhancementSender
            || _enhancementCompanionLifetimeCts.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _ = await EnsureEnhancementCompanionReadyForExplicitActionAsync(
                    sourceIdentity,
                    _enhancementCompanionLifetimeCts.Token);
            }
            catch
            {
                // The reservation is already durable. Recovery is best-effort.
            }
        });
    }

    private static EnhancementApiResponse NormalizeDurableEnqueueResponse(
        EnhancementApiResponse response,
        EnhancementEnqueueInboxItem item)
    {
        if (response.Ok
            && EnhancementEnqueueProbePolicy.HasMatchingDurableReceipt(
                response.Payload,
                item.RequestId))
        {
            return response;
        }
        if (response.StatusCode is >= 400 and < 500
            && response.StatusCode is not (408 or 425 or 429))
        {
            return response;
        }
        return SavedForDeliveryResponse(item);
    }

    private static EnhancementApiResponse SavedForDeliveryResponse(
        EnhancementEnqueueInboxItem item)
        => new(
            true,
            202,
            null,
            "",
            SavedForDelivery: true,
            DeliveryRequestId: item.RequestId);

    private static bool IsReadyEnhancementCompanionResponse(EnhancementApiResponse response)
        => response.Ok
            && response.Payload is JsonElement payload
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("jobs", out JsonElement jobs)
            && jobs.ValueKind == JsonValueKind.Array;

    private bool TryStartOwnedEnhancementCompanion(out string error)
    {
        error = "";
        Uri endpoint = ResolveBrowserEnhancementBaseUri();
        if (_startEnhancementCompanionForSmoke is not null)
        {
            (bool started, string injectedError) = _startEnhancementCompanionForSmoke(endpoint);
            if (started)
            {
                _enhancementCompanionLaunchAttemptCount++;
                return true;
            }
            error = injectedError;
            return false;
        }

        ValidatedEnhancementCompanionRoot? companionRoot = ResolveEnhancementCompanionRoot();
        if (companionRoot is null)
        {
            error = "Aibos could not find the H25 Browser companion beside this portable build. Open the H25 copy of Aibos or set AIBOS_H25_COMPANION_ROOT.";
            return false;
        }

        try
        {
            if (endpoint.Scheme != Uri.UriSchemeHttp
                || !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal))
            {
                error = "Automatic local AI startup requires an http://127.0.0.1 loopback endpoint.";
                return false;
            }
            ValidatedNodeExecutable? nodeExecutable = ResolveNodeExecutablePath();
            if (nodeExecutable is null)
            {
                error = "Aibos could not find an installed Node.js executable for the local AI companion.";
                return false;
            }
            ProcessStartInfo startInfo = CreateEnhancementCompanionStartInfo(
                nodeExecutable,
                companionRoot,
                endpoint);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false,
            };
            if (!process.Start())
            {
                process.Dispose();
                error = "Windows did not start the local AI companion.";
                return false;
            }

            _ownedEnhancementCompanion = process;
            _enhancementCompanionLaunchAttemptCount++;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            error = $"Aibos could not start the local AI companion: {ex.Message}";
            return false;
        }
    }

    private static ProcessStartInfo CreateEnhancementCompanionStartInfo(
        ValidatedNodeExecutable nodeExecutable,
        ValidatedEnhancementCompanionRoot companionRoot,
        Uri endpoint)
    {
        var startInfo = new ProcessStartInfo
        {
            // ResolveNodeExecutablePath only accepts canonical node.exe files
            // below the Windows Program Files roots.
            // codeql[cs/command-line-injection]
            FileName = nodeExecutable.Path, // lgtm[cs/command-line-injection]
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        startInfo.ArgumentList.Add(companionRoot.LauncherPath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(endpoint.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["PVU_NO_OPEN"] = "1";
        startInfo.Environment["PVU_COMFY_AUTOSTART"] = "0";
        // Do not set PVU_OWNER_PID. The companion owns the durable FIFO worker
        // after an explicit AI action and must outlive the WPF viewer process.
        startInfo.Environment.Remove("PVU_OWNER_PID");
        return startInfo;
    }

    private static ValidatedEnhancementCompanionRoot? ResolveEnhancementCompanionRoot()
        => ResolveEnhancementCompanionRoot(
            Environment.GetEnvironmentVariable("AIBOS_H25_COMPANION_ROOT"),
            AppContext.BaseDirectory,
            UseLegacyNextCompanionLauncher());

    private static bool UseLegacyNextCompanionLauncher()
        => string.Equals(
            Environment.GetEnvironmentVariable(
                "AIBOS_H25_LEGACY_NEXT_COMPANION"),
            "1",
            StringComparison.Ordinal);

    private static ValidatedEnhancementCompanionRoot? ResolveEnhancementCompanionRoot(
        string? configuredRoot,
        string appBaseDirectory,
        bool useLegacyNextLauncher)
    {
        // An explicitly configured root is authoritative and must itself be
        // the H25 project root. Never walk its parents or silently fall back.
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return TryValidateEnhancementCompanionRoot(
                configuredRoot,
                useLegacyNextLauncher,
                out ValidatedEnhancementCompanionRoot? configured)
                ? configured
                : null;

        string? current;
        try
        {
            current = Path.GetFullPath(appBaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        // Portable builds can live below the H25 root. AppContext.BaseDirectory
        // is controlled by the launched app, unlike Environment.CurrentDirectory.
        for (int depth = 0; depth < 12 && current is not null; depth++)
        {
            if (TryValidateEnhancementCompanionRoot(
                    current,
                    useLegacyNextLauncher,
                    out ValidatedEnhancementCompanionRoot? validated))
                return validated;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static bool TryValidateEnhancementCompanionRoot(
        string candidateRoot,
        bool useLegacyNextLauncher,
        out ValidatedEnhancementCompanionRoot? validatedRoot)
    {
        validatedRoot = null;
        try
        {
            string lexicalRoot = Path.GetFullPath(candidateRoot);
            if (!Directory.Exists(lexicalRoot))
                return false;

            string canonicalRoot = ResolveFinalPathCore(lexicalRoot);
            if (!Directory.Exists(canonicalRoot))
                return false;

            string packagePath = Path.Combine(canonicalRoot, "package.json");
            string projectPath = Path.Combine(canonicalRoot, "project.toml");
            string launcherPath = Path.Combine(
                canonicalRoot,
                "scripts",
                useLegacyNextLauncher
                    ? LegacyNextCompanionLauncherFileName
                    : EnhancementCompanionLauncherFileName);
            foreach (string requiredPath in new[] { packagePath, projectPath, launcherPath })
            {
                if (!File.Exists(requiredPath))
                    return false;
                string canonicalRequiredPath = ResolveFinalPathCore(requiredPath);
                if (!IsPathInside(canonicalRequiredPath, canonicalRoot))
                    return false;
            }

            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(packagePath));
            JsonElement packageRoot = package.RootElement;
            bool packageIdentity = packageRoot.ValueKind == JsonValueKind.Object
                && packageRoot.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(
                    name.GetString(),
                    "h000025-photoviewer",
                    StringComparison.Ordinal)
                && packageRoot.TryGetProperty("private", out JsonElement privateValue)
                && privateValue.ValueKind is JsonValueKind.True;
            if (!packageIdentity)
                return false;

            string[] projectLines = File.ReadAllLines(projectPath);
            bool projectId = projectLines.Any(static line =>
                string.Equals(line.Trim(), "id = \"H000025\"", StringComparison.Ordinal));
            bool projectName = projectLines.Any(static line =>
                string.Equals(line.Trim(), "name = \"PhotoViewer\"", StringComparison.Ordinal));
            if (!projectId || !projectName)
                return false;

            validatedRoot = new(canonicalRoot, ResolveFinalPathCore(launcherPath));
            return true;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            UnauthorizedAccessException or
            IOException or
            JsonException)
        {
            return false;
        }
    }

    private static ValidatedNodeExecutable? ResolveNodeExecutablePath()
    {
        var candidates = new List<(string ProgramFilesRoot, string CandidatePath)>();
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add((programFiles, Path.Combine(programFiles, "nodejs", "node.exe")));
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add((programFilesX86, Path.Combine(programFilesX86, "nodejs", "node.exe")));

        foreach ((string programFilesRoot, string candidatePath) in candidates)
        {
            if (TryValidateNodeExecutablePath(
                    programFilesRoot,
                    candidatePath,
                    out ValidatedNodeExecutable? validated))
                return validated;
        }
        return null;
    }

    private static bool TryValidateNodeExecutablePath(
        string programFilesRoot,
        string candidatePath,
        out ValidatedNodeExecutable? validatedExecutable)
    {
        validatedExecutable = null;
        try
        {
            string lexicalProgramFilesRoot = Path.GetFullPath(programFilesRoot);
            if (!Directory.Exists(lexicalProgramFilesRoot))
                return false;

            string expectedCandidate = Path.GetFullPath(
                Path.Combine(lexicalProgramFilesRoot, "nodejs", "node.exe"));
            if (!string.Equals(
                    Path.GetFullPath(candidatePath),
                    expectedCandidate,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(expectedCandidate))
            {
                return false;
            }

            string canonicalProgramFilesRoot = ResolveFinalPathCore(lexicalProgramFilesRoot);
            string canonicalNodeDirectory = ResolveFinalPathCore(
                Path.Combine(lexicalProgramFilesRoot, "nodejs"));
            string canonicalCandidate = ResolveFinalPathCore(expectedCandidate);
            if (!Directory.Exists(canonicalProgramFilesRoot)
                || !Directory.Exists(canonicalNodeDirectory)
                || !File.Exists(canonicalCandidate)
                || !IsPathInside(canonicalNodeDirectory, canonicalProgramFilesRoot)
                || !string.Equals(
                    Path.GetDirectoryName(canonicalCandidate),
                    canonicalNodeDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(canonicalCandidate),
                    "node.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            validatedExecutable = new(canonicalCandidate);
            return true;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            NotSupportedException or
            UnauthorizedAccessException or
            IOException)
        {
            return false;
        }
    }

    private void StopOwnedEnhancementCompanion()
    {
        Process? process = Interlocked.Exchange(ref _ownedEnhancementCompanion, null);
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Only the exact process tree created by this WPF instance is owned.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ReleaseOwnedEnhancementCompanion()
    {
        // Disposing a Process wrapper does not stop the OS process. Once the
        // loopback companion is ready, it is an independent durable worker so
        // queued/running jobs continue while Aibos is closed.
        Interlocked.Exchange(ref _ownedEnhancementCompanion, null)?.Dispose();
    }

    private void CancelOwnedEnhancementCompanionLifetime()
    {
        try
        {
            _enhancementCompanionLifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public int EnhancementCompanionLaunchAttemptCountForSmoke => _enhancementCompanionLaunchAttemptCount;
    public string? EnhancementCompanionLaunchErrorForSmoke => _enhancementCompanionLaunchError;
    public static string? ResolveEnhancementCompanionRootForSmoke()
        => ResolveEnhancementCompanionRoot()?.RootPath;
    public static string? ResolveEnhancementCompanionRootForSmoke(
        string? configuredRoot,
        string appBaseDirectory)
        => ResolveEnhancementCompanionRoot(
            configuredRoot,
            appBaseDirectory,
            useLegacyNextLauncher: false)?.RootPath;
    public static string? ResolveEnhancementCompanionLauncherForSmoke(
        string? configuredRoot,
        string appBaseDirectory,
        bool useLegacyNextLauncher)
        => ResolveEnhancementCompanionRoot(
            configuredRoot,
            appBaseDirectory,
            useLegacyNextLauncher)?.LauncherPath;
    public static string? ResolveNodeExecutablePathForSmoke() => ResolveNodeExecutablePath()?.Path;
    public static bool ValidateNodeExecutableCandidateForSmoke(
        string programFilesRoot,
        string candidatePath)
        => TryValidateNodeExecutablePath(programFilesRoot, candidatePath, out _);
    public static EnhancementCompanionLaunchContractSmokeSnapshot
        EnhancementCompanionLaunchContractForSmoke(
            bool useLegacyNextLauncher = false)
    {
        ProcessStartInfo startInfo = CreateEnhancementCompanionStartInfo(
            new ValidatedNodeExecutable(@"C:\Program Files\nodejs\node.exe"),
            new ValidatedEnhancementCompanionRoot(
                @"C:\fixture\H000025_PhotoViewer",
                Path.Combine(
                    @"C:\fixture\H000025_PhotoViewer\scripts",
                    useLegacyNextLauncher
                        ? LegacyNextCompanionLauncherFileName
                        : EnhancementCompanionLauncherFileName)),
            new Uri("http://127.0.0.1:3000"));
        return new(
            startInfo.UseShellExecute,
            startInfo.CreateNoWindow,
            startInfo.RedirectStandardOutput,
            startInfo.RedirectStandardError,
            !string.IsNullOrEmpty(startInfo.WorkingDirectory),
            startInfo.Environment.ContainsKey("PVU_OWNER_PID"),
            startInfo.Environment["PVU_NO_OPEN"],
            startInfo.Environment["PVU_COMFY_AUTOSTART"],
            Path.GetFileName(startInfo.ArgumentList[0]));
    }
    public void ConfigureEnhancementCompanionAutoStartForSmoke(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        Func<Uri, (bool Started, string Error)> starter)
    {
        _modalEnhancementSender = sender;
        _usingDefaultModalEnhancementSender = true;
        _startEnhancementCompanionForSmoke = starter;
    }

    public async Task<bool> EnsureEnhancementCompanionForExplicitActionForSmokeAsync()
    {
        EnhancementApiResponse response = await EnsureEnhancementCompanionReadyForExplicitActionAsync();
        return response.Ok;
    }
    public async Task<bool> EnsurePhotorealCompanionForExplicitActionForSmokeAsync()
    {
        EnhancementApiResponse response = await EnsurePhotorealCompanionReadyForExplicitActionAsync();
        return response.Ok;
    }
    public static bool HasPhotorealPromptControlsCapabilityForSmoke(JsonElement payload)
        => HasPhotorealPromptControlsCapability(payload);
    public static bool HasRecoveredPhotorealSourceUpscaleCapabilityForSmoke(
        JsonElement payload)
        => HasEnhancementCapability(
            payload,
            RecoveredPhotorealSourceUpscaleCapability);
}

internal sealed record ValidatedEnhancementCompanionRoot(
    string RootPath,
    string LauncherPath);

internal sealed record ValidatedNodeExecutable(string Path);

public sealed record EnhancementCompanionLaunchContractSmokeSnapshot(
    bool UseShellExecute,
    bool CreateNoWindow,
    bool RedirectStandardOutput,
    bool RedirectStandardError,
    bool HasExplicitWorkingDirectory,
    bool HasExternalOwnerPid,
    string? NoOpen,
    string? ComfyAutostart,
    string LauncherFileName);
