using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementCompanionReadyTimeoutMilliseconds = 120_000;
    private const int EnhancementCompanionProbeDelayMilliseconds = 450;
    private const int EnhancementCompanionIdentityBusyRetryAttempts = 3;
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
    private const string DisplayedManagedVideoSourceCapability =
        "displayedManagedVideoSourceV1";
    private const int DurableEnqueueActionDeadlineMilliseconds = 2_000;
    private const int DurableEnqueueRecoveryAttempts = 3;
    private const string EnhancementCompanionAuthProtocol =
        "aibos.companion-auth/v1";
    private const string EnhancementCompanionRequestAuthProtocol =
        "aibos.companion-request/v2";
    private const string EnhancementCompanionTunnelProtocol =
        "aibos.companion-tunnel/v1";
    private const string EnhancementCompanionTunnelKeyProtocol =
        "aibos.companion-tunnel-key/v1";
    private const string EnhancementCompanionResponseAuthProtocol =
        "aibos.companion-response/v1";
    private const string EnhancementCompanionIdentityRoute =
        "api/enhance/identity";
    private const string EnhancementCompanionSecureRoute =
        "api/enhance/secure";
    private const string EnhancementCompanionWakeRoute =
        "api/enhance/inbox/wake";
    private const string EnhancementCompanionQueueRecoveryRoute =
        "api/enhance/queue/recover";
    private const string EnhancementCompanionChallengeHeader =
        "X-Aibos-Companion-Challenge";
    private const string EnhancementCompanionTimestampHeader =
        "X-Aibos-Auth-Timestamp";
    private const string EnhancementCompanionNonceHeader =
        "X-Aibos-Auth-Nonce";
    private const string EnhancementCompanionSignatureHeader =
        "X-Aibos-Auth-Signature";
    private const string EnhancementCompanionInstanceHeader =
        "X-Aibos-Companion-Instance";
    private const string EnhancementCompanionEpochHeader =
        "X-Aibos-Companion-Epoch";
    private const string EnhancementCompanionResponseSignatureHeader =
        "X-Aibos-Response-Signature";
    private const int EnhancementCompanionIdentityResponseMaxBytes = 4096;
    private const int EnhancementCompanionMaximumSecureResponseBytes =
        48 * 1024 * 1024;
    private static readonly string EnhancementCompanionSmokeAuthToken =
        Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(
            "aibos-companion-smoke-token-v1")));
    private static readonly byte[] EnhancementCompanionAuthEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "aibos-companion-capability-dpapi/v1"));

    private readonly SemaphoreSlim _enhancementCompanionLaunchGate = new(1, 1);
    private readonly SemaphoreSlim _enhancementCompanionPassiveProbeGate = new(1, 1);
    private readonly CancellationTokenSource _enhancementCompanionLifetimeCts = new();
    private readonly object _enhancementCompanionDurableRecoverySync = new();
    private Process? _ownedEnhancementCompanion;
    private EnhancementCompanionProcessObservation?
        _ownedEnhancementCompanionObservation;
    private string? _enhancementCompanionLaunchError;
    private int _enhancementCompanionLaunchAttemptCount;
    private Func<Uri, (bool Started, string Error)>? _startEnhancementCompanionForSmoke;
    private string? _enhancementCompanionAuthToken;
    private string? _ownedEnhancementCompanionInstanceId;
    private bool _enhancementCompanionOwnershipVerified;
    private string? _verifiedEnhancementCompanionInstanceId;
    private string? _verifiedEnhancementCompanionServerStartedAtUtc;
    private bool _enhancementCompanionDurableRecoveryRequested;
    private bool _enhancementCompanionDurableRecoveryRunning;
    private string? _enhancementCompanionDurableRecoveryRequestId;
    private string? _enhancementCompanionDurableRecoverySourceIdentity;
    private sealed class EnhancementCompanionProcessObservation(
        int processId,
        long startedTimestamp)
    {
        internal const int Running = 0;
        internal const int StopRequested = 1;
        internal const int OwnershipReleased = 2;
        private int _disposition = Running;

        internal int ProcessId { get; } = processId;
        internal long StartedTimestamp { get; } = startedTimestamp;
        internal int Disposition => Volatile.Read(ref _disposition);

        internal void MarkStopRequested()
            => Interlocked.CompareExchange(
                ref _disposition,
                StopRequested,
                Running);

        internal void MarkOwnershipReleased()
            => Interlocked.CompareExchange(
                ref _disposition,
                OwnershipReleased,
                Running);
    }
    private sealed record EnhancementEnqueueProbe(
        EnhancementEnqueueBackendMode Mode,
        JsonElement? HealthPayload,
        long ActionDeadlineTick);
    private sealed record DurableEnhancementBatchResponse(
        EnhancementApiResponse[] Responses,
        int NudgeCount,
        int PublishedCount);
    private sealed record DurableEnhancementBatchItem(
        object? Body,
        string? RetryJobId);
    private sealed record EnhancementCompanionOwnershipProbe(
        bool Verified,
        bool TransportUnavailable,
        bool RetryableBusy,
        int StatusCode,
        JsonElement? Payload,
        string Error);

    private async Task<EnhancementApiResponse> EnsureEnhancementCompanionReadyForExplicitActionAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        EnhancementApiResponse readiness =
            await EnsureEnhancementCompanionApiReadyAsync(
                sourceIdentity,
                token);
        if (!readiness.Ok)
            return readiness;

        EnhancementApiResponse recovery = await SendEnhancementApiAsync(
            HttpMethod.Post,
            EnhancementCompanionQueueRecoveryRoute,
            token: token);
        return recovery.Ok ? readiness : recovery;
    }

    private async Task<EnhancementApiResponse> EnsureEnhancementCompanionApiReadyAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        _ = sourceIdentity;
        const string readinessRoute = "api/enhance/health";
        if (!_usingDefaultModalEnhancementSender)
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: token);
            return IsReadyEnhancementCompanionResponse(response)
                ? response
                : InvalidEnhancementCompanionReadiness(response);
        }
        if (!TryGetOrCreateEnhancementCompanionAuthToken(
                out string authToken,
                out string authError))
        {
            return new EnhancementApiResponse(false, 0, null, authError);
        }

        EnhancementCompanionOwnershipProbe ownership =
            await ProbeEnhancementCompanionOwnershipAsync(authToken, token);
        if (ownership.Verified)
        {
            _enhancementCompanionOwnershipVerified = true;
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: token);
            return IsReadyEnhancementCompanionResponse(response)
                ? response
                : InvalidEnhancementCompanionReadiness(response);
        }
        _enhancementCompanionOwnershipVerified = false;
        if (!ownership.TransportUnavailable && !ownership.RetryableBusy)
        {
            return new EnhancementApiResponse(
                false,
                ownership.StatusCode,
                ownership.Payload,
                ownership.Error);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            _enhancementCompanionLifetimeCts.Token);
        CancellationToken linkedToken = linkedCts.Token;
        await _enhancementCompanionLaunchGate.WaitAsync(linkedToken);
        try
        {
            ownership = await ProbeEnhancementCompanionOwnershipAsync(
                authToken,
                linkedToken);
            if (ownership.Verified)
            {
                _enhancementCompanionOwnershipVerified = true;
                EnhancementApiResponse response = await SendEnhancementApiAsync(
                    HttpMethod.Get,
                    readinessRoute,
                    token: linkedToken);
                return IsReadyEnhancementCompanionResponse(response)
                    ? response
                    : InvalidEnhancementCompanionReadiness(response);
            }
            if (!ownership.TransportUnavailable && !ownership.RetryableBusy)
            {
                return new EnhancementApiResponse(
                    false,
                    ownership.StatusCode,
                    ownership.Payload,
                    ownership.Error);
            }

            for (int attempt = 0;
                ownership.RetryableBusy
                    && attempt < EnhancementCompanionIdentityBusyRetryAttempts;
                attempt++)
            {
                await Task.Delay(
                    EnhancementCompanionProbeDelayMilliseconds,
                    linkedToken);
                ownership = await ProbeEnhancementCompanionOwnershipAsync(
                    authToken,
                    linkedToken);
                if (ownership.Verified)
                {
                    _enhancementCompanionOwnershipVerified = true;
                    EnhancementApiResponse response = await SendEnhancementApiAsync(
                        HttpMethod.Get,
                        readinessRoute,
                        token: linkedToken);
                    return IsReadyEnhancementCompanionResponse(response)
                        ? response
                        : InvalidEnhancementCompanionReadiness(response);
                }
                if (!ownership.TransportUnavailable && !ownership.RetryableBusy)
                {
                    return new EnhancementApiResponse(
                        false,
                        ownership.StatusCode,
                        ownership.Payload,
                        ownership.Error);
                }
            }
            if (ownership.RetryableBusy)
            {
                return new EnhancementApiResponse(
                    false,
                    ownership.StatusCode,
                    ownership.Payload,
                    ownership.Error);
            }

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
                ownership = await ProbeEnhancementCompanionOwnershipAsync(
                    authToken,
                    linkedToken);
                if (ownership.Verified)
                {
                    _enhancementCompanionOwnershipVerified = true;
                    EnhancementApiResponse response = await SendEnhancementApiAsync(
                        HttpMethod.Get,
                        readinessRoute,
                        token: linkedToken);
                    if (IsReadyEnhancementCompanionResponse(response))
                    {
                        _enhancementCompanionLaunchError = null;
                        return response;
                    }
                }
                else if (!ownership.TransportUnavailable && !ownership.RetryableBusy)
                {
                    string error = ownership.Error;
                    _enhancementCompanionLaunchError = error;
                    StopOwnedEnhancementCompanion();
                    return new EnhancementApiResponse(
                        false,
                        ownership.StatusCode,
                        ownership.Payload,
                        error);
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

    private async Task StartEnhancementCompanionApiForApplicationLaunchAsync()
    {
        if (ShouldSuppressEnhancementCompanionStartup(
                Environment.GetCommandLineArgs()))
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        AibosOperationLog.Write("companion.startup", "started");
        EnhancementApiResponse response =
            await EnsureEnhancementCompanionApiReadyAsync(
                token: _enhancementCompanionLifetimeCts.Token);
        long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (response.Ok)
        {
            AibosOperationLog.Write(
                "companion.startup",
                "ready",
                elapsedMilliseconds,
                response.StatusCode);
        }
        else
        {
            _enhancementCompanionLaunchError = response.Error;
            AibosOperationLog.Write(
                "companion.startup",
                "failed",
                elapsedMilliseconds,
                response.StatusCode,
                ClassifyEnhancementCompanionStartupError(response.Error));
        }
    }

    private static bool ShouldSuppressEnhancementCompanionStartup(
        IReadOnlyList<string> args)
        => App.IsAutomationInvocation(args);

    internal static bool EnhancementCompanionStartupSuppressedForSmoke(
        IReadOnlyList<string> args)
        => ShouldSuppressEnhancementCompanionStartup(args);

    private async Task<EnhancementApiResponse> SendPassiveEnhancementReadAsync(
        string relativePath,
        CancellationToken token = default,
        int? timeoutMilliseconds = null,
        int? maxResponseBytes = null,
        string? timeoutError = null)
    {
        if (!_usingDefaultModalEnhancementSender)
        {
            return await SendEnhancementApiAsync(
                HttpMethod.Get,
                relativePath,
                token: token,
                timeoutMilliseconds: timeoutMilliseconds,
                maxResponseBytes: maxResponseBytes,
                timeoutError: timeoutError);
        }

        EnhancementApiResponse? verificationFailure =
            await EnsureEnhancementCompanionOwnershipForPassiveReadAsync(token);
        if (verificationFailure is not null)
            return verificationFailure;

        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            relativePath,
            token: token,
            timeoutMilliseconds: timeoutMilliseconds,
            maxResponseBytes: maxResponseBytes,
            timeoutError: timeoutError);
        if (!ShouldReverifyEnhancementCompanionAfterAuthenticatedRequest(response))
            return response;

        verificationFailure =
            await EnsureEnhancementCompanionOwnershipForPassiveReadAsync(token);
        if (verificationFailure is not null)
            return verificationFailure;

        return await SendEnhancementApiAsync(
            HttpMethod.Get,
            relativePath,
            token: token,
            timeoutMilliseconds: timeoutMilliseconds,
            maxResponseBytes: maxResponseBytes,
            timeoutError: timeoutError);
    }

    private async Task<EnhancementApiResponse?>
        EnsureEnhancementCompanionOwnershipForPassiveReadAsync(
            CancellationToken token)
    {
        if (_enhancementCompanionOwnershipVerified)
            return null;

        bool gateEntered = false;
        try
        {
            await _enhancementCompanionPassiveProbeGate.WaitAsync(token);
            gateEntered = true;
            if (_enhancementCompanionOwnershipVerified)
                return null;
            if (!TryGetOrCreateEnhancementCompanionAuthToken(
                    out string authToken,
                    out string authError))
            {
                return new EnhancementApiResponse(false, 0, null, authError);
            }

            EnhancementCompanionOwnershipProbe ownership =
                await ProbeEnhancementCompanionOwnershipAsync(authToken, token);
            if (ownership.Verified)
            {
                _enhancementCompanionOwnershipVerified = true;
                return null;
            }

            _enhancementCompanionOwnershipVerified = false;
            return new EnhancementApiResponse(
                false,
                ownership.StatusCode,
                ownership.Payload,
                ownership.Error);
        }
        catch (OperationCanceledException)
        {
            return new EnhancementApiResponse(
                false,
                0,
                null,
                "The passive local AI status read was canceled.");
        }
        finally
        {
            if (gateEntered)
                _enhancementCompanionPassiveProbeGate.Release();
        }
    }

    private static bool ShouldReverifyEnhancementCompanionAfterAuthenticatedRequest(
        EnhancementApiResponse response)
        => !response.Ok
            && !response.InnerStatusAuthoritative
            && response.StatusCode is 0 or 401 or 403;

    // Only use this for operations whose exact request can be replayed after a
    // lost response without applying the logical mutation twice.
    private async Task<EnhancementApiResponse>
        SendIdempotentEnhancementMutationAsync(
            HttpMethod method,
            string relativePath,
            object? body = null,
            CancellationToken token = default)
    {
        string? exactBodyJson = body is null
            ? null
            : JsonSerializer.Serialize(body);
        if (_usingDefaultModalEnhancementSender
            && !_enhancementCompanionOwnershipVerified)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionApiReadyAsync(token: token);
            if (!readiness.Ok)
                return readiness;
        }

        EnhancementApiResponse response = await SendEnhancementApiAsync(
            method,
            relativePath,
            token: token,
            exactBodyJson: exactBodyJson);
        if (!_usingDefaultModalEnhancementSender
            || token.IsCancellationRequested
            || !ShouldReverifyEnhancementCompanionAfterAuthenticatedRequest(response))
        {
            return response;
        }

        EnhancementApiResponse reconnect =
            await EnsureEnhancementCompanionApiReadyAsync(token: token);
        if (!reconnect.Ok)
            return reconnect;

        return await SendEnhancementApiAsync(
            method,
            relativePath,
            token: token,
            exactBodyJson: exactBodyJson);
    }

    private void InvalidateEnhancementCompanionOwnershipIfCurrent(
        string? requestInstanceId,
        string? requestServerStartedAtUtc)
    {
        if (!_usingDefaultModalEnhancementSender
            || string.IsNullOrWhiteSpace(requestInstanceId)
            || string.IsNullOrWhiteSpace(requestServerStartedAtUtc))
        {
            return;
        }
        if (string.Equals(
                requestInstanceId,
                _verifiedEnhancementCompanionInstanceId,
                StringComparison.Ordinal)
            && string.Equals(
                requestServerStartedAtUtc,
                _verifiedEnhancementCompanionServerStartedAtUtc,
                StringComparison.Ordinal))
        {
            _enhancementCompanionOwnershipVerified = false;
        }
    }

    private static string ClassifyEnhancementCompanionStartupError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "request_failed";
        if (error.Contains("busy", StringComparison.OrdinalIgnoreCase))
            return "identity_busy";
        if (error.Contains("untrusted process", StringComparison.OrdinalIgnoreCase)
            || error.Contains("proved ownership", StringComparison.OrdinalIgnoreCase))
            return "ownership_rejected";
        if (error.Contains("could not find", StringComparison.OrdinalIgnoreCase))
            return "root_not_found";
        if (error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "authentication_failed";
        if (error.Contains("stopped before", StringComparison.OrdinalIgnoreCase))
            return "process_stopped";
        if (error.Contains("within two minutes", StringComparison.OrdinalIgnoreCase))
            return "startup_timeout";
        if (error.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            return "canceled";
        return "request_failed";
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
            $"The Aibos Image local AI service does not support {capabilityLabel}. Restart the local AI service first; no job was added.");
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
            $"The Aibos Image local AI service does not support {missing}. Restart the local AI service first; no job was added.");
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
                : $"The Aibos Image local AI service does not support {string.Join(", ", missingCapabilities)}. Restart the local AI service first; no job was added.";
        };
    }

    private static Func<JsonElement, string?> CreateEnhancementCapabilityHealthValidator(
        string capability,
        string capabilityLabel)
        => payload => HasEnhancementCapability(payload, capability)
            ? null
            : $"The Aibos Image local AI service does not support {capabilityLabel}. Restart the local AI service first; no job was added.";

    private readonly record struct MiniMaxH3VideoCapabilityState(
        bool Ready,
        string? ReasonCode);

    private static readonly string[] MiniMaxH3VideoReasonCodes =
    [
        "MINIMAX_H3_WRITER_DISABLED",
        "MINIMAX_H3_RUNTIME_SEAL_INVALID",
        "MINIMAX_H3_RUNTIME_MANIFEST_INVALID",
        "MINIMAX_H3_LICENSE_NOT_ACCEPTED",
        "MINIMAX_H3_MODELS_UNVERIFIED",
        "MINIMAX_H3_WORKFLOW_UNVERIFIED",
        "MINIMAX_H3_GPU_CANARY_UNVERIFIED",
        "MINIMAX_H3_BACKEND_CONFIG_INVALID",
    ];

    private static bool TryParseMiniMaxH3VideoCapability(
        JsonElement payload,
        out MiniMaxH3VideoCapabilityState state)
    {
        state = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoV2")
            || !capabilities.TryGetProperty("videoV2", out JsonElement capability)
            || capability.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                capability,
                "contractId",
                "protocol",
                "readerReady",
                "writerEnabled",
                "backendConfigured",
                "runtimeSealVerified",
                "runtimeManifestVerified",
                "licenseAccepted",
                "modelsVerified",
                "workflowConfigured",
                "gpuCanaryVerified",
                "ready",
                "state",
                "reasonCode",
                "presetId",
                "backendId",
                "workflowRevision",
                "runtimeMode",
                "profile")
            || !VideoV2ExactString(
                capability,
                "contractId",
                MiniMaxH3VideoContractId)
            || !VideoV2ExactString(
                capability,
                "protocol",
                MiniMaxH3VideoProtocol)
            || !VideoV2ExactBoolean(capability, "readerReady", true)
            || !VideoV2TryBoolean(capability, "writerEnabled", out bool writerEnabled)
            || !VideoV2TryBoolean(capability, "backendConfigured", out bool backendConfigured)
            || !VideoV2TryBoolean(
                capability,
                "runtimeSealVerified",
                out bool runtimeSealVerified)
            || !VideoV2TryBoolean(
                capability,
                "runtimeManifestVerified",
                out bool runtimeManifestVerified)
            || !VideoV2TryBoolean(capability, "licenseAccepted", out bool licenseAccepted)
            || !VideoV2TryBoolean(capability, "modelsVerified", out bool modelsVerified)
            || !VideoV2TryBoolean(capability, "workflowConfigured", out bool workflowConfigured)
            || !VideoV2TryBoolean(capability, "gpuCanaryVerified", out bool gpuCanaryVerified)
            || !VideoV2TryBoolean(capability, "ready", out bool ready)
            || !VideoV2ExactString(
                capability,
                "presetId",
                MiniMaxH3VideoPresetId)
            || !VideoV2ExactString(
                capability,
                "backendId",
                MiniMaxH3VideoBackendId)
            || !VideoV2ExactString(
                capability,
                "workflowRevision",
                MiniMaxH3VideoWorkflowRevision)
            || !VideoV2ExactString(capability, "runtimeMode", "on-demand")
            || !capability.TryGetProperty("state", out JsonElement stateElement)
            || stateElement.ValueKind != JsonValueKind.String
            || !capability.TryGetProperty("reasonCode", out JsonElement reasonElement)
            || !capability.TryGetProperty("profile", out JsonElement profile)
            || profile.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                profile,
                "canvasPolicy",
                "canary",
                "frameCount",
                "playbackFps",
                "steps",
                "audio")
            || !profile.TryGetProperty(
                "canvasPolicy",
                out JsonElement canvasPolicy)
            || canvasPolicy.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                canvasPolicy,
                "kind",
                "alignment",
                "minDimension",
                "maxDimension",
                "maxPixelArea")
            || !VideoV2ExactString(
                canvasPolicy,
                "kind",
                MiniMaxH3VideoCanvasPolicyKind)
            || !VideoV2ExactInt32(
                canvasPolicy,
                "alignment",
                MiniMaxH3VideoCanvasAlignment)
            || !VideoV2ExactInt32(
                canvasPolicy,
                "minDimension",
                MiniMaxH3VideoCanvasMinimumDimension)
            || !VideoV2ExactInt32(
                canvasPolicy,
                "maxDimension",
                MiniMaxH3VideoCanvasMaximumDimension)
            || !VideoV2ExactInt32(
                canvasPolicy,
                "maxPixelArea",
                MiniMaxH3VideoCanvasMaximumPixelArea)
            || !profile.TryGetProperty("canary", out JsonElement canary)
            || canary.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(canary, "width", "height")
            || !VideoV2ExactInt32(
                canary,
                "width",
                MiniMaxH3VideoCanaryWidth)
            || !VideoV2ExactInt32(
                canary,
                "height",
                MiniMaxH3VideoCanaryHeight)
            || !VideoV2ExactInt32(profile, "frameCount", MiniMaxH3VideoFrameCount)
            || !VideoV2ExactInt32(profile, "playbackFps", MiniMaxH3VideoPlaybackFps)
            || !VideoV2ExactInt32(profile, "steps", MiniMaxH3VideoSteps)
            || !VideoV2ExactBoolean(profile, "audio", true))
        {
            return false;
        }

        string capabilityState = stateElement.GetString() ?? "";
        string? reasonCode = reasonElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => reasonElement.GetString(),
            _ => "__invalid__",
        };
        bool allReadyFlags = writerEnabled
            && backendConfigured
            && runtimeSealVerified
            && runtimeManifestVerified
            && licenseAccepted
            && modelsVerified
            && workflowConfigured
            && gpuCanaryVerified;
        if (ready)
        {
            if (!allReadyFlags
                || !string.Equals(capabilityState, "ready", StringComparison.Ordinal)
                || reasonCode is not null)
            {
                return false;
            }
        }
        else if (capabilityState is not ("disabled" or "unverified")
            || reasonCode is null
            || !MiniMaxH3VideoReasonCodes.Contains(
                reasonCode,
                StringComparer.Ordinal)
            || !MiniMaxH3ReasonMatchesFlags(
                reasonCode,
                capabilityState,
                writerEnabled,
                backendConfigured,
                runtimeSealVerified,
                runtimeManifestVerified,
                licenseAccepted,
                modelsVerified,
                workflowConfigured,
                gpuCanaryVerified))
        {
            return false;
        }

        state = new MiniMaxH3VideoCapabilityState(ready, reasonCode);
        return true;
    }

    private static bool MiniMaxH3ReasonMatchesFlags(
        string reasonCode,
        string state,
        bool writerEnabled,
        bool backendConfigured,
        bool runtimeSealVerified,
        bool runtimeManifestVerified,
        bool licenseAccepted,
        bool modelsVerified,
        bool workflowConfigured,
        bool gpuCanaryVerified)
        => reasonCode switch
        {
            "MINIMAX_H3_WRITER_DISABLED" =>
                state == "disabled" && !writerEnabled,
            "MINIMAX_H3_RUNTIME_SEAL_INVALID" =>
                state == "unverified" && !runtimeSealVerified,
            "MINIMAX_H3_RUNTIME_MANIFEST_INVALID" =>
                state == "unverified" && !runtimeManifestVerified,
            "MINIMAX_H3_LICENSE_NOT_ACCEPTED" =>
                state == "unverified" && !licenseAccepted,
            "MINIMAX_H3_MODELS_UNVERIFIED" =>
                state == "unverified" && !modelsVerified,
            "MINIMAX_H3_WORKFLOW_UNVERIFIED" =>
                state == "unverified" && !workflowConfigured,
            "MINIMAX_H3_GPU_CANARY_UNVERIFIED" =>
                state == "unverified" && !gpuCanaryVerified,
            "MINIMAX_H3_BACKEND_CONFIG_INVALID" =>
                state == "unverified" && !backendConfigured,
            _ => false,
        };

    private static Func<JsonElement, string?> CreateMiniMaxH3VideoHealthValidator(
        bool requireDisplayedManagedSource = false)
        => payload => !TryParseMiniMaxH3VideoCapability(payload, out _)
            ? "The Aibos Image local AI service cannot prove the exact MiniMax H3 protocol. No job was added."
            : !TryParseMiniMaxH3VideoProfilesCapability(payload)
                ? "The Aibos Image local AI service does not expose the tested MiniMax H3 5, 10, 12, and 15 second profiles. Restart the local AI service first; no job was added."
                : !TryParseMiniMaxH3VideoStepsCapability(payload)
                    ? "The Aibos Image local AI service does not expose bounded MiniMax H3 STEP control. Restart the local AI service first; no job was added."
                    : !TryParseMiniMaxH3VideoCanvasTiersCapability(payload)
                        ? "The Aibos Image local AI service does not expose bounded MiniMax H3 video-size tiers. Restart the local AI service first; no job was added."
                        : requireDisplayedManagedSource
                            && !HasEnhancementCapability(
                                payload,
                                DisplayedManagedVideoSourceCapability)
                            ? "The Aibos Image local AI service cannot use the displayed generated image as a video source. Restart the local AI service first; no job was added."
                            : null;

    private static bool TryParseMiniMaxH3VideoProfilesCapability(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoH3ProfilesV1")
            || !capabilities.TryGetProperty(
                "videoH3ProfilesV1",
                out JsonElement capability)
            || capability.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                capability,
                "contractId",
                "protocol",
                "defaultProfileId",
                "playbackFps",
                "steps",
                "maxPixelArea",
                "profiles")
            || !VideoV2ExactString(
                capability,
                "contractId",
                MiniMaxH3VideoProfilesContractId)
            || !VideoV2ExactString(
                capability,
                "protocol",
                MiniMaxH3VideoProfilesProtocol)
            || !VideoV2ExactString(
                capability,
                "defaultProfileId",
                MiniMaxH3VideoDefaultProfileId)
            || !VideoV2ExactInt32(
                capability,
                "playbackFps",
                MiniMaxH3VideoPlaybackFps)
            || !VideoV2ExactInt32(
                capability,
                "steps",
                MiniMaxH3VideoSteps)
            || !VideoV2ExactInt32(
                capability,
                "maxPixelArea",
                MiniMaxH3VideoCanvasMaximumPixelArea)
            || !capability.TryGetProperty("profiles", out JsonElement profiles)
            || profiles.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement[] actualProfiles = profiles.EnumerateArray().ToArray();
        (string Id, int NominalSeconds, int FrameCount)[] expectedProfiles =
        [
            (MiniMaxH3VideoDefaultProfileId, 5, 124),
            (MiniMaxH3Video10SecondProfileId, 10, 243),
            (MiniMaxH3Video12SecondProfileId, 12, 294),
            (MiniMaxH3Video15SecondProfileId, 15, 362),
        ];
        if (actualProfiles.Length != expectedProfiles.Length)
            return false;

        for (int index = 0; index < expectedProfiles.Length; index++)
        {
            JsonElement profile = actualProfiles[index];
            var expected = expectedProfiles[index];
            if (profile.ValueKind != JsonValueKind.Object
                || !HasExactVideoV2Properties(
                    profile,
                    "id",
                    "nominalDurationSeconds",
                    "frameCount")
                || !VideoV2ExactString(profile, "id", expected.Id)
                || !VideoV2ExactInt32(
                    profile,
                    "nominalDurationSeconds",
                    expected.NominalSeconds)
                || !VideoV2ExactInt32(
                    profile,
                    "frameCount",
                    expected.FrameCount))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseMiniMaxH3VideoStepsCapability(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoH3StepsV1")
            || !capabilities.TryGetProperty(
                "videoH3StepsV1",
                out JsonElement capability)
            || capability.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                capability,
                "contractId",
                "protocol",
                "defaultSteps",
                "minimumSteps",
                "maximumSteps")
            || !VideoV2ExactString(
                capability,
                "contractId",
                MiniMaxH3VideoStepsContractId)
            || !VideoV2ExactString(
                capability,
                "protocol",
                MiniMaxH3VideoStepsProtocol)
            || !VideoV2ExactInt32(
                capability,
                "defaultSteps",
                MiniMaxH3VideoSteps)
            || !VideoV2ExactInt32(
                capability,
                "minimumSteps",
                MiniMaxH3VideoMinimumSteps)
            || !VideoV2ExactInt32(
                capability,
                "maximumSteps",
                MiniMaxH3VideoMaximumSteps))
        {
            return false;
        }
        return true;
    }

    private static bool TryParseMiniMaxH3VideoCanvasTiersCapability(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoH3CanvasTiersV1")
            || !capabilities.TryGetProperty(
                "videoH3CanvasTiersV1",
                out JsonElement capability)
            || capability.ValueKind != JsonValueKind.Object
            || !HasExactVideoV2Properties(
                capability,
                "contractId",
                "protocol",
                "defaultMaximumPixelArea",
                "maximumPixelAreas")
            || !VideoV2ExactString(
                capability,
                "contractId",
                MiniMaxH3VideoCanvasTiersContractId)
            || !VideoV2ExactString(
                capability,
                "protocol",
                MiniMaxH3VideoCanvasTiersProtocol)
            || !VideoV2ExactInt32(
                capability,
                "defaultMaximumPixelArea",
                MiniMaxH3VideoCanvasMaximumPixelArea)
            || !capability.TryGetProperty(
                "maximumPixelAreas",
                out JsonElement maximumPixelAreas)
            || maximumPixelAreas.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        int[] actual = maximumPixelAreas
            .EnumerateArray()
            .Select(static item => item.TryGetInt32(out int value) ? value : -1)
            .ToArray();
        return actual.SequenceEqual(SupportedMiniMaxH3VideoMaximumPixelAreas);
    }

    private static bool HasExactVideoV2Properties(
        JsonElement element,
        params string[] expectedNames)
    {
        string[] actualNames = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return actualNames.Length == expectedNames.Length
            && actualNames
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames);
    }

    private static bool VideoV2ExactString(
        JsonElement element,
        string propertyName,
        string expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool VideoV2TryBoolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool VideoV2ExactBoolean(
        JsonElement element,
        string propertyName,
        bool expected)
        => VideoV2TryBoolean(element, propertyName, out bool value)
            && value == expected;

    private static bool VideoV2ExactInt32(
        JsonElement element,
        string propertyName,
        int expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.TryGetInt32(out int value)
            && value == expected;

    public static bool TryParseMiniMaxH3VideoCapabilityForSmoke(
        JsonElement payload,
        out bool ready,
        out string? reasonCode)
    {
        bool parsed = TryParseMiniMaxH3VideoCapability(payload, out var capability);
        ready = parsed && capability.Ready;
        reasonCode = parsed ? capability.ReasonCode : null;
        return parsed;
    }

    public static bool TryParseMiniMaxH3VideoProfilesCapabilityForSmoke(
        JsonElement payload)
        => TryParseMiniMaxH3VideoProfilesCapability(payload);

    public static bool TryParseMiniMaxH3VideoStepsCapabilityForSmoke(
        JsonElement payload)
        => TryParseMiniMaxH3VideoStepsCapability(payload);

    public static bool TryParseMiniMaxH3VideoCanvasTiersCapabilityForSmoke(
        JsonElement payload)
        => TryParseMiniMaxH3VideoCanvasTiersCapability(payload);

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
                    "The Aibos Image local AI service cannot prove support for this request. Restart the local AI service first; no job was added.")
                : null;
        }
        if (probe.HealthPayload is not JsonElement healthPayload)
        {
            return new EnhancementApiResponse(
                false,
                426,
                null,
                "The Aibos Image local AI service cannot prove support for this request. Restart the local AI service first; no job was added.");
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
        string? recoverySourceIdentity = null,
        Func<string?>? prePublishValidator = null,
        Func<CancellationToken, Task<string?>>?
            asyncPrePublishValidator = null)
    {
        if (_usingDefaultModalEnhancementSender)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionApiReadyAsync(
                    recoverySourceIdentity,
                    token);
            if (!readiness.Ok)
                return readiness;
        }

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
            string? prePublishError = asyncPrePublishValidator is not null
                ? await asyncPrePublishValidator(token)
                : null;
            if (!string.IsNullOrWhiteSpace(prePublishError))
            {
                return new EnhancementApiResponse(
                    false,
                    409,
                    null,
                    prePublishError);
            }
            token.ThrowIfCancellationRequested();
            prePublishError = prePublishValidator?.Invoke();
            if (!string.IsNullOrWhiteSpace(prePublishError))
            {
                return new EnhancementApiResponse(
                    false,
                    409,
                    null,
                    prePublishError);
            }
            token.ThrowIfCancellationRequested();
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
                recoverySourceIdentity,
                item.RequestId);
            return SavedForDeliveryResponse(item);
        }

        int remaining = RemainingEnhancementEnqueueActionMilliseconds(probe);
        if (remaining <= 0)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                recoverySourceIdentity,
                item.RequestId);
            return SavedForDeliveryResponse(item);
        }
        string nudgeRoute = _usingDefaultModalEnhancementSender
            ? EnhancementCompanionQueueRecoveryRoute
            : retryJobId is null
                ? "api/enhance/jobs"
                : $"api/enhance/jobs/{Uri.EscapeDataString(retryJobId)}/retry";
        EnhancementApiResponse nudge = await SendEnhancementApiAsync(
            HttpMethod.Post,
            nudgeRoute,
            token: token,
            exactBodyJson: _usingDefaultModalEnhancementSender
                ? null
                : item.BodyJson,
            idempotencyKey: item.RequestId,
            timeoutMilliseconds: remaining);
        EnhancementApiResponse normalized =
            NormalizeDurableEnqueueResponse(nudge, item);
        if (normalized.SavedForDelivery)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                recoverySourceIdentity,
                item.RequestId);
        }
        return normalized;
    }

    private async Task<DurableEnhancementBatchResponse>
        TrySendDurableEnhancementBatchAsync(
            IReadOnlyList<object?> bodies,
            string queuePlacement = "last",
            CancellationToken token = default,
            Action? onFirstPublish = null,
            Func<bool>? shouldStopBeforeFirstPublish = null,
            Func<JsonElement, string?>? healthValidator = null,
            bool requireExactHealthValidation = false)
        => await TrySendDurableEnhancementBatchCoreAsync(
            bodies.Select(static body =>
                new DurableEnhancementBatchItem(body, null)).ToArray(),
            queuePlacement,
            token,
            onFirstPublish,
            shouldStopBeforeFirstPublish,
            healthValidator,
            requireExactHealthValidation);

    private async Task<DurableEnhancementBatchResponse>
        TrySendDurableEnhancementRetryBatchAsync(
            IReadOnlyList<EnhancementWorkspaceJobView> jobs,
            CancellationToken token = default)
    {
        Func<JsonElement, string?>?[] validators = jobs
            .Select(CreateEnhancementRetryHealthValidator)
            .ToArray();
        return await TrySendDurableEnhancementBatchCoreAsync(
            jobs.Select(static job =>
                new DurableEnhancementBatchItem(null, job.Id)).ToArray(),
            "last",
            token,
            itemHealthValidators: validators,
            requireExactItemHealthValidation: true);
    }

    private async Task<DurableEnhancementBatchResponse>
        TrySendDurableEnhancementBatchCoreAsync(
            IReadOnlyList<DurableEnhancementBatchItem> items,
            string queuePlacement = "last",
            CancellationToken token = default,
            Action? onFirstPublish = null,
            Func<bool>? shouldStopBeforeFirstPublish = null,
            Func<JsonElement, string?>? healthValidator = null,
            bool requireExactHealthValidation = false,
            IReadOnlyList<Func<JsonElement, string?>?>? itemHealthValidators = null,
            bool requireExactItemHealthValidation = false)
    {
        if (items.Count == 0)
            return new DurableEnhancementBatchResponse([], 0, 0);
        if (itemHealthValidators is not null
            && itemHealthValidators.Count != items.Count)
        {
            throw new ArgumentException(
                "Item health validators must match the batch item count.",
                nameof(itemHealthValidators));
        }
        if (_usingDefaultModalEnhancementSender)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionApiReadyAsync(
                    token: token);
            if (!readiness.Ok)
            {
                EnhancementApiResponse rejected = new(
                    false,
                    readiness.StatusCode,
                    readiness.Payload,
                    readiness.Error);
                return new DurableEnhancementBatchResponse(
                    Enumerable.Repeat(rejected, items.Count).ToArray(),
                    0,
                    0);
            }
        }

        EnhancementEnqueueProbe probe =
            await ProbeEnhancementEnqueueBackendAsync(token);
        EnhancementApiResponse? validationFailure =
            ValidateEnhancementEnqueueProbe(
                probe,
                healthValidator,
                requireExactHealthValidation);
        if (validationFailure is not null)
        {
            return new DurableEnhancementBatchResponse(
                Enumerable.Repeat(validationFailure, items.Count).ToArray(),
                0,
                0);
        }
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
        var responses = Enumerable.Repeat(unsavedFailure, items.Count).ToArray();
        var publishIndices = new List<int>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            EnhancementApiResponse? itemValidationFailure =
                itemHealthValidators is null
                    ? null
                    : ValidateEnhancementEnqueueProbe(
                        probe,
                        itemHealthValidators[index],
                        requireExactItemHealthValidation);
            if (itemValidationFailure is null)
                publishIndices.Add(index);
            else
                responses[index] = itemValidationFailure;
        }
        var publishedItems = new List<(int GlobalIndex, EnhancementEnqueueInboxItem Item)>(
            publishIndices.Count);
        bool firstPublishReported = false;
        bool abortRemainingPublishes = false;

        bool PublishRange(int start, int count)
        {
            if (!firstPublishReported
                && shouldStopBeforeFirstPublish?.Invoke() == true)
            {
                for (int index = start; index < publishIndices.Count; index++)
                    responses[publishIndices[index]] = stoppedResponse;
                abortRemainingPublishes = true;
                return false;
            }

            try
            {
                EnhancementEnqueueInboxItem[] chunk = Enumerable.Range(0, count)
                    .Select(localIndex =>
                    {
                        int globalIndex = publishIndices[start + localIndex];
                        DurableEnhancementBatchItem batchItem =
                            items[globalIndex];
                        return EnhancementEnqueueInboxStore.CreateItem(
                            batchItem.Body,
                            queuePlacement,
                            localIndex,
                            kind: batchItem.RetryJobId is null
                                ? "create"
                                : "retry",
                            retryJobId: batchItem.RetryJobId);
                    })
                    .ToArray();
                _ = EnhancementEnqueueInboxStore.Publish(
                    ResolvedEnhancementJobsPath,
                    chunk);
                for (int localIndex = 0; localIndex < chunk.Length; localIndex++)
                {
                    int globalIndex = publishIndices[start + localIndex];
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
                responses[publishIndices[start]] = new EnhancementApiResponse(
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

        for (int start = 0; start < publishIndices.Count; start += EnhancementEnqueueInboxStore.MaximumItemsPerEnvelope)
        {
            int count = EnhancementEnqueueProbePolicy.NextEnvelopeItemCount(
                publishIndices.Count - start);
            if (!PublishRange(start, count))
                break;
        }

        if (!EnhancementEnqueueProbePolicy.AllowsImmediateNudge(probe.Mode)
            && publishedItems.Count > 0)
        {
            KickEnhancementCompanionRecoveryAfterDurablePublish(
                sourceIdentity: null,
                publishedItems[0].Item.RequestId);
        }

        int nudgeCount = 0;
        if (EnhancementEnqueueProbePolicy.AllowsImmediateNudge(probe.Mode))
        {
            IReadOnlyList<(int GlobalIndex, EnhancementEnqueueInboxItem Item)> nudgeItems =
                _usingDefaultModalEnhancementSender && publishedItems.Count > 0
                    ? publishedItems.Take(1).ToArray()
                    : publishedItems;
            foreach ((int globalIndex, EnhancementEnqueueInboxItem item) in nudgeItems)
            {
                int remaining = RemainingEnhancementEnqueueActionMilliseconds(probe);
                if (remaining <= 0 || token.IsCancellationRequested)
                    break;

                nudgeCount++;
                EnhancementApiResponse nudge = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    _usingDefaultModalEnhancementSender
                        ? EnhancementCompanionQueueRecoveryRoute
                        : item.RetryJobId is null
                            ? "api/enhance/jobs"
                            : $"api/enhance/jobs/{Uri.EscapeDataString(item.RetryJobId)}/retry",
                    token: token,
                    exactBodyJson: _usingDefaultModalEnhancementSender
                        ? null
                        : item.BodyJson,
                    idempotencyKey: item.RequestId,
                    timeoutMilliseconds: remaining);
                if (_usingDefaultModalEnhancementSender)
                {
                    // One wake drains the published inbox envelope. Repeating
                    // the same API wake once per item makes large batches wait
                    // on redundant loopback round trips without adding
                    // durability or ordering guarantees.
                    foreach ((int publishedIndex, EnhancementEnqueueInboxItem publishedItem)
                        in publishedItems)
                    {
                        responses[publishedIndex] = NormalizeDurableEnqueueResponse(
                            nudge,
                            publishedItem,
                            allowDefinitiveFailure:
                                publishedIndex == globalIndex);
                    }
                }
                else
                {
                    responses[globalIndex] = NormalizeDurableEnqueueResponse(nudge, item);
                }
            }
            if (publishedItems.Any(entry =>
                    responses[entry.GlobalIndex].SavedForDelivery))
            {
                KickEnhancementCompanionRecoveryAfterDurablePublish(
                    sourceIdentity: null,
                    publishedItems[0].Item.RequestId);
            }
        }

        return new DurableEnhancementBatchResponse(
            responses,
            nudgeCount,
            publishedItems.Count);
    }

    private void KickEnhancementCompanionRecoveryAfterDurablePublish(
        string? sourceIdentity,
        string requestId)
    {
        if (!_usingDefaultModalEnhancementSender
            || _enhancementCompanionLifetimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_enhancementCompanionDurableRecoverySync)
        {
            _enhancementCompanionDurableRecoveryRequested = true;
            _enhancementCompanionDurableRecoveryRequestId = requestId;
            _enhancementCompanionDurableRecoverySourceIdentity = sourceIdentity;
            if (_enhancementCompanionDurableRecoveryRunning)
                return;
            _enhancementCompanionDurableRecoveryRunning = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_enhancementCompanionLifetimeCts.IsCancellationRequested)
                {
                    string? nextRequestId;
                    string? nextSourceIdentity;
                    lock (_enhancementCompanionDurableRecoverySync)
                    {
                        if (!_enhancementCompanionDurableRecoveryRequested)
                            break;
                        _enhancementCompanionDurableRecoveryRequested = false;
                        nextRequestId = _enhancementCompanionDurableRecoveryRequestId;
                        nextSourceIdentity =
                            _enhancementCompanionDurableRecoverySourceIdentity;
                    }

                    await RecoverAndWakeDurableEnqueueInboxAsync(
                        nextSourceIdentity,
                        nextRequestId,
                        _enhancementCompanionLifetimeCts.Token);
                }
            }
            catch
            {
                // The reservation is already durable. Recovery is best-effort.
            }
            finally
            {
                bool restart;
                lock (_enhancementCompanionDurableRecoverySync)
                {
                    _enhancementCompanionDurableRecoveryRunning = false;
                    restart = _enhancementCompanionDurableRecoveryRequested;
                }
                if (restart)
                {
                    KickEnhancementCompanionRecoveryAfterDurablePublish(
                        _enhancementCompanionDurableRecoverySourceIdentity,
                        _enhancementCompanionDurableRecoveryRequestId ?? requestId);
                }
            }
        });
    }

    private async Task RecoverAndWakeDurableEnqueueInboxAsync(
        string? sourceIdentity,
        string? requestId,
        CancellationToken token)
    {
        for (int attempt = 0; attempt < DurableEnqueueRecoveryAttempts; attempt++)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionApiReadyAsync(
                    sourceIdentity,
                    token);
            if (readiness.Ok)
            {
                EnhancementApiResponse recovery = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    EnhancementCompanionQueueRecoveryRoute,
                    token: token,
                    idempotencyKey: requestId);
                if (recovery.Ok
                    && requestId is not null
                    && EnhancementEnqueueProbePolicy.HasMatchingDurableReceipt(
                        recovery.Payload,
                        requestId))
                {
                    return;
                }
                if (recovery.Ok)
                {
                    // Older authenticated companions recover the queue here
                    // but do not drain the durable inbox. A bodyless wake is
                    // safe after recovery and keeps saved reservations moving
                    // during a version handoff.
                    EnhancementApiResponse wake = await SendEnhancementApiAsync(
                        HttpMethod.Post,
                        EnhancementCompanionWakeRoute,
                        token: token,
                        idempotencyKey: requestId);
                    if (wake.Ok)
                        return;
                    if (wake.InnerStatusAuthoritative
                        && wake.StatusCode is >= 400 and < 500
                        && wake.StatusCode is not (408 or 425 or 429))
                    {
                        return;
                    }
                }
                else if (recovery.InnerStatusAuthoritative
                    && recovery.StatusCode is >= 400 and < 500
                    && recovery.StatusCode is not (408 or 425 or 429))
                {
                    return;
                }
            }

            if (attempt + 1 < DurableEnqueueRecoveryAttempts)
            {
                await Task.Delay(
                    EnhancementCompanionProbeDelayMilliseconds,
                    token);
            }
        }
    }

    private static EnhancementApiResponse NormalizeDurableEnqueueResponse(
        EnhancementApiResponse response,
        EnhancementEnqueueInboxItem item,
        bool allowDefinitiveFailure = true)
    {
        if (response.Ok
            && EnhancementEnqueueProbePolicy.HasMatchingDurableReceipt(
                response.Payload,
                item.RequestId))
        {
            return response.Payload is JsonElement payload
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("job", out JsonElement job)
                && job.ValueKind == JsonValueKind.Object
                    ? response
                    : SavedForDeliveryResponse(item);
        }
        if (allowDefinitiveFailure
            && response.InnerStatusAuthoritative
            && response.StatusCode is >= 400 and < 500
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

    private bool TryGetOrCreateEnhancementCompanionAuthToken(
        out string authToken,
        out string error)
    {
        authToken = "";
        error = "";
        if (IsValidEnhancementCompanionAuthToken(_enhancementCompanionAuthToken))
        {
            authToken = _enhancementCompanionAuthToken!;
            return true;
        }

        if (!EnhancementCompanionAuthStoragePath.TryForCurrentUser(
                out EnhancementCompanionAuthStoragePath? storage))
        {
            authToken = "";
            error = "The local AI companion authentication root is unavailable.";
            return false;
        }
        bool ok = TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
            storage!,
            out authToken,
            out error);
        if (ok)
        {
            _enhancementCompanionAuthToken = authToken;
        }
        return ok;
    }

    private static bool TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
        EnhancementCompanionAuthStoragePath storage,
        out string authToken,
        out string error,
        bool failAfterCreateForSmoke = false)
    {
        if (!TryAcquireEnhancementCompanionAuthDirectoryLease(
                storage,
                out EnhancementCompanionAuthDirectoryLease? directoryLease))
        {
            authToken = "";
            error = "The local AI companion authentication directory is not trusted.";
            return false;
        }
        using (directoryLease)
        {
            return TryGetOrCreateEnhancementCompanionAuthTokenUnderLease(
                storage,
                directoryLease!,
                out authToken,
                out error,
                failAfterCreateForSmoke);
        }
    }

    private static bool TryGetOrCreateEnhancementCompanionAuthTokenUnderLease(
        EnhancementCompanionAuthStoragePath storage,
        EnhancementCompanionAuthDirectoryLease directoryLease,
        out string authToken,
        out string error,
        bool failAfterCreateForSmoke)
    {
        authToken = "";
        error = "";
        bool createdFile = false;
        bool acceptedCreatedFile = false;
        FileStream? stream = null;
        try
        {
            if (!directoryLease.IsStillBound())
            {
                error = "The local AI companion authentication directory changed during validation.";
                return false;
            }
            try
            {
                // The three directory leases retain the validated identities.
                // Their final paths are rechecked around this exclusive open.
                // codeql[cs/path-injection]
                stream = new FileStream(
                    storage.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    1024,
                    FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                try
                {
                    FileSecurity security = BuildCurrentUserOnlyAuthFileSecurity();
                    // CreateNew applies the protected DACL atomically and
                    // FullControl gives this same handle DELETE for safe
                    // delete-on-close if provisioning later fails.
                    // codeql[cs/path-injection]
                    stream = FileSystemAclExtensions.Create(
                        new FileInfo(storage.FilePath),
                        FileMode.CreateNew,
                        FileSystemRights.FullControl,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough,
                        security);
                    createdFile = true;
                }
                catch (IOException)
                {
                    // Another Aibos process may have won CreateNew. Do not
                    // inspect or replace it by name; open the winner once and
                    // validate that exact exclusive handle below.
                    // codeql[cs/path-injection]
                    stream = new FileStream(
                        storage.FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        1024,
                        FileOptions.SequentialScan);
                }
            }

            if (stream is null
                || !directoryLease.IsStillBound()
                || !IsTrustedEnhancementCompanionAuthFileHandleShape(
                    stream,
                    storage))
            {
                error = "The local AI companion authentication file changed during validation.";
                return false;
            }

            if (createdFile)
            {
                if (failAfterCreateForSmoke)
                {
                    throw new IOException(
                        "Synthetic failure after companion auth file creation.");
                }
                string generated = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                byte[] bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(
                    ProtectedData.Protect(
                        Encoding.ASCII.GetBytes(generated),
                        EnhancementCompanionAuthEntropy,
                        DataProtectionScope.CurrentUser)));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                if (!directoryLease.IsStillBound()
                    || !IsTrustedEnhancementCompanionAuthFileHandleShape(
                        stream,
                        storage))
                {
                    error = "The local AI companion authentication path changed during creation.";
                    return false;
                }
            }

            if (!TryReadTrustedEnhancementCompanionAuthToken(
                    stream,
                    storage,
                    out string candidate))
            {
                error = "The local AI companion authentication file is not user-only.";
                return false;
            }
            if (!IsValidEnhancementCompanionAuthToken(candidate))
            {
                error = "The local AI companion authentication file is invalid.";
                return false;
            }
            if (!directoryLease.IsStillBound())
            {
                error = "The local AI companion authentication directory changed during reading.";
                return false;
            }
            acceptedCreatedFile = true;
            authToken = candidate;
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            CryptographicException or
            FormatException or
            ArgumentException or
            NotSupportedException)
        {
            error = $"Aibos could not establish user-only local AI authentication: {ex.Message}";
            return false;
        }
        finally
        {
            if (createdFile && !acceptedCreatedFile && stream is not null)
            {
                // Delete only the exact newly-created handle. Never reopen an
                // unknown current occupant by name during failure cleanup.
                _ = WindowsPathIdentity.TryDeleteOpenRegularFile(
                    stream.SafeFileHandle);
            }
            stream?.Dispose();
        }
    }

    private static bool TryAcquireEnhancementCompanionAuthDirectoryLease(
        EnhancementCompanionAuthStoragePath storage,
        out EnhancementCompanionAuthDirectoryLease? lease)
    {
        lease = null;
        SafeFileHandle? rootLease = null;
        SafeFileHandle? applicationLease = null;
        SafeFileHandle? authLease = null;
        try
        {
            if (!WindowsPathIdentity.TryOpenDirectoryLease(
                    storage.RootPath,
                    out rootLease))
            {
                return false;
            }

            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath))
            {
                return false;
            }

            // Windows can rename an open directory. Recheck the retained root
            // identity around every path-based child operation and fail closed
            // if its final path changes.
            // codeql[cs/path-injection]
            if (!Directory.Exists(storage.ApplicationDirectoryPath))
            {
                if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                        rootLease,
                        storage.RootPath))
                {
                    return false;
                }
                // codeql[cs/path-injection]
                Directory.CreateDirectory(storage.ApplicationDirectoryPath);
            }
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath))
            {
                return false;
            }
            if (!WindowsPathIdentity.TryOpenDirectoryLease(
                    storage.ApplicationDirectoryPath,
                    out applicationLease))
            {
                return false;
            }
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    applicationLease,
                    storage.ApplicationDirectoryPath))
            {
                return false;
            }

            // Existing unknown state is never rewritten. For a missing final
            // directory, apply the protected DACL atomically at creation.
            // codeql[cs/path-injection]
            if (!Directory.Exists(storage.DirectoryPath))
            {
                if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                        rootLease,
                        storage.RootPath)
                    || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                        applicationLease,
                        storage.ApplicationDirectoryPath))
                {
                    return false;
                }
                DirectorySecurity directorySecurity =
                    BuildCurrentUserOnlyAuthDirectorySecurity();
                // codeql[cs/path-injection]
                FileSystemAclExtensions.Create(
                    new DirectoryInfo(storage.DirectoryPath),
                    directorySecurity);
            }
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    applicationLease,
                    storage.ApplicationDirectoryPath))
            {
                return false;
            }
            if (!WindowsPathIdentity.TryOpenDirectoryLease(
                    storage.DirectoryPath,
                    out authLease))
            {
                return false;
            }
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    applicationLease,
                    storage.ApplicationDirectoryPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    authLease,
                    storage.DirectoryPath))
            {
                return false;
            }

            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
                return false;
            // The path-based directory ACL API is read-only. The retained
            // identities are checked both before and after it so a rename is
            // detected before any child file operation is allowed.
            // codeql[cs/path-injection]
            DirectorySecurity actualSecurity = new DirectoryInfo(
                    storage.DirectoryPath)
                .GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
            if (!HasCurrentUserOnlyAuthAcl(actualSecurity, currentUser))
                return false;
            if (!WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    rootLease,
                    storage.RootPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    applicationLease,
                    storage.ApplicationDirectoryPath)
                || !WindowsPathIdentity.IsDirectoryLeaseBoundTo(
                    authLease,
                    storage.DirectoryPath))
            {
                return false;
            }

            lease = new EnhancementCompanionAuthDirectoryLease(
                storage,
                rootLease,
                applicationLease,
                authLease);
            rootLease = null;
            applicationLease = null;
            authLease = null;
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
        finally
        {
            authLease?.Dispose();
            applicationLease?.Dispose();
            rootLease?.Dispose();
        }
    }

    private static DirectorySecurity BuildCurrentUserOnlyAuthDirectorySecurity()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new System.Security.SecurityException(
                "The current Windows user identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static FileSecurity BuildCurrentUserOnlyAuthFileSecurity()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new System.Security.SecurityException(
                "The current Windows user identity is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static bool TryReadTrustedEnhancementCompanionAuthToken(
        FileStream stream,
        EnhancementCompanionAuthStoragePath storage,
        out string candidate)
    {
        candidate = "";
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
            return false;
        if (!IsTrustedEnhancementCompanionAuthFileHandleShape(stream, storage)
            || stream.Length is <= 0 or > 1024)
        {
            return false;
        }
        FileSecurity security = stream.GetAccessControl();
        if (!HasCurrentUserOnlyAuthAcl(security, currentUser))
            return false;

        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        string protectedToken = reader.ReadToEnd().Trim();
        byte[] plaintext = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedToken),
            EnhancementCompanionAuthEntropy,
            DataProtectionScope.CurrentUser);
        try
        {
            candidate = Encoding.ASCII.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        return stream.Length is > 0 and <= 1024;
    }

    private static bool IsTrustedEnhancementCompanionAuthFileHandleShape(
        FileStream stream,
        EnhancementCompanionAuthStoragePath storage)
    {
        if (!WindowsPathIdentity.TryGetFinalPath(
                stream.SafeFileHandle,
                out string finalPath)
            || !string.Equals(
                storage.FilePath,
                finalPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(finalPath),
                storage.DirectoryPath,
                StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(stream.SafeFileHandle)
                & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || !WindowsPathIdentity.TryGetHardLinkCount(
                stream.SafeFileHandle,
                out uint linkCount)
            || linkCount != 1)
        {
            return false;
        }

        return true;
    }

    private static bool HasCurrentUserOnlyAuthAcl(
        FileSystemSecurity security,
        SecurityIdentifier currentUser)
    {
        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(currentUser))
        {
            return false;
        }
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        bool currentUserFullControl = false;
        bool localSystemFullControl = false;
        AuthorizationRuleCollection rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IsInherited)
                return false;
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference.Equals(currentUser))
            {
                currentUserFullControl |=
                    (rule.FileSystemRights & FileSystemRights.FullControl)
                        == FileSystemRights.FullControl;
                continue;
            }
            if (rule.IdentityReference.Equals(localSystem))
            {
                localSystemFullControl |=
                    (rule.FileSystemRights & FileSystemRights.FullControl)
                        == FileSystemRights.FullControl;
                continue;
            }
            return false;
        }
        return currentUserFullControl && localSystemFullControl;
    }

    private async Task<EnhancementCompanionOwnershipProbe>
        ProbeEnhancementCompanionOwnershipAsync(
            string authToken,
            CancellationToken token)
    {
        string challenge = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        Uri endpoint = new(
            ResolveBrowserEnhancementBaseUri(),
            EnhancementCompanionIdentityRoute);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionChallengeHeader,
            challenge);
        try
        {
            using HttpResponseMessage response = await _modalEnhancementSender(
                request,
                token);
            int statusCode = (int)response.StatusCode;
            byte[]? responseBytes = await ReadBoundedEnhancementResponseAsync(
                response.Content,
                EnhancementCompanionIdentityResponseMaxBytes,
                token);
            if (responseBytes is null)
            {
                return new(
                    false,
                    false,
                    false,
                    statusCode,
                    null,
                    "The process on the local AI port returned an oversized identity response. No request was sent.");
            }

            JsonElement? payload = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBytes);
                payload = document.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
            if (IsRetryableEnhancementCompanionIdentityBusyResponse(
                    response,
                    payload))
            {
                return new(
                    false,
                    false,
                    true,
                    statusCode,
                    payload,
                    "The local AI companion is busy and has not proved ownership yet. No source, prompt, secret, job body, or durable reservation was sent or published. Try again.");
            }
            if (!response.IsSuccessStatusCode
                || payload is not JsonElement identity
                || identity.ValueKind != JsonValueKind.Object
                || !TryGetStringProperty(identity, "protocol", out string? protocol)
                || !string.Equals(protocol, EnhancementCompanionAuthProtocol, StringComparison.Ordinal)
                || !TryGetStringProperty(identity, "instanceId", out string? instanceId)
                || !TryGetStringProperty(identity, "challenge", out string? echoedChallenge)
                || !TryGetStringProperty(identity, "proof", out string? proof)
                || instanceId is null
                || proof is null
                || !identity.TryGetProperty("processId", out JsonElement processIdElement)
                || !processIdElement.TryGetInt32(out int processId)
                || !TryGetStringProperty(identity, "serverStartedAtUtc", out string? serverStartedAtRaw)
                || !DateTimeOffset.TryParse(serverStartedAtRaw, out DateTimeOffset serverStartedAt)
                || !string.Equals(echoedChallenge, challenge, StringComparison.Ordinal)
                || !IsExpectedEnhancementCompanionIdentity(
                    authToken,
                    challenge,
                    proof,
                    instanceId,
                    processId,
                    serverStartedAtRaw!,
                    serverStartedAt))
            {
                return new(
                    false,
                    false,
                    false,
                    statusCode,
                    payload,
                    "The local AI service port is occupied by an untrusted process. No source, prompt, secret, job body, or durable reservation was sent.");
            }
            _verifiedEnhancementCompanionInstanceId = instanceId;
            _verifiedEnhancementCompanionServerStartedAtUtc = serverStartedAtRaw;
            return new(true, false, false, statusCode, payload, "");
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or InvalidOperationException)
        {
            return new(
                false,
                true,
                false,
                0,
                null,
                "No local AI companion is listening yet.");
        }
    }

    private static bool IsRetryableEnhancementCompanionIdentityBusyResponse(
        HttpResponseMessage response,
        JsonElement? payload)
    {
        if ((int)response.StatusCode != 503
            || payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonProperty[] properties = root.EnumerateObject().ToArray();
        return properties.Length == 1
            && string.Equals(
                properties[0].Name,
                "error",
                StringComparison.Ordinal)
            && properties[0].Value.ValueKind == JsonValueKind.String
            && string.Equals(
                properties[0].Value.GetString(),
                "The local AI companion is busy.",
                StringComparison.Ordinal);
    }

    private bool IsExpectedEnhancementCompanionIdentity(
        string authToken,
        string challenge,
        string proof,
        string instanceId,
        int processId,
        string serverStartedAtRaw,
        DateTimeOffset serverStartedAt)
    {
        if (!TryBase64UrlDecode(authToken, out byte[] authTokenBytes)
            || authTokenBytes.Length != 32
            || !TryBase64UrlDecode(proof, out byte[] suppliedProof)
            || suppliedProof.Length != 32)
        {
            return false;
        }
        using var hmac = new HMACSHA256(authTokenBytes);
        byte[] expectedProof = hmac.ComputeHash(Encoding.UTF8.GetBytes(
            $"{EnhancementCompanionAuthProtocol}\0{challenge}\0{instanceId}\0{processId}\0{serverStartedAtRaw}"));
        if (!CryptographicOperations.FixedTimeEquals(
                suppliedProof,
                expectedProof))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(_ownedEnhancementCompanionInstanceId)
            && !string.Equals(
                instanceId,
                _ownedEnhancementCompanionInstanceId,
                StringComparison.Ordinal))
        {
            return false;
        }
        Process actualProcess;
        try
        {
            actualProcess = Process.GetProcessById(processId);
            if (actualProcess.HasExited)
                return false;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return false;
        }
        using (actualProcess)
        {
            DateTimeOffset processStartedAt;
            try
            {
                processStartedAt = actualProcess.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or Win32Exception
                    or NotSupportedException)
            {
                return false;
            }
            if (serverStartedAt < processStartedAt
                || serverStartedAt - processStartedAt > TimeSpan.FromMinutes(3))
            {
                return false;
            }
        }
        if (_ownedEnhancementCompanion is { } owned)
        {
            if (owned.HasExited || owned.Id != processId)
                return false;
        }
        return serverStartedAt <= DateTimeOffset.UtcNow.AddSeconds(30);
    }

    private bool TryCreateEnhancementCompanionSecureRequest(
        HttpMethod innerMethod,
        string relativePath,
        ReadOnlySpan<byte> innerBodyBytes,
        string? idempotencyKey,
        out HttpRequestMessage request,
        out string requestNonce)
    {
        request = new HttpRequestMessage();
        requestNonce = "";
        if (!_enhancementCompanionOwnershipVerified
            || !IsValidEnhancementCompanionAuthToken(
                _enhancementCompanionAuthToken)
            || string.IsNullOrWhiteSpace(
                _verifiedEnhancementCompanionInstanceId)
            || string.IsNullOrWhiteSpace(
                _verifiedEnhancementCompanionServerStartedAtUtc)
            || !TryBase64UrlDecode(
                _enhancementCompanionAuthToken!,
                out byte[] authTokenBytes))
        {
            return false;
        }

        string instanceId = _verifiedEnhancementCompanionInstanceId!;
        string serverStartedAtUtc =
            _verifiedEnhancementCompanionServerStartedAtUtc!;
        Uri innerEndpoint = new(
            ResolveBrowserEnhancementBaseUri(),
            relativePath.TrimStart('/'));
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            method = innerMethod.Method.ToUpperInvariant(),
            pathAndQuery = innerEndpoint.PathAndQuery,
            bodyBase64Url = innerBodyBytes.Length == 0
                ? null
                : Base64UrlEncode(innerBodyBytes),
            idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : idempotencyKey,
        });
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        requestNonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] tunnelKey = DeriveEnhancementCompanionTunnelKey(
            authTokenBytes,
            instanceId,
            serverStartedAtUtc);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        byte[] aad = Encoding.UTF8.GetBytes(
            $"{EnhancementCompanionTunnelProtocol}\0{timestamp}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}");
        using (var aes = new AesGcm(tunnelKey, tag.Length))
            aes.Encrypt(iv, plaintext, ciphertext, tag, aad);
        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = EnhancementCompanionTunnelProtocol,
            iv = Base64UrlEncode(iv),
            ciphertext = Base64UrlEncode(ciphertext),
            tag = Base64UrlEncode(tag),
        });

        request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                ResolveBrowserEnhancementBaseUri(),
                EnhancementCompanionSecureRoute));
        Uri secureEndpoint = request.RequestUri!;
        request.Content = new ByteArrayContent(envelopeBytes);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/json");
        string bodyHash = Base64UrlEncode(SHA256.HashData(envelopeBytes));
        string message =
            $"{EnhancementCompanionRequestAuthProtocol}\0{timestamp}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}\0POST\0{secureEndpoint.PathAndQuery}\0{bodyHash}";
        using var hmac = new HMACSHA256(authTokenBytes);
        string signature = Base64UrlEncode(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionTimestampHeader,
            timestamp);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionNonceHeader,
            requestNonce);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionSignatureHeader,
            signature);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionInstanceHeader,
            instanceId);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionEpochHeader,
            serverStartedAtUtc);
        return true;
    }

    private byte[] DeriveEnhancementCompanionTunnelKey(
        ReadOnlySpan<byte> authTokenBytes,
        string instanceId,
        string serverStartedAtUtc)
    {
        using var hmac = new HMACSHA256(authTokenBytes.ToArray());
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(
            $"{EnhancementCompanionTunnelKeyProtocol}\0{instanceId}\0{serverStartedAtUtc}"));
    }

    private bool TryDecryptEnhancementCompanionSecureResponse(
        HttpResponseMessage response,
        ReadOnlySpan<byte> envelopeBytes,
        string requestNonce,
        out int innerStatusCode,
        out byte[] plaintext)
    {
        innerStatusCode = 0;
        plaintext = [];
        if (!IsValidEnhancementCompanionAuthToken(
                _enhancementCompanionAuthToken)
            || string.IsNullOrWhiteSpace(
                _verifiedEnhancementCompanionInstanceId)
            || string.IsNullOrWhiteSpace(
                _verifiedEnhancementCompanionServerStartedAtUtc)
            || !TryBase64UrlDecode(
                _enhancementCompanionAuthToken!,
                out byte[] authTokenBytes)
            || !response.Headers.TryGetValues(
                EnhancementCompanionResponseSignatureHeader,
                out IEnumerable<string>? signatures))
        {
            return false;
        }
        string? suppliedSignature = signatures.SingleOrDefault();
        if (suppliedSignature is null
            || !TryBase64UrlDecode(
                suppliedSignature,
                out byte[] suppliedSignatureBytes)
            || suppliedSignatureBytes.Length != 32)
        {
            return false;
        }

        string instanceId = _verifiedEnhancementCompanionInstanceId!;
        string serverStartedAtUtc =
            _verifiedEnhancementCompanionServerStartedAtUtc!;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                envelopeBytes.ToArray());
            JsonElement envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object
                || !TryGetStringProperty(
                    envelope,
                    "protocol",
                    out string? protocol)
                || !string.Equals(
                    protocol,
                    EnhancementCompanionResponseAuthProtocol,
                    StringComparison.Ordinal)
                || !TryGetStringProperty(
                    envelope,
                    "requestNonce",
                    out string? echoedNonce)
                || !string.Equals(
                    echoedNonce,
                    requestNonce,
                    StringComparison.Ordinal)
                || !TryGetStringProperty(
                    envelope,
                    "instanceId",
                    out string? echoedInstance)
                || !string.Equals(
                    echoedInstance,
                    instanceId,
                    StringComparison.Ordinal)
                || !TryGetStringProperty(
                    envelope,
                    "serverStartedAtUtc",
                    out string? echoedEpoch)
                || !string.Equals(
                    echoedEpoch,
                    serverStartedAtUtc,
                    StringComparison.Ordinal)
                || !envelope.TryGetProperty(
                    "status",
                    out JsonElement statusElement)
                || !statusElement.TryGetInt32(out innerStatusCode)
                || innerStatusCode is < 100 or > 599
                || !TryGetStringProperty(envelope, "iv", out string? ivRaw)
                || !TryGetStringProperty(
                    envelope,
                    "ciphertext",
                    out string? ciphertextRaw)
                || !TryGetStringProperty(envelope, "tag", out string? tagRaw)
                || ivRaw is null
                || ciphertextRaw is null
                || tagRaw is null
                || !TryBase64UrlDecode(ivRaw, out byte[] iv)
                || iv.Length != 12
                || !TryBase64UrlDecode(
                    ciphertextRaw,
                    out byte[] ciphertext)
                || !TryBase64UrlDecode(tagRaw, out byte[] tag)
                || tag.Length != 16)
            {
                return false;
            }

            string bodyHash = Base64UrlEncode(
                SHA256.HashData(envelopeBytes));
            string responseMessage =
                $"{EnhancementCompanionResponseAuthProtocol}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}\0{innerStatusCode}\0{bodyHash}";
            using var hmac = new HMACSHA256(authTokenBytes);
            byte[] expectedSignature = hmac.ComputeHash(
                Encoding.UTF8.GetBytes(responseMessage));
            if (!CryptographicOperations.FixedTimeEquals(
                    suppliedSignatureBytes,
                    expectedSignature))
            {
                return false;
            }

            byte[] tunnelKey = DeriveEnhancementCompanionTunnelKey(
                authTokenBytes,
                instanceId,
                serverStartedAtUtc);
            plaintext = new byte[ciphertext.Length];
            byte[] aad = Encoding.UTF8.GetBytes(
                $"{EnhancementCompanionResponseAuthProtocol}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}\0{innerStatusCode}");
            using var aes = new AesGcm(tunnelKey, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
            return true;
        }
        catch (Exception ex) when (
            ex is JsonException
                or CryptographicException
                or FormatException
                or InvalidOperationException)
        {
            innerStatusCode = 0;
            plaintext = [];
            return false;
        }
    }

    private static bool IsValidEnhancementCompanionAuthToken(string? value)
        => value is { Length: 43 }
            && TryBase64UrlDecode(value, out byte[] decoded)
            && decoded.Length == 32;

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(
        string value,
        out byte[] decoded)
    {
        decoded = [];
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                0 => "",
                _ => throw new FormatException(),
            };
            decoded = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsReadyEnhancementCompanionResponse(EnhancementApiResponse response)
        => response.Ok
            && response.Payload is JsonElement payload
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("version", out JsonElement version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out int versionNumber)
            && versionNumber == 1
            && payload.TryGetProperty("status", out JsonElement status)
            && status.ValueKind == JsonValueKind.String
            && status.GetString() is "healthy" or "working" or "needs-attention"
            && payload.TryGetProperty("jobs", out JsonElement jobs)
            && jobs.ValueKind == JsonValueKind.Object
            && jobs.TryGetProperty("counts", out JsonElement counts)
            && counts.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("worker", out JsonElement worker)
            && worker.ValueKind == JsonValueKind.Object;

    private static EnhancementApiResponse InvalidEnhancementCompanionReadiness(
        EnhancementApiResponse response)
        => response.Ok
            ? new EnhancementApiResponse(
                false,
                response.StatusCode,
                response.Payload,
                "The local AI companion health response was malformed or unsupported.")
            : response;

    private bool TryStartOwnedEnhancementCompanion(out string error)
    {
        error = "";
        Uri endpoint = ResolveBrowserEnhancementBaseUri();
        if (!TryGetOrCreateEnhancementCompanionAuthToken(
                out string authToken,
                out error))
        {
            return false;
        }
        _ownedEnhancementCompanionInstanceId = Base64UrlEncode(
            RandomNumberGenerator.GetBytes(24));
        _enhancementCompanionOwnershipVerified = false;
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
            error = "Aibos could not find the local AI companion. Set AIBOS_COMPANION_ROOT when it is stored separately from this build.";
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
                endpoint,
                authToken,
                _ownedEnhancementCompanionInstanceId);

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

            var observation = new EnhancementCompanionProcessObservation(
                process.Id,
                Stopwatch.GetTimestamp());
            _ownedEnhancementCompanion = process;
            _ownedEnhancementCompanionObservation = observation;
            AibosOperationLog.Write(
                "companion.process",
                "started",
                mode: "owned",
                relatedProcessId: observation.ProcessId);
            try
            {
                process.Exited += (_, _) =>
                    LogEnhancementCompanionProcessExit(process, observation);
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is
                InvalidOperationException or
                Win32Exception or
                NotSupportedException)
            {
                // Diagnostics are best-effort. A listener that started
                // successfully must not be torn down only because Windows
                // process-exit observation is unavailable.
                AibosOperationLog.Write(
                    "companion.process",
                    "monitor_failed",
                    errorCode: "exit_monitor_unavailable",
                    mode: "owned",
                    relatedProcessId: observation.ProcessId);
            }
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
        Uri endpoint,
        string authToken,
        string instanceId)
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
        startInfo.ArgumentList.Add("--defer-queue-recovery");
        startInfo.Environment["PVU_NO_OPEN"] = "1";
        startInfo.Environment["PVU_COMFY_AUTOSTART"] = "0";
        startInfo.Environment["AIBOS_COMPANION_AUTH_TOKEN"] = authToken;
        startInfo.Environment["AIBOS_COMPANION_INSTANCE_ID"] = instanceId;
        // Do not set PVU_OWNER_PID. The companion owns the durable FIFO worker
        // after an explicit AI action and must outlive the WPF viewer process.
        startInfo.Environment.Remove("PVU_OWNER_PID");
        return startInfo;
    }

    private static ValidatedEnhancementCompanionRoot? ResolveEnhancementCompanionRoot()
        => ResolveEnhancementCompanionRoot(
            FirstConfiguredEnhancementCompanionRoot(
                Environment.GetEnvironmentVariable("AIBOS_COMPANION_ROOT"),
                Environment.GetEnvironmentVariable("AIBOS_H25_COMPANION_ROOT")),
            AppContext.BaseDirectory,
            UseLegacyNextCompanionLauncher());

    private static string? FirstConfiguredEnhancementCompanionRoot(
        string? configuredRoot,
        string? compatibilityRoot)
        => !string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot
            : compatibilityRoot;

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
        _enhancementCompanionOwnershipVerified = false;
        _ownedEnhancementCompanionInstanceId = null;
        EnhancementCompanionProcessObservation? observation =
            Interlocked.Exchange(
                ref _ownedEnhancementCompanionObservation,
                null);
        Process? process = Interlocked.Exchange(ref _ownedEnhancementCompanion, null);
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                observation?.MarkStopRequested();
                AibosOperationLog.Write(
                    "companion.process",
                    "stop_requested",
                    errorCode: "wpf_requested",
                    mode: "owned",
                    relatedProcessId: observation?.ProcessId ?? process.Id);
                process.Kill(entireProcessTree: true);
            }
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
        EnhancementCompanionProcessObservation? observation =
            Interlocked.Exchange(
                ref _ownedEnhancementCompanionObservation,
                null);
        Process? process = Interlocked.Exchange(
            ref _ownedEnhancementCompanion,
            null);
        if (observation is not null)
        {
            observation.MarkOwnershipReleased();
            AibosOperationLog.Write(
                "companion.process",
                "ownership_released",
                elapsedMilliseconds: (long)Stopwatch.GetElapsedTime(
                    observation.StartedTimestamp).TotalMilliseconds,
                errorCode: "wpf_closed",
                mode: "released",
                relatedProcessId: observation.ProcessId);
        }
        process?.Dispose();
    }

    private static void LogEnhancementCompanionProcessExit(
        Process process,
        EnhancementCompanionProcessObservation observation)
    {
        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
        }

        int disposition = observation.Disposition;
        bool expectedStop = disposition ==
            EnhancementCompanionProcessObservation.StopRequested;
        string outcome = disposition switch
        {
            EnhancementCompanionProcessObservation.StopRequested
                => "expected_exit",
            EnhancementCompanionProcessObservation.OwnershipReleased
                => "exit_after_release",
            _ => "unexpected_exit",
        };
        string errorCode = disposition ==
                EnhancementCompanionProcessObservation.OwnershipReleased
            ? "ownership_released"
            : ClassifyEnhancementCompanionExit(expectedStop, exitCode);
        string mode = disposition ==
                EnhancementCompanionProcessObservation.OwnershipReleased
            ? "released"
            : "owned";
        AibosOperationLog.Write(
            "companion.process",
            outcome,
            elapsedMilliseconds: (long)Stopwatch.GetElapsedTime(
                observation.StartedTimestamp).TotalMilliseconds,
            errorCode: errorCode,
            mode: mode,
            relatedProcessId: observation.ProcessId,
            exitCode: exitCode);
    }

    private static string ClassifyEnhancementCompanionExit(
        bool expectedStop,
        int? exitCode)
    {
        if (expectedStop)
            return "wpf_requested";
        if (exitCode is null)
            return "exit_code_unavailable";
        if (exitCode == 0)
            return "self_exit_zero";
        if (exitCode == -1)
            return "terminated_or_aborted";

        uint nativeExitCode = unchecked((uint)exitCode.Value);
        return nativeExitCode switch
        {
            0xC000013A => "console_or_shutdown",
            0xC0000005 => "access_violation",
            _ => "abnormal_or_forced",
        };
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
    public static string ClassifyEnhancementCompanionExitForSmoke(
        bool expectedStop,
        int? exitCode)
        => ClassifyEnhancementCompanionExit(expectedStop, exitCode);
    public static string? ResolveEnhancementCompanionRootForSmoke()
        => ResolveEnhancementCompanionRoot()?.RootPath;
    public static string? SelectEnhancementCompanionRootSettingForSmoke(
        string? configuredRoot,
        string? compatibilityRoot)
        => FirstConfiguredEnhancementCompanionRoot(
            configuredRoot,
            compatibilityRoot);
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
            new Uri("http://127.0.0.1:3000"),
            EnhancementCompanionSmokeAuthToken,
            "companion-smoke-instance-v1");
        return new(
            startInfo.UseShellExecute,
            startInfo.CreateNoWindow,
            startInfo.RedirectStandardOutput,
            startInfo.RedirectStandardError,
            !string.IsNullOrEmpty(startInfo.WorkingDirectory),
            startInfo.Environment.ContainsKey("PVU_OWNER_PID"),
            startInfo.Environment.ContainsKey("AIBOS_COMPANION_AUTH_TOKEN"),
            startInfo.Environment.ContainsKey("AIBOS_COMPANION_INSTANCE_ID"),
            startInfo.Environment["PVU_NO_OPEN"],
            startInfo.Environment["PVU_COMFY_AUTOSTART"],
            startInfo.ArgumentList.Contains("--defer-queue-recovery"),
            Path.GetFileName(startInfo.ArgumentList[0]));
    }
    public void ConfigureEnhancementCompanionAutoStartForSmoke(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        Func<Uri, (bool Started, string Error)> starter)
    {
        _modalEnhancementSender = sender;
        _usingDefaultModalEnhancementSender = true;
        _startEnhancementCompanionForSmoke = starter;
        _enhancementCompanionAuthToken = EnhancementCompanionSmokeAuthToken;
        _enhancementCompanionOwnershipVerified = false;
    }

    public void EnableEnhancementCompanionAutoStartProbeForSmoke(
        Func<Uri, (bool Started, string Error)> starter)
    {
        // This helper observes launch attempts only. It must not replace a
        // smoke-provided HTTP sender with the production authenticated path.
        _startEnhancementCompanionForSmoke = starter;
    }

    public async Task<IdempotentEnhancementMutationSmokeSnapshot>
        SendIdempotentEnhancementMutationForSmokeAsync(
            HttpMethod method,
            string relativePath,
            object? body = null)
    {
        EnhancementApiResponse response =
            await SendIdempotentEnhancementMutationAsync(
            method,
            relativePath,
            body);
        return new(
            response.Ok,
            response.StatusCode,
            response.Payload,
            response.Error);
    }

    public sealed record IdempotentEnhancementMutationSmokeSnapshot(
        bool Ok,
        int StatusCode,
        JsonElement? Payload,
        string Error);

    public void KickDurableEnqueueRecoveryForSmoke(string requestId)
        => KickEnhancementCompanionRecoveryAfterDurablePublish(
            sourceIdentity: null,
            requestId);

    public async Task WaitForDurableEnqueueRecoveryForSmokeAsync()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            bool idle;
            lock (_enhancementCompanionDurableRecoverySync)
            {
                idle = !_enhancementCompanionDurableRecoveryRunning
                    && !_enhancementCompanionDurableRecoveryRequested;
            }
            if (idle)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException(
            "The durable enqueue recovery smoke did not become idle.");
    }

    public IReadOnlyDictionary<string, object>
        EnhancementCompanionIdentityPayloadForSmoke(string challenge)
    {
        if (!TryBase64UrlDecode(
                EnhancementCompanionSmokeAuthToken,
                out byte[] authTokenBytes))
        {
            throw new InvalidOperationException("Smoke authentication token is invalid.");
        }
        string instanceId = _ownedEnhancementCompanionInstanceId
            ?? "companion-smoke-instance-v1";
        int processId = Environment.ProcessId;
        string serverStartedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        using var hmac = new HMACSHA256(authTokenBytes);
        string proof = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            $"{EnhancementCompanionAuthProtocol}\0{challenge}\0{instanceId}\0{processId}\0{serverStartedAtUtc}")));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["protocol"] = EnhancementCompanionAuthProtocol,
            ["instanceId"] = instanceId,
            ["processId"] = processId,
            ["serverStartedAtUtc"] = serverStartedAtUtc,
            ["challenge"] = challenge,
            ["proof"] = proof,
        };
    }

    public static bool HasCompanionRequestAuthenticationForSmoke(
        HttpRequestMessage request)
        => request.Headers.Contains(EnhancementCompanionTimestampHeader)
            && request.Headers.Contains(EnhancementCompanionNonceHeader)
            && request.Headers.Contains(EnhancementCompanionSignatureHeader)
            && request.Headers.Contains(EnhancementCompanionInstanceHeader)
            && request.Headers.Contains(EnhancementCompanionEpochHeader)
            && request.Headers.Authorization is null;

    public async Task<EnhancementCompanionSecureRequestSmokeSnapshot?>
        DecodeEnhancementCompanionSecureRequestForSmokeAsync(
            HttpRequestMessage request,
            CancellationToken token)
    {
        try
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsolutePath.EndsWith(
                    "/api/enhance/secure",
                    StringComparison.Ordinal) != true
                || request.Content is null
                || !HasCompanionRequestAuthenticationForSmoke(request)
                || !IsValidEnhancementCompanionAuthToken(
                    _enhancementCompanionAuthToken)
                || !TryBase64UrlDecode(
                    _enhancementCompanionAuthToken!,
                    out byte[] authTokenBytes)
                || !request.Headers.TryGetValues(
                    EnhancementCompanionTimestampHeader,
                    out IEnumerable<string>? timestamps)
                || !request.Headers.TryGetValues(
                    EnhancementCompanionNonceHeader,
                    out IEnumerable<string>? nonces)
                || !request.Headers.TryGetValues(
                    EnhancementCompanionSignatureHeader,
                    out IEnumerable<string>? signatures)
                || !request.Headers.TryGetValues(
                    EnhancementCompanionInstanceHeader,
                    out IEnumerable<string>? instances)
                || !request.Headers.TryGetValues(
                    EnhancementCompanionEpochHeader,
                    out IEnumerable<string>? epochs))
            {
                return null;
            }
            string timestamp = timestamps.Single();
            string nonce = nonces.Single();
            string suppliedSignature = signatures.Single();
            string instanceId = instances.Single();
            string serverStartedAtUtc = epochs.Single();
            if (!string.Equals(
                    instanceId,
                    _verifiedEnhancementCompanionInstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    serverStartedAtUtc,
                    _verifiedEnhancementCompanionServerStartedAtUtc,
                    StringComparison.Ordinal))
            {
                return null;
            }
            byte[] envelopeBytes = await request.Content.ReadAsByteArrayAsync(
                token);
            string bodyHash = Base64UrlEncode(SHA256.HashData(envelopeBytes));
            string message =
                $"{EnhancementCompanionRequestAuthProtocol}\0{timestamp}\0{nonce}\0{instanceId}\0{serverStartedAtUtc}\0POST\0{request.RequestUri.PathAndQuery}\0{bodyHash}";
            using var hmac = new HMACSHA256(authTokenBytes);
            string expectedSignature = Base64UrlEncode(hmac.ComputeHash(
                Encoding.UTF8.GetBytes(message)));
            if (!string.Equals(
                    suppliedSignature,
                    expectedSignature,
                    StringComparison.Ordinal))
            {
                return null;
            }
            using JsonDocument envelopeDocument = JsonDocument.Parse(
                envelopeBytes);
            JsonElement envelope = envelopeDocument.RootElement;
            if (!TryGetStringProperty(envelope, "protocol", out string? protocol)
                || !string.Equals(
                    protocol,
                    EnhancementCompanionTunnelProtocol,
                    StringComparison.Ordinal)
                || !TryGetStringProperty(envelope, "iv", out string? ivRaw)
                || !TryGetStringProperty(
                    envelope,
                    "ciphertext",
                    out string? ciphertextRaw)
                || !TryGetStringProperty(envelope, "tag", out string? tagRaw)
                || ivRaw is null
                || ciphertextRaw is null
                || tagRaw is null
                || !TryBase64UrlDecode(ivRaw, out byte[] iv)
                || !TryBase64UrlDecode(
                    ciphertextRaw,
                    out byte[] ciphertext)
                || !TryBase64UrlDecode(tagRaw, out byte[] tag))
            {
                return null;
            }
            byte[] plaintext = new byte[ciphertext.Length];
            byte[] aad = Encoding.UTF8.GetBytes(
                $"{EnhancementCompanionTunnelProtocol}\0{timestamp}\0{nonce}\0{instanceId}\0{serverStartedAtUtc}");
            using (var aes = new AesGcm(
                DeriveEnhancementCompanionTunnelKey(
                    authTokenBytes,
                    instanceId,
                    serverStartedAtUtc),
                tag.Length))
            {
                aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
            }
            using JsonDocument plaintextDocument = JsonDocument.Parse(
                plaintext);
            JsonElement inner = plaintextDocument.RootElement;
            string method = inner.GetProperty("method").GetString() ?? "";
            string pathAndQuery =
                inner.GetProperty("pathAndQuery").GetString() ?? "";
            string? bodyBase64Url = inner.TryGetProperty(
                    "bodyBase64Url",
                    out JsonElement bodyElement)
                && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : null;
            string? idempotencyKey = inner.TryGetProperty(
                    "idempotencyKey",
                    out JsonElement idempotencyElement)
                && idempotencyElement.ValueKind == JsonValueKind.String
                ? idempotencyElement.GetString()
                : null;
            string? bodyJson = bodyBase64Url is not null
                && TryBase64UrlDecode(bodyBase64Url, out byte[] bodyBytes)
                ? Encoding.UTF8.GetString(bodyBytes)
                : null;
            return new(
                method,
                pathAndQuery,
                bodyJson,
                idempotencyKey,
                Encoding.UTF8.GetString(envelopeBytes));
        }
        catch (Exception ex) when (
            ex is JsonException
                or CryptographicException
                or FormatException
                or InvalidOperationException)
        {
            return null;
        }
    }

    public HttpResponseMessage EnhancementCompanionSecureResponseForSmoke(
        HttpRequestMessage request,
        int innerStatusCode,
        object payload)
    {
        string requestNonce = request.Headers
            .GetValues(EnhancementCompanionNonceHeader)
            .Single();
        string instanceId = _verifiedEnhancementCompanionInstanceId
            ?? throw new InvalidOperationException(
                "Companion identity was not verified.");
        string serverStartedAtUtc =
            _verifiedEnhancementCompanionServerStartedAtUtc
            ?? throw new InvalidOperationException(
                "Companion epoch was not verified.");
        if (!TryBase64UrlDecode(
                _enhancementCompanionAuthToken!,
                out byte[] authTokenBytes))
        {
            throw new InvalidOperationException(
                "Companion authentication token was invalid.");
        }
        byte[] responseBody = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[responseBody.Length];
        byte[] tag = new byte[16];
        byte[] aad = Encoding.UTF8.GetBytes(
            $"{EnhancementCompanionResponseAuthProtocol}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}\0{innerStatusCode}");
        using (var aes = new AesGcm(
            DeriveEnhancementCompanionTunnelKey(
                authTokenBytes,
                instanceId,
                serverStartedAtUtc),
            tag.Length))
        {
            aes.Encrypt(iv, responseBody, ciphertext, tag, aad);
        }
        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = EnhancementCompanionResponseAuthProtocol,
            requestNonce,
            instanceId,
            serverStartedAtUtc,
            status = innerStatusCode,
            iv = Base64UrlEncode(iv),
            ciphertext = Base64UrlEncode(ciphertext),
            tag = Base64UrlEncode(tag),
        });
        string bodyHash = Base64UrlEncode(SHA256.HashData(envelopeBytes));
        string message =
            $"{EnhancementCompanionResponseAuthProtocol}\0{requestNonce}\0{instanceId}\0{serverStartedAtUtc}\0{innerStatusCode}\0{bodyHash}";
        using var hmac = new HMACSHA256(authTokenBytes);
        string signature = Base64UrlEncode(hmac.ComputeHash(
            Encoding.UTF8.GetBytes(message)));
        var response = new HttpResponseMessage(
            System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(envelopeBytes),
        };
        response.Headers.TryAddWithoutValidation(
            EnhancementCompanionResponseSignatureHeader,
            signature);
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/json");
        return response;
    }

    public async Task<bool> EnsureEnhancementCompanionForExplicitActionForSmokeAsync()
    {
        EnhancementApiResponse response = await EnsureEnhancementCompanionReadyForExplicitActionAsync();
        return response.Ok;
    }
    public async Task<string> SendEnhancementEnqueueErrorForSmokeAsync(object body)
    {
        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(body);
        return response.Error;
    }
    public async Task<bool> StartEnhancementCompanionApiForApplicationLaunchForSmokeAsync()
    {
        EnhancementApiResponse response =
            await EnsureEnhancementCompanionApiReadyAsync();
        return response.Ok;
    }
    public async Task<bool> SendEnhancementEnqueueForSmokeAsync(object body)
    {
        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(body);
        return response.SavedForDelivery;
    }
    public async Task<bool> SendEnhancementEnqueueWithListenerHandoffForSmokeAsync(
        object body,
        Action onBeforePublish)
    {
        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
            body,
            prePublishValidator: () =>
            {
                onBeforePublish();
                return null;
            });
        return response.SavedForDelivery;
    }
    public async Task<bool> SendEnhancementSecureRequestForSmokeAsync(
        object body)
    {
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Post,
            "api/enhance/jobs",
            body);
        return response.Ok;
    }
    public async Task<bool> SendPassiveEnhancementReadForSmokeAsync()
    {
        EnhancementApiResponse response = await SendPassiveEnhancementReadAsync(
            "api/enhance/jobs");
        return response.Ok;
    }
    public async Task<bool> MixedRetryCapabilityPartitionForSmokeAsync()
    {
        DurableEnhancementBatchResponse response =
            await TrySendDurableEnhancementBatchCoreAsync(
            [
                new DurableEnhancementBatchItem(null, "capability-rejected-video-job"),
                new DurableEnhancementBatchItem(null, "capability-accepted-image-job"),
            ],
            itemHealthValidators:
            [
                static _ => "Synthetic video capability is unavailable.",
                null,
            ],
            requireExactItemHealthValidation: true);
        return response.PublishedCount == 1
            && response.Responses.Length == 2
            && response.Responses[0] is
            {
                Ok: false,
                StatusCode: 426,
                SavedForDelivery: false,
            }
            && response.Responses[1].Ok;
    }
    public async Task<int> SendEnhancementBatchForSmokeAsync(
        IReadOnlyList<object?> bodies)
    {
        DurableEnhancementBatchResponse response =
            await TrySendDurableEnhancementBatchAsync(bodies);
        return response.PublishedCount;
    }

    public async Task<bool> DurableBatchDefinitiveFailureIsPerItemForSmokeAsync(
        IReadOnlyList<object?> bodies,
        Func<(bool Observed, bool Bodyless, string? RequestId)> captureNudge)
    {
        DurableEnhancementBatchResponse response =
            await TrySendDurableEnhancementBatchAsync(bodies);
        (bool nudgeObserved, bool nudgeBodyless, string? nudgeRequestId) =
            captureNudge();
        return bodies.Count == 2
            && nudgeObserved
            && nudgeBodyless
            && !string.IsNullOrWhiteSpace(nudgeRequestId)
            && response.PublishedCount == 2
            && response.NudgeCount == 1
            && response.Responses.Length == 2
            && response.Responses[0] is
            {
                Ok: false,
                StatusCode: 409,
                SavedForDelivery: false,
                InnerStatusAuthoritative: true,
            }
            && response.Responses[1] is
            {
                Ok: true,
                StatusCode: 202,
                SavedForDelivery: true,
            }
            && !string.IsNullOrWhiteSpace(
                response.Responses[1].DeliveryRequestId)
            && !string.Equals(
                response.Responses[1].DeliveryRequestId,
                nudgeRequestId,
                StringComparison.Ordinal);
    }

    public static bool ReceiptOnlyDurableResponseIsPendingForSmoke()
    {
        EnhancementEnqueueInboxItem item =
            EnhancementEnqueueInboxStore.CreateItem(
                new { operation = "photoreal" },
                "last",
                0);
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                receipt = new
                {
                    idempotencyKey = item.RequestId,
                    jobId = "already-consumed-job",
                },
            }));
        EnhancementApiResponse response = new(
            true,
            200,
            document.RootElement.Clone(),
            "");
        EnhancementApiResponse normalized =
            NormalizeDurableEnqueueResponse(response, item);
        return normalized.Ok
            && normalized.SavedForDelivery
            && normalized.StatusCode == 202
            && string.IsNullOrWhiteSpace(normalized.Error);
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
    public static EnhancementCompanionAuthStorageSmokeSnapshot
        EnhancementCompanionAuthStorageContractForSmoke(
            string fixtureRoot,
            string junctionFixtureRoot)
    {
        string PrepareRoot(string name)
        {
            string root = Path.Combine(fixtureRoot, name);
            // fixtureRoot is a managed TEMP root from the smoke harness.
            // codeql[cs/path-injection]
            Directory.CreateDirectory(root);
            return root;
        }

        void WriteSyntheticFile(
            EnhancementCompanionAuthStoragePath storage,
            byte[] bytes,
            bool foreignAllow)
        {
            if (!TryAcquireEnhancementCompanionAuthDirectoryLease(
                    storage,
                    out EnhancementCompanionAuthDirectoryLease? lease))
            {
                throw new InvalidDataException(
                    "The synthetic companion authentication directory was rejected.");
            }
            using (lease)
            {
                FileSecurity security = BuildCurrentUserOnlyAuthFileSecurity();
                if (foreignAllow)
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        FileSystemRights.ReadData,
                        AccessControlType.Allow));
                }
                // The synthetic storage capability is handle-bound below TEMP.
                // codeql[cs/path-injection]
                using FileStream stream = FileSystemAclExtensions.Create(
                    new FileInfo(storage.FilePath),
                    FileMode.CreateNew,
                    FileSystemRights.FullControl,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough,
                    security);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }

        string primaryRoot = PrepareRoot("auth-primary");
        EnhancementCompanionAuthStoragePath primary =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                primaryRoot);
        bool firstCreated = TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
            primary,
            out string firstToken,
            out _);
        // The synthetic capability owns this fixed leaf below TEMP.
        // codeql[cs/path-injection]
        byte[] primaryBytes = firstCreated
            ? File.ReadAllBytes(primary.FilePath)
            : Array.Empty<byte>();
        // codeql[cs/path-injection]
        DateTime primaryWriteUtc = firstCreated
            ? File.GetLastWriteTimeUtc(primary.FilePath)
            : DateTime.MinValue;
        bool secondRead = TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
            primary,
            out string secondToken,
            out _);
        // codeql[cs/path-injection]
        byte[] primaryBytesAfter = secondRead
            ? File.ReadAllBytes(primary.FilePath)
            : Array.Empty<byte>();
        // codeql[cs/path-injection]
        DateTime primaryWriteUtcAfter = secondRead
            ? File.GetLastWriteTimeUtc(primary.FilePath)
            : DateTime.MaxValue;
        bool createAndStableReread = firstCreated
            && secondRead
            && IsValidEnhancementCompanionAuthToken(firstToken)
            && string.Equals(firstToken, secondToken, StringComparison.Ordinal)
            && primaryBytes.AsSpan().SequenceEqual(primaryBytesAfter)
            && primaryWriteUtc == primaryWriteUtcAfter
            && !Encoding.ASCII.GetString(primaryBytes).Contains(
                firstToken,
                StringComparison.Ordinal);

        string malformedRoot = PrepareRoot("auth-malformed");
        EnhancementCompanionAuthStoragePath malformed =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                malformedRoot);
        byte[] malformedSeed = Encoding.ASCII.GetBytes("not-a-dpapi-envelope");
        WriteSyntheticFile(malformed, malformedSeed, foreignAllow: false);
        // codeql[cs/path-injection]
        DateTime malformedWriteUtc = File.GetLastWriteTimeUtc(malformed.FilePath);
        bool malformedRejected =
            !TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
                malformed,
                out _,
                out _)
            // codeql[cs/path-injection]
            && File.ReadAllBytes(malformed.FilePath).AsSpan()
                .SequenceEqual(malformedSeed)
            // codeql[cs/path-injection]
            && File.GetLastWriteTimeUtc(malformed.FilePath) == malformedWriteUtc;

        string foreignFileRoot = PrepareRoot("auth-foreign-file");
        EnhancementCompanionAuthStoragePath foreignFile =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                foreignFileRoot);
        byte[] foreignFileSeed = primaryBytes;
        WriteSyntheticFile(foreignFile, foreignFileSeed, foreignAllow: true);
        // codeql[cs/path-injection]
        DateTime foreignFileWriteUtc = File.GetLastWriteTimeUtc(foreignFile.FilePath);
        // The synthetic capability owns this fixed leaf below TEMP.
        // codeql[cs/path-injection]
        string foreignFileSddl = new FileInfo(foreignFile.FilePath)
            .GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Owner | AccessControlSections.Access);
        bool foreignFileRejected =
            !TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
                foreignFile,
                out _,
                out _)
            // codeql[cs/path-injection]
            && File.ReadAllBytes(foreignFile.FilePath).AsSpan()
                .SequenceEqual(foreignFileSeed)
            // codeql[cs/path-injection]
            && File.GetLastWriteTimeUtc(foreignFile.FilePath)
                == foreignFileWriteUtc
            // codeql[cs/path-injection]
            && string.Equals(
                foreignFileSddl,
                new FileInfo(foreignFile.FilePath)
                    .GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(
                        AccessControlSections.Owner | AccessControlSections.Access),
                StringComparison.Ordinal);

        string oversizedRoot = PrepareRoot("auth-oversized");
        EnhancementCompanionAuthStoragePath oversized =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                oversizedRoot);
        byte[] oversizedSeed = Enumerable.Repeat((byte)'A', 1025).ToArray();
        WriteSyntheticFile(oversized, oversizedSeed, foreignAllow: false);
        // codeql[cs/path-injection]
        DateTime oversizedWriteUtc = File.GetLastWriteTimeUtc(oversized.FilePath);
        bool oversizedRejected =
            !TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
                oversized,
                out _,
                out _)
            // codeql[cs/path-injection]
            && File.ReadAllBytes(oversized.FilePath).AsSpan()
                .SequenceEqual(oversizedSeed)
            // codeql[cs/path-injection]
            && File.GetLastWriteTimeUtc(oversized.FilePath) == oversizedWriteUtc;

        string foreignDirectoryRoot = PrepareRoot("auth-foreign-directory");
        EnhancementCompanionAuthStoragePath foreignDirectory =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                foreignDirectoryRoot);
        // Both names are fixed children of a managed TEMP capability.
        // codeql[cs/path-injection]
        Directory.CreateDirectory(foreignDirectory.ApplicationDirectoryPath);
        DirectorySecurity foreignDirectorySecurity =
            BuildCurrentUserOnlyAuthDirectorySecurity();
        foreignDirectorySecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        // codeql[cs/path-injection]
        FileSystemAclExtensions.Create(
            new DirectoryInfo(foreignDirectory.DirectoryPath),
            foreignDirectorySecurity);
        // codeql[cs/path-injection]
        DirectorySecurity foreignDirectoryBefore = new DirectoryInfo(
                foreignDirectory.DirectoryPath)
            .GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
        string foreignDirectorySddl = foreignDirectoryBefore
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Owner | AccessControlSections.Access);
        bool directoryAccepted = TryAcquireEnhancementCompanionAuthDirectoryLease(
            foreignDirectory,
            out EnhancementCompanionAuthDirectoryLease? unexpectedDirectoryLease);
        unexpectedDirectoryLease?.Dispose();
        // codeql[cs/path-injection]
        string foreignDirectorySddlAfter = new DirectoryInfo(
                foreignDirectory.DirectoryPath)
            .GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(
                AccessControlSections.Owner | AccessControlSections.Access);
        bool foreignDirectoryRejected = !directoryAccepted
            && string.Equals(
                foreignDirectorySddl,
                foreignDirectorySddlAfter,
                StringComparison.Ordinal);

        string failureRoot = PrepareRoot("auth-failed-create");
        EnhancementCompanionAuthStoragePath failedCreate =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                failureRoot);
        bool injectedFailureRejected =
            !TryGetOrCreateEnhancementCompanionAuthTokenForStorage(
                failedCreate,
                out _,
                out _,
                failAfterCreateForSmoke: true)
            // codeql[cs/path-injection]
            && !File.Exists(failedCreate.FilePath);

        byte[] junctionSentinel = Encoding.ASCII.GetBytes(
            "outside-companion-auth-sentinel");
        EnhancementCompanionAuthStoragePath junctionFixture =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                junctionFixtureRoot);
        string validatedJunctionFixtureRoot = junctionFixture.RootPath;
        string applicationJunctionRoot = Path.Combine(
            validatedJunctionFixtureRoot,
            "auth-app-junction");
        EnhancementCompanionAuthStoragePath applicationJunction =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                applicationJunctionRoot);
        string applicationJunctionOutside =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                Path.Combine(
                    validatedJunctionFixtureRoot,
                    "auth-app-junction-outside"))
            .RootPath;
        string applicationJunctionSentinel = Path.Combine(
            applicationJunctionOutside,
            "sentinel.bin");
        bool applicationJunctionAccepted =
            TryAcquireEnhancementCompanionAuthDirectoryLease(
                applicationJunction,
                out EnhancementCompanionAuthDirectoryLease?
                    unexpectedApplicationJunctionLease);
        unexpectedApplicationJunctionLease?.Dispose();
        bool applicationJunctionRejected = !applicationJunctionAccepted
            // The PowerShell verifier owns this TEMP-only junction fixture.
            // codeql[cs/path-injection]
            && (File.GetAttributes(applicationJunction.ApplicationDirectoryPath)
                & FileAttributes.ReparsePoint) != 0
            // codeql[cs/path-injection]
            && File.ReadAllBytes(applicationJunctionSentinel).AsSpan()
                .SequenceEqual(junctionSentinel)
            // codeql[cs/path-injection]
            && !Directory.Exists(Path.Combine(
                applicationJunctionOutside,
                "companion-auth-v1"));

        string authJunctionRoot = Path.Combine(
            validatedJunctionFixtureRoot,
            "auth-directory-junction");
        EnhancementCompanionAuthStoragePath authJunction =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                authJunctionRoot);
        string authJunctionOutside =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                Path.Combine(
                    validatedJunctionFixtureRoot,
                    "auth-directory-junction-outside"))
            .RootPath;
        string authJunctionSentinel = Path.Combine(
            authJunctionOutside,
            "sentinel.bin");
        bool authJunctionAccepted =
            TryAcquireEnhancementCompanionAuthDirectoryLease(
                authJunction,
                out EnhancementCompanionAuthDirectoryLease?
                    unexpectedAuthJunctionLease);
        unexpectedAuthJunctionLease?.Dispose();
        bool authJunctionRejected = !authJunctionAccepted
            // The PowerShell verifier owns this TEMP-only junction fixture.
            // codeql[cs/path-injection]
            && (File.GetAttributes(authJunction.DirectoryPath)
                & FileAttributes.ReparsePoint) != 0
            // codeql[cs/path-injection]
            && File.ReadAllBytes(authJunctionSentinel).AsSpan()
                .SequenceEqual(junctionSentinel)
            // codeql[cs/path-injection]
            && !File.Exists(Path.Combine(
                authJunctionOutside,
                "companion-auth-v1.key"));

        string leaseRoot = PrepareRoot("auth-directory-lease");
        EnhancementCompanionAuthStoragePath leased =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                leaseRoot);
        bool leaseAcquired = TryAcquireEnhancementCompanionAuthDirectoryLease(
            leased,
            out EnhancementCompanionAuthDirectoryLease? activeLease);
        string movedDirectory = leased.DirectoryPath + "-moved";
        bool moveBlocked = false;
        bool moveSucceeded = false;
        bool renameDetected = false;
        if (leaseAcquired && activeLease is not null)
        {
            using (activeLease)
            {
                try
                {
                    // Windows normally permits this rename. The retained
                    // identity must then report that its final path changed.
                    // codeql[cs/path-injection]
                    Directory.Move(leased.DirectoryPath, movedDirectory);
                    moveSucceeded = true;
                    renameDetected = !activeLease.IsStillBound();
                }
                catch (Exception ex) when (ex is
                    IOException or
                    UnauthorizedAccessException)
                {
                    moveBlocked = true;
                }
            }
        }
        // codeql[cs/path-injection]
        bool originalExists = Directory.Exists(leased.DirectoryPath);
        // codeql[cs/path-injection]
        bool movedExists = Directory.Exists(movedDirectory);
        bool directoryRenameContained = leaseAcquired
            && ((moveBlocked && originalExists && !movedExists)
                || (moveSucceeded
                    && renameDetected
                    && !originalExists
                    && movedExists));

        return new EnhancementCompanionAuthStorageSmokeSnapshot(
            createAndStableReread,
            malformedRejected,
            foreignFileRejected,
            oversizedRejected,
            foreignDirectoryRejected,
            injectedFailureRejected,
            applicationJunctionRejected,
            authJunctionRejected,
            directoryRenameContained,
            leaseAcquired,
            moveBlocked,
            moveSucceeded,
            renameDetected);
    }
    public static EnhancementCompanionAuthAclSmokeSnapshot
        EnhancementCompanionAuthAclContractForSmoke(string fixtureRoot)
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new System.Security.SecurityException(
                "The current Windows user identity is unavailable.");
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier foreign = new(WellKnownSidType.WorldSid, null);
        FileSecurity Build(SecurityIdentifier owner, bool foreignAllow)
        {
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                localSystem,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            if (foreignAllow)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    foreign,
                    FileSystemRights.ReadData,
                    AccessControlType.Allow));
            }
            return security;
        }
        EnhancementCompanionAuthStoragePath pathCapability =
            EnhancementCompanionAuthStoragePath.ForManagedTempFixtureRoot(
                fixtureRoot);
        string fullFixtureRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(fixtureRoot));
        string expectedDirectory = Path.Combine(
            fullFixtureRoot,
            "PhotoViewer.Wpf",
            "companion-auth-v1");
        string expectedFile = Path.Combine(
            expectedDirectory,
            "companion-auth-v1.key");
        string outsideTemp = Path.GetPathRoot(fullFixtureRoot)
            ?? throw new InvalidDataException(
                "The companion authentication smoke root had no volume root.");
        return new EnhancementCompanionAuthAclSmokeSnapshot(
            HasCurrentUserOnlyAuthAcl(Build(currentUser, foreignAllow: false), currentUser),
            !HasCurrentUserOnlyAuthAcl(Build(foreign, foreignAllow: false), currentUser),
            !HasCurrentUserOnlyAuthAcl(Build(currentUser, foreignAllow: true), currentUser),
            string.Equals(
                pathCapability.DirectoryPath,
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    pathCapability.FilePath,
                    expectedFile,
                    StringComparison.OrdinalIgnoreCase),
            !EnhancementCompanionAuthStoragePath
                .AcceptsManagedTempFixtureRootForSmoke("relative-auth-root"),
            !EnhancementCompanionAuthStoragePath
                .AcceptsManagedTempFixtureRootForSmoke(outsideTemp),
            EnhancementCompanionAuthStoragePath
                .RejectsUnavailableProductRootsForSmoke());
    }
}

public sealed record EnhancementCompanionSecureRequestSmokeSnapshot(
    string Method,
    string PathAndQuery,
    string? BodyJson,
    string? IdempotencyKey,
    string OuterEnvelopeJson);

public sealed record EnhancementCompanionAuthAclSmokeSnapshot(
    bool CanonicalCurrentUserAclAccepted,
    bool ForeignOwnerRejected,
    bool ForeignAllowRejected,
    bool FixedCapabilityShape,
    bool RelativeFixtureRootRejected,
    bool OutsideTempFixtureRootRejected,
    bool UnavailableProductRootRejected);

public sealed record EnhancementCompanionAuthStorageSmokeSnapshot(
    bool CreateAndStableReread,
    bool MalformedPreservedAndRejected,
    bool ForeignFileAclPreservedAndRejected,
    bool OversizedPreservedAndRejected,
    bool ForeignDirectoryAclPreservedAndRejected,
    bool FailedCreateExactHandleRemoved,
    bool ApplicationDirectoryJunctionRejectedOutsidePreserved,
    bool AuthDirectoryJunctionRejectedOutsidePreserved,
    bool DirectoryRenameContained,
    bool LeaseAcquired,
    bool LeaseMoveBlocked,
    bool LeaseMoveSucceeded,
    bool LeaseRenameDetected);

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
    bool HasInheritedAuthenticationToken,
    bool HasInheritedInstanceId,
    string? NoOpen,
    string? ComfyAutostart,
    bool DefersQueueRecovery,
    string LauncherFileName);
