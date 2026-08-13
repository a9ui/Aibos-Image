using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
    private const string EnhancementCompanionAuthProtocol =
        "aibos.companion-auth/v1";
    private const string EnhancementCompanionRequestAuthProtocol =
        "aibos.companion-request/v1";
    private const string EnhancementCompanionIdentityRoute =
        "api/enhance/identity";
    private const string EnhancementCompanionAuthFileName =
        "companion-auth-v1.key";
    private const string EnhancementCompanionChallengeHeader =
        "X-Aibos-Companion-Challenge";
    private const string EnhancementCompanionTimestampHeader =
        "X-Aibos-Auth-Timestamp";
    private const string EnhancementCompanionNonceHeader =
        "X-Aibos-Auth-Nonce";
    private const string EnhancementCompanionSignatureHeader =
        "X-Aibos-Auth-Signature";
    private const int EnhancementCompanionIdentityResponseMaxBytes = 4096;
    private static readonly string EnhancementCompanionSmokeAuthToken =
        Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(
            "aibos-companion-smoke-token-v1")));

    private readonly SemaphoreSlim _enhancementCompanionLaunchGate = new(1, 1);
    private readonly CancellationTokenSource _enhancementCompanionLifetimeCts = new();
    private Process? _ownedEnhancementCompanion;
    private string? _enhancementCompanionLaunchError;
    private int _enhancementCompanionLaunchAttemptCount;
    private Func<Uri, (bool Started, string Error)>? _startEnhancementCompanionForSmoke;
    private string? _enhancementCompanionAuthToken;
    private string? _ownedEnhancementCompanionInstanceId;
    private bool _enhancementCompanionOwnershipVerified;
    private sealed record EnhancementEnqueueProbe(
        EnhancementEnqueueBackendMode Mode,
        JsonElement? HealthPayload,
        long ActionDeadlineTick);
    private sealed record DurableEnhancementBatchResponse(
        EnhancementApiResponse[] Responses,
        int NudgeCount,
        int PublishedCount);
    private sealed record EnhancementCompanionOwnershipProbe(
        bool Verified,
        bool TransportUnavailable,
        int StatusCode,
        JsonElement? Payload,
        string Error);

    private async Task<EnhancementApiResponse> EnsureEnhancementCompanionReadyForExplicitActionAsync(
        string? sourceIdentity = null,
        CancellationToken token = default)
    {
        _ = sourceIdentity;
        const string readinessRoute = "api/enhance/jobs";
        if (!_usingDefaultModalEnhancementSender)
        {
            return await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: token);
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
            return await SendEnhancementApiAsync(
                HttpMethod.Get,
                readinessRoute,
                token: token);
        }
        _enhancementCompanionOwnershipVerified = false;
        if (!ownership.TransportUnavailable)
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
                return await SendEnhancementApiAsync(
                    HttpMethod.Get,
                    readinessRoute,
                    token: linkedToken);
            }
            if (!ownership.TransportUnavailable)
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
                else if (!ownership.TransportUnavailable)
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

    private static Func<JsonElement, string?> CreateMiniMaxH3VideoHealthValidator()
        => payload => !TryParseMiniMaxH3VideoCapability(payload, out _)
            ? "The Aibos Image local AI service cannot prove the exact MiniMax H3 protocol. No job was added."
            : !TryParseMiniMaxH3VideoProfilesCapability(payload)
                ? "The Aibos Image local AI service does not expose the tested MiniMax H3 5, 10, 12, and 15 second profiles. Restart the local AI service first; no job was added."
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
        Func<string?>? prePublishValidator = null)
    {
        if (_usingDefaultModalEnhancementSender)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionReadyForExplicitActionAsync(
                    recoverySourceIdentity,
                    token);
            if (!readiness.Ok)
                return readiness;
        }

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
            string? prePublishError = prePublishValidator?.Invoke();
            if (!string.IsNullOrWhiteSpace(prePublishError))
            {
                return new EnhancementApiResponse(
                    false,
                    409,
                    null,
                    prePublishError);
            }
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
        if (_usingDefaultModalEnhancementSender)
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionReadyForExplicitActionAsync(
                    token: token);
            if (!readiness.Ok)
            {
                EnhancementApiResponse rejected = new(
                    false,
                    readiness.StatusCode,
                    readiness.Payload,
                    readiness.Error);
                return new DurableEnhancementBatchResponse(
                    Enumerable.Repeat(rejected, bodies.Count).ToArray(),
                    0,
                    0);
            }
        }

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

    private static string EnhancementCompanionAuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoViewer.Wpf",
        EnhancementCompanionAuthFileName);

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

        string path = EnhancementCompanionAuthPath;
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                error = "The local AI companion authentication directory is not trusted.";
                return false;
            }

            if (!File.Exists(path))
            {
                string generated = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough);
                    byte[] bytes = Encoding.ASCII.GetBytes(generated);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                    ApplyCurrentUserOnlyAuthFileAcl(path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another Aibos window won the CreateNew race. Read the
                    // completed user-only file below.
                }
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !HasCurrentUserOnlyAuthFileAcl(path))
            {
                error = "The local AI companion authentication file is not user-only.";
                return false;
            }
            string candidate = File.ReadAllText(path, Encoding.ASCII).Trim();
            if (!IsValidEnhancementCompanionAuthToken(candidate))
            {
                error = "The local AI companion authentication file is invalid.";
                return false;
            }
            _enhancementCompanionAuthToken = candidate;
            authToken = candidate;
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            error = $"Aibos could not establish user-only local AI authentication: {ex.Message}";
            return false;
        }
    }

    private static void ApplyCurrentUserOnlyAuthFileAcl(string path)
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
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool HasCurrentUserOnlyAuthFileAcl(string path)
    {
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
            return false;
        SecurityIdentifier localSystem = new(
            WellKnownSidType.LocalSystemSid,
            null);
        AuthorizationRuleCollection rules = new FileInfo(path)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference.Equals(currentUser)
                || rule.IdentityReference.Equals(localSystem))
            {
                continue;
            }
            if ((rule.FileSystemRights & (
                    FileSystemRights.ReadData
                    | FileSystemRights.ReadAttributes
                    | FileSystemRights.ReadExtendedAttributes
                    | FileSystemRights.ReadPermissions
                    | FileSystemRights.FullControl)) != 0)
            {
                return false;
            }
        }
        return true;
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
                    statusCode,
                    payload,
                    "The local AI service port is occupied by an untrusted process. No source, prompt, secret, job body, or durable reservation was sent.");
            }
            return new(true, false, statusCode, payload, "");
        }
        catch (HttpRequestException)
        {
            return new(
                false,
                true,
                0,
                null,
                "No local AI companion is listening yet.");
        }
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

    private bool TryAddEnhancementCompanionRequestAuthentication(
        HttpRequestMessage request,
        ReadOnlySpan<byte> bodyBytes)
    {
        if (!_usingDefaultModalEnhancementSender)
            return true;
        if (!_enhancementCompanionOwnershipVerified
            || !IsValidEnhancementCompanionAuthToken(
                _enhancementCompanionAuthToken)
            || request.RequestUri is null
            || !TryBase64UrlDecode(
                _enhancementCompanionAuthToken!,
                out byte[] authTokenBytes))
        {
            return false;
        }

        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        string nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        string bodyHash = Base64UrlEncode(SHA256.HashData(bodyBytes));
        string requestPath = request.RequestUri.PathAndQuery;
        string message = $"{EnhancementCompanionRequestAuthProtocol}\0{timestamp}\0{nonce}\0{request.Method.Method.ToUpperInvariant()}\0{requestPath}\0{bodyHash}";
        using var hmac = new HMACSHA256(authTokenBytes);
        string signature = Base64UrlEncode(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionTimestampHeader,
            timestamp);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionNonceHeader,
            nonce);
        request.Headers.TryAddWithoutValidation(
            EnhancementCompanionSignatureHeader,
            signature);
        return true;
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
            && payload.TryGetProperty("jobs", out JsonElement jobs)
            && jobs.ValueKind == JsonValueKind.Array;

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
            error = "Aibos could not find the local AI service beside this portable build. Open a build with the local AI service available, or set the compatibility variable AIBOS_H25_COMPANION_ROOT.";
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
        _enhancementCompanionOwnershipVerified = false;
        _ownedEnhancementCompanionInstanceId = null;
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
        _usingDefaultModalEnhancementSender = true;
        _startEnhancementCompanionForSmoke = starter;
        _enhancementCompanionAuthToken = EnhancementCompanionSmokeAuthToken;
        _enhancementCompanionOwnershipVerified = false;
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
            && request.Headers.Authorization is null;

    public async Task<bool> EnsureEnhancementCompanionForExplicitActionForSmokeAsync()
    {
        EnhancementApiResponse response = await EnsureEnhancementCompanionReadyForExplicitActionAsync();
        return response.Ok;
    }
    public async Task<bool> SendEnhancementEnqueueForSmokeAsync(object body)
    {
        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(body);
        return response.SavedForDelivery;
    }
    public async Task<int> SendEnhancementBatchForSmokeAsync(
        IReadOnlyList<object?> bodies)
    {
        DurableEnhancementBatchResponse response =
            await TrySendDurableEnhancementBatchAsync(bodies);
        return response.PublishedCount;
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
    bool HasInheritedAuthenticationToken,
    bool HasInheritedInstanceId,
    string? NoOpen,
    string? ComfyAutostart,
    string LauncherFileName);
