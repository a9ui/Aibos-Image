using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string VideoH3PromptRewriteRevision =
        "aibos-h3-i2va-local-v1";
    private const int VideoH3PromptRewriteTimeoutMilliseconds = 360_000;
    private const int MaxVideoH3PromptRewriteResponseBytes = 32 * 1024;
    // Keep one over-limit code unit so an editor paste is rejected as
    // oversized instead of being silently clipped into a valid 2,000-unit
    // generation prompt.
    private const int MaxVideoH3CandidateEditorLength =
        MaxVideoPromptLength + 1;
    private const string VideoH3PromptOpening =
        "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.";
    private const string VideoH3IntegratedPrefix =
        "\n\nintegrated_multimodal_description: [Shot 1] ";
    private const string VideoH3SoundscapePrefix =
        "\n\noverall_soundscape: ";
    private const string VideoH3MusicPrefix =
        "\n\nnon_diegetic_music: ";
    private const string VideoH3IntegratedMarker =
        "integrated_multimodal_description:";
    private const string VideoH3SoundscapeMarker = "overall_soundscape:";
    private const string VideoH3MusicMarker = "non_diegetic_music:";
    private const string VideoH3DirectionRewriteInstruction =
        "Creative direction: preserve the user's intent, then strengthen image-compatible subject motion, expression, timing, and camera movement without inventing new people, objects, touch, or cuts.";
    private const string VideoH3AutoRewriteInstruction =
        "Auto direction: preserve the user's intent and let the source image determine the most compatible visible motion, expression, timing, and camera movement without inventing new people, objects, touch, or cuts.";

    private enum VideoH3PromptRewriteMode
    {
        Polish,
        Direction,
        Auto,
    }

    private string _videoH3PromptCandidate = "";
    private string? _videoH3CandidateBasePrompt;
    private VideoH3SourceStamp? _videoH3CandidateSourceStamp;
    private string? _videoH3CandidateStyleName;
    private VideoH3PromptRewriteMode? _videoH3CandidateMode;
    private string? _videoH3CandidateRewriteRevision;
    private string? _videoH3CandidateSourceSha256;
    private string? _videoPromptBeforeH3Apply;
    private string? _videoPromptAfterH3Apply;
    private bool _videoH3RewritePending;
    private bool _syncingVideoH3PromptCandidate;
    private bool _changingVideoPromptForH3History;
    private long _videoH3RewriteGeneration;
    private long _videoH3RewriteContextRevision;
    private long _videoH3CandidateContextRevision;
    private CancellationTokenSource? _videoH3RewriteCts;
    private VideoH3PromptRewriteMode _videoH3RewriteMode =
        VideoH3PromptRewriteMode.Polish;

    private readonly record struct VideoH3SourceStamp(
        string SourceIdentity,
        string DisplayPath,
        string? ProducerJobId,
        long Length,
        long LastWriteUtcTicks);

    private async void RewriteVideoPromptForH3_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoH3RewritePending)
        {
            CancelVideoH3PromptRewrite(userInitiated: true);
            return;
        }

        await RewriteVideoPromptForH3Async();
    }

    private void VideoH3RewriteMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        VideoH3PromptRewriteMode selected =
            ParseVideoH3PromptRewriteMode(
                (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        if (_videoH3RewriteMode == selected)
            return;

        _videoH3RewriteMode = selected;
        VideoH3PromptRewriteContextChanged();
    }

    private static VideoH3PromptRewriteMode ParseVideoH3PromptRewriteMode(
        string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "direction" => VideoH3PromptRewriteMode.Direction,
            "auto" => VideoH3PromptRewriteMode.Auto,
            _ => VideoH3PromptRewriteMode.Polish,
        };

    private static string VideoH3PromptRewriteModeId(
        VideoH3PromptRewriteMode mode)
        => mode switch
        {
            VideoH3PromptRewriteMode.Direction => "direction",
            VideoH3PromptRewriteMode.Auto => "auto",
            _ => "polish",
        };

    private static bool TryBuildVideoH3RewriteRequestPrompt(
        string inputPrompt,
        VideoH3PromptRewriteMode mode,
        out string requestPrompt)
    {
        string? instruction = mode switch
        {
            VideoH3PromptRewriteMode.Direction =>
                VideoH3DirectionRewriteInstruction,
            VideoH3PromptRewriteMode.Auto => VideoH3AutoRewriteInstruction,
            _ => null,
        };
        if (instruction is null)
        {
            requestPrompt = inputPrompt;
            return requestPrompt.Length <= MaxVideoPromptLength;
        }

        string separator = string.IsNullOrWhiteSpace(inputPrompt)
            ? ""
            : "\n\n";
        string directedPrompt = inputPrompt + separator + instruction;
        if (directedPrompt.Length > MaxVideoPromptLength)
        {
            requestPrompt = "";
            return false;
        }

        requestPrompt = directedPrompt;
        return true;
    }

    private async Task<bool> RewriteVideoPromptForH3Async()
    {
        if (_videoH3RewritePending || !IsMiniMaxH3VideoModel(_videoModelId))
            return false;

        var operationWatch = Stopwatch.StartNew();
        string operationMode = VideoH3PromptRewriteModeId(_videoH3RewriteMode);
        int operationDurationSeconds = NormalizeMiniMaxH3DurationSeconds(
            _videoDurationSeconds);

        if (!TryCaptureVideoH3RewriteSourceStamp(
                out VideoSourceChoice source,
                out VideoH3SourceStamp sourceStamp,
                out string sourceError))
        {
            SetVideoH3PromptRewriteStatus(sourceError);
            RefreshVideoH3PromptRewriteControls(updateStatus: false);
            AibosOperationLog.Write(
                "h3_prompt_rewrite",
                "rejected",
                operationWatch.ElapsedMilliseconds,
                errorCode: "SOURCE_UNAVAILABLE",
                mode: operationMode,
                durationSeconds: operationDurationSeconds);
            return false;
        }

        string basePrompt = _videoPrompt;
        string? baseStyleName = _selectedVideoStyleName;
        VideoH3PromptRewriteMode baseMode = _videoH3RewriteMode;
        long baseContextRevision = _videoH3RewriteContextRevision;
        if (!TryBuildVideoH3RewriteRequestPrompt(
                basePrompt,
                baseMode,
                out string requestPrompt))
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusInputTooLong",
                "選んだ変換方法の指示を含めると2000文字を超えます。入力を少し短くしてください。動画ジョブやworkerは変更していません。"));
            RefreshVideoH3PromptRewriteControls(updateStatus: false);
            AibosOperationLog.Write(
                "h3_prompt_rewrite",
                "rejected",
                operationWatch.ElapsedMilliseconds,
                errorCode: "INPUT_TOO_LONG",
                mode: operationMode,
                durationSeconds: operationDurationSeconds);
            return false;
        }
        AibosOperationLog.Write(
            "h3_prompt_rewrite",
            "started",
            mode: operationMode,
            durationSeconds: operationDurationSeconds);
        string operationOutcome = "failed";
        string? operationErrorCode = "UNKNOWN_FAILURE";
        int? operationStatusCode = null;
        double? operationInferenceMilliseconds = null;
        long generation = ++_videoH3RewriteGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoH3RewriteCts,
            cts);
        prior?.Cancel();
        prior?.Dispose();
        _videoH3RewritePending = true;
        void SetStatusIfCurrent(string message)
        {
            if (generation == _videoH3RewriteGeneration)
                SetVideoH3PromptRewriteStatus(message);
        }
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        SetStatusIfCurrent(VideoH3Localized(
            "UiVideoH3StatusWorking",
            "画像と入力を確認し、H3向け候補を作成しています…"));

        try
        {
            string expectedSourceSha256 = await ComputeVideoH3SourceSha256Async(
                sourceStamp.DisplayPath,
                cts.Token);
            if (!IsVideoH3RewriteContextCurrent(
                    basePrompt,
                    sourceStamp,
                    baseStyleName,
                    baseMode,
                    baseContextRevision))
            {
                SetStatusIfCurrent(VideoH3Localized(
                    "UiVideoH3StatusStaleResponse",
                    "作成中に入力・画像・Model・Styleが変わったため、結果を採用しませんでした。"));
                operationErrorCode = "CONTEXT_CHANGED";
                return false;
            }

            var requestBody = new Dictionary<string, object?>
            {
                ["sourceId"] = source.SourceIdentity,
                ["prompt"] = requestPrompt,
                ["frameCount"] = MiniMaxH3FrameCountForDuration(
                    _videoDurationSeconds),
                ["playbackFps"] = MiniMaxH3VideoPlaybackFps,
            };
            if (!string.IsNullOrWhiteSpace(source.ProducerJobId))
                requestBody["sourceProducerJobId"] = source.ProducerJobId;

            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/video-prompts/h3/rewrite",
                requestBody,
                cts.Token,
                timeoutMilliseconds: VideoH3PromptRewriteTimeoutMilliseconds,
                maxResponseBytes: MaxVideoH3PromptRewriteResponseBytes,
                timeoutError: VideoH3Localized(
                    "UiVideoH3StatusTimedOut",
                    "H3語化が6分以内に完了しませんでした。入力プロンプトは変更していません。もう一度試してください。"));
            operationStatusCode = response.StatusCode;
            if (response.Payload is JsonElement responsePayload
                && responsePayload.TryGetProperty(
                    "inferenceMilliseconds",
                    out JsonElement inferenceElement)
                && inferenceElement.TryGetDouble(out double inferenceMilliseconds))
            {
                operationInferenceMilliseconds = inferenceMilliseconds;
            }
            if (cts.IsCancellationRequested)
            {
                SetStatusIfCurrent(VideoH3Localized(
                    "UiVideoH3StatusCanceled",
                    "H3語化を中止しました。動画ジョブは追加していません。"));
                operationOutcome = "canceled";
                operationErrorCode = "USER_CANCELED";
                return false;
            }
            if (!response.Ok || response.Payload is not JsonElement payload)
            {
                SetStatusIfCurrent(DescribeVideoH3PromptRewriteFailure(response));
                operationErrorCode = response.Payload is JsonElement errorPayload
                    && TryGetStringProperty(
                        errorPayload,
                        "code",
                        out string? responseCode)
                        ? responseCode ?? "API_ERROR"
                        : response.StatusCode == 0
                            ? "COMPANION_UNAVAILABLE"
                            : "API_ERROR";
                return false;
            }
            bool parsed = TryParseVideoH3PromptRewriteResponse(
                    payload,
                    out string candidate,
                    out string rewriteRevision,
                    out string sourceSha256);
            if (!parsed
                || !string.Equals(
                    sourceSha256,
                    expectedSourceSha256,
                    StringComparison.Ordinal))
            {
                SetStatusIfCurrent(VideoH3Localized(
                    "UiVideoH3StatusInvalidResponse",
                    "H3語化の応答を確認できません。入力プロンプトは変更していません。"));
                operationErrorCode = parsed
                    ? "SOURCE_HASH_MISMATCH"
                    : "RESPONSE_CONTRACT_INVALID";
                return false;
            }
            if (generation != _videoH3RewriteGeneration
                || !IsVideoH3RewriteContextCurrent(
                    basePrompt,
                    sourceStamp,
                    baseStyleName,
                    baseMode,
                    baseContextRevision))
            {
                SetStatusIfCurrent(VideoH3Localized(
                    "UiVideoH3StatusStaleResponse",
                    "作成中に入力・画像・Model・Styleが変わったため、結果を採用しませんでした。"));
                operationErrorCode = "CONTEXT_CHANGED";
                return false;
            }

            _videoH3PromptCandidate = candidate;
            _videoH3CandidateBasePrompt = basePrompt;
            _videoH3CandidateSourceStamp = sourceStamp;
            _videoH3CandidateStyleName = baseStyleName;
            _videoH3CandidateMode = baseMode;
            _videoH3CandidateContextRevision = baseContextRevision;
            _videoH3CandidateRewriteRevision = rewriteRevision;
            _videoH3CandidateSourceSha256 = sourceSha256;
            RefreshVideoH3PromptRewriteControls(updateStatus: false);
            SetStatusIfCurrent(VideoH3Localized(
                "UiVideoH3StatusReady",
                "候補を編集できます。動画生成にはまだ使われていません。"));
            operationOutcome = "completed";
            operationErrorCode = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatusIfCurrent(VideoH3Localized(
                "UiVideoH3StatusCanceled",
                "H3語化を中止しました。動画ジョブは追加していません。"));
            operationOutcome = "canceled";
            operationErrorCode = "CANCELED";
            return false;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            SetStatusIfCurrent(VideoH3Localized(
                "UiVideoH3StatusSourceUnavailable",
                "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。"));
            operationErrorCode = "SOURCE_READ_FAILED";
            return false;
        }
        finally
        {
            AibosOperationLog.Write(
                "h3_prompt_rewrite",
                operationOutcome,
                operationWatch.ElapsedMilliseconds,
                operationStatusCode,
                operationErrorCode,
                operationMode,
                operationDurationSeconds,
                operationInferenceMilliseconds);
            if (generation == _videoH3RewriteGeneration)
            {
                _videoH3RewritePending = false;
                if (ReferenceEquals(_videoH3RewriteCts, cts))
                    _videoH3RewriteCts = null;
                RefreshVideoH3PromptRewriteControls(updateStatus: false);
            }
            cts.Dispose();
        }
    }

    private void VideoH3PromptCandidate_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_syncingVideoH3PromptCandidate || sender is not TextBox source)
            return;

        _videoH3PromptCandidate = source.Text;
        RefreshVideoH3PromptRewriteControls();
    }

    private void ApplyVideoH3PromptCandidate_Click(
        object sender,
        RoutedEventArgs e)
        => ApplyVideoH3PromptCandidate();

    private bool ApplyVideoH3PromptCandidate()
    {
        if (!CanApplyVideoH3PromptCandidate()
            || !TryNormalizeAndValidateVideoH3Prompt(
                _videoH3PromptCandidate,
                out string normalizedCandidate))
        {
            RefreshVideoH3PromptRewriteControls();
            return false;
        }

        string before = _videoPrompt;
        string after = normalizedCandidate;
        _videoH3PromptCandidate = normalizedCandidate;
        _videoPromptBeforeH3Apply = before;
        _videoPromptAfterH3Apply = after;
        _changingVideoPromptForH3History = true;
        try
        {
            ModalVideoPromptTextBox.Text = after;
            ModalVideoPromptTextBox.CaretIndex = ModalVideoPromptTextBox.Text.Length;
            ModalVideoPromptTextBox.ScrollToEnd();
            Keyboard.Focus(ModalVideoPromptTextBox);
        }
        finally
        {
            _changingVideoPromptForH3History = false;
        }

        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        SetVideoH3PromptRewriteStatus(VideoH3Localized(
            "UiVideoH3StatusApplied",
            "候補を入力プロンプトへ反映しました。動画化はまだ開始していません。"));
        return true;
    }

    private void UndoAppliedVideoH3Prompt_Click(
        object sender,
        RoutedEventArgs e)
        => UndoAppliedVideoH3Prompt();

    private bool UndoAppliedVideoH3Prompt()
    {
        if (!CanUndoAppliedVideoH3Prompt()
            || _videoPromptBeforeH3Apply is not string before)
        {
            RefreshVideoH3PromptRewriteControls();
            return false;
        }

        _changingVideoPromptForH3History = true;
        try
        {
            ModalVideoPromptTextBox.Text = before;
        }
        finally
        {
            _changingVideoPromptForH3History = false;
        }
        _videoPromptBeforeH3Apply = null;
        _videoPromptAfterH3Apply = null;
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        SetVideoH3PromptRewriteStatus(VideoH3Localized(
            "UiVideoH3StatusUndone",
            "入力プロンプトを反映前へ戻しました。"));
        return true;
    }

    private void VideoH3PromptRewriteContextChanged(bool cancelPending = true)
    {
        _videoH3RewriteContextRevision++;
        if (!_changingVideoPromptForH3History)
        {
            _videoPromptBeforeH3Apply = null;
            _videoPromptAfterH3Apply = null;
        }
        if (cancelPending && _videoH3RewritePending)
            CancelVideoH3PromptRewrite();
        RefreshVideoH3PromptRewriteControls();
    }

    private void InvalidateVideoH3PromptUndoAfterManualEdit()
    {
        if (_changingVideoPromptForH3History
            || _videoPromptAfterH3Apply is null
            || string.Equals(
                _videoPrompt,
                _videoPromptAfterH3Apply,
                StringComparison.Ordinal))
        {
            return;
        }

        _videoPromptBeforeH3Apply = null;
        _videoPromptAfterH3Apply = null;
    }

    private void CancelVideoH3PromptRewrite(bool userInitiated = false)
    {
        if (!_videoH3RewritePending)
            return;

        _videoH3RewriteGeneration++;
        CancellationTokenSource? active = _videoH3RewriteCts;
        _videoH3RewriteCts = null;
        _videoH3RewritePending = false;
        active?.Cancel();
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        if (userInitiated)
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusCanceled",
                "H3語化を中止しました。候補・入力・動画ジョブは変更していません。"));
        }
    }

    private void RefreshVideoH3PromptRewriteControls(bool updateStatus = true)
    {
        if (ModalVideoH3PromptRewritePanel is null
            || ModalVideoH3RewritePromptButton is null
            || ModalVideoH3RewriteModeComboBox is null
            || ModalVideoH3PromptCandidateTextBox is null
            || ModalVideoH3ApplyPromptButton is null
            || ModalVideoH3UndoPromptButton is null)
        {
            return;
        }

        bool h3Selected = IsMiniMaxH3VideoModel(_videoModelId);
        ModalVideoH3PromptRewritePanel.Visibility = h3Selected
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.Equals(
                ModalVideoH3PromptCandidateTextBox.Text,
                _videoH3PromptCandidate,
                StringComparison.Ordinal))
        {
            _syncingVideoH3PromptCandidate = true;
            try
            {
                ModalVideoH3PromptCandidateTextBox.Text =
                    _videoH3PromptCandidate;
            }
            finally
            {
                _syncingVideoH3PromptCandidate = false;
            }
        }

        bool sourceReady = false;
        string sourceError = "";
        if (h3Selected)
        {
            sourceReady = TryCaptureVideoH3SourceStamp(
                out _,
                out _,
                out sourceError);
        }
        ModalVideoH3RewritePromptButton.IsEnabled = h3Selected
            && (_videoH3RewritePending || sourceReady);
        ModalVideoH3RewritePromptButton.Content = _videoH3RewritePending
            ? VideoH3Localized(
                "UiVideoH3RewriteCancelButton",
                "キャンセル")
            : VideoH3Localized(
                string.IsNullOrEmpty(_videoH3PromptCandidate)
                    ? "UiVideoH3RewriteButton"
                    : "UiVideoH3RewriteAgainButton",
                string.IsNullOrEmpty(_videoH3PromptCandidate)
                    ? "MiniMax語に変換"
                    : "もう一度変換");
        string rewriteButtonAutomation = _videoH3RewritePending
            ? VideoH3Localized(
                "UiVideoH3RewriteCancelButtonAutomation",
                "MiniMax H3語変換をキャンセル")
            : VideoH3Localized(
                "UiVideoH3RewriteButtonAutomation",
                "画像と入力からMiniMax H3向け候補を作成");
        string rewriteButtonHelp = _videoH3RewritePending
            ? VideoH3Localized(
                "UiVideoH3RewriteCancelButtonHelp",
                "待機中のHTTP要求をすぐ中止します。候補・入力・動画ジョブは変更しません。")
            : VideoH3Localized(
                "UiVideoH3RewriteButtonHelp",
                "起動済みのlocal Qwen変換器を使います。worker起動や動画ジョブ追加はしません。");
        AutomationProperties.SetName(
            ModalVideoH3RewritePromptButton,
            rewriteButtonAutomation);
        AutomationProperties.SetHelpText(
            ModalVideoH3RewritePromptButton,
            rewriteButtonHelp);
        ModalVideoH3RewritePromptButton.ToolTip = rewriteButtonHelp;
        ModalVideoH3RewriteModeComboBox.IsEnabled = h3Selected
            && !_videoH3RewritePending;
        ModalVideoH3PromptCandidateTextBox.IsEnabled = h3Selected
            && !_videoH3RewritePending
            && !string.IsNullOrEmpty(_videoH3PromptCandidate);
        ModalVideoH3ApplyPromptButton.IsEnabled =
            CanApplyVideoH3PromptCandidate();
        ModalVideoH3UndoPromptButton.IsEnabled = CanUndoAppliedVideoH3Prompt();

        if (!updateStatus || !h3Selected)
            return;
        if (_videoH3RewritePending)
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusWorking",
                "画像と入力を確認し、H3向け候補を作成しています…"));
        }
        else if (!sourceReady)
        {
            SetVideoH3PromptRewriteStatus(string.IsNullOrWhiteSpace(sourceError)
                ? VideoH3Localized(
                    "UiVideoH3StatusSourceUnavailable",
                    "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。")
                : sourceError);
        }
        else if (string.IsNullOrEmpty(_videoH3PromptCandidate))
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusIdle",
                "画像と入力から候補を作ります。ここでは動画ジョブを追加しません。"));
        }
        else if (!IsVideoH3PromptCandidateFresh())
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusStale",
                "入力・画像・Model・Styleが変わりました。表示中の候補は反映できますが、必要ならもう一度H3語化してください。"));
        }
        else if (!TryNormalizeAndValidateVideoH3Prompt(
                     _videoH3PromptCandidate,
                     out _))
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusInvalidCandidate",
                "候補はH3形式ではないか、2000文字を超えています。入力には反映していません。"));
        }
        else
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusReady",
                "候補を編集できます。動画生成にはまだ使われていません。"));
        }
    }

    private bool CanApplyVideoH3PromptCandidate()
    {
        if (!IsMiniMaxH3VideoModel(_videoModelId)
            || _videoH3RewritePending
            || !TryNormalizeAndValidateVideoH3Prompt(
                _videoH3PromptCandidate,
                out string normalizedCandidate))
        {
            return false;
        }

        return !string.Equals(
            _videoPrompt,
            normalizedCandidate,
            StringComparison.Ordinal);
    }

    private bool CanUndoAppliedVideoH3Prompt()
        => !_videoH3RewritePending
            && _videoPromptBeforeH3Apply is not null
            && _videoPromptAfterH3Apply is not null
            && string.Equals(
                _videoPrompt,
                _videoPromptAfterH3Apply,
                StringComparison.Ordinal);

    private bool IsVideoH3PromptCandidateFresh()
        => _videoH3CandidateSourceStamp is VideoH3SourceStamp stamp
            && string.Equals(
                _videoH3CandidateBasePrompt,
                _videoPrompt,
                StringComparison.Ordinal)
            && string.Equals(
                _videoH3CandidateStyleName,
                _selectedVideoStyleName,
                StringComparison.Ordinal)
            && _videoH3CandidateMode == _videoH3RewriteMode
            && string.Equals(
                _videoH3CandidateRewriteRevision,
                VideoH3PromptRewriteRevision,
                StringComparison.Ordinal)
            && _videoH3CandidateContextRevision
                == _videoH3RewriteContextRevision
            && IsValidSha256(_videoH3CandidateSourceSha256)
            && IsVideoH3RewriteContextCurrent(
                _videoPrompt,
                stamp,
                _selectedVideoStyleName,
                _videoH3RewriteMode,
                _videoH3RewriteContextRevision);

    private bool IsVideoH3RewriteContextCurrent(
        string basePrompt,
        VideoH3SourceStamp sourceStamp,
        string? baseStyleName,
        VideoH3PromptRewriteMode baseMode,
        long baseContextRevision)
    {
        if (!IsMiniMaxH3VideoModel(_videoModelId)
            || _videoH3RewriteContextRevision != baseContextRevision
            || !string.Equals(_videoPrompt, basePrompt, StringComparison.Ordinal)
            || _videoH3RewriteMode != baseMode
            || !string.Equals(
                _selectedVideoStyleName,
                baseStyleName,
                StringComparison.Ordinal)
            || !TryCaptureVideoH3SourceStamp(
                out _,
                out VideoH3SourceStamp current,
                out _))
        {
            return false;
        }

        return VideoH3SourceStampsEqual(sourceStamp, current);
    }

    private bool TryCaptureVideoH3RewriteSourceStamp(
        out VideoSourceChoice source,
        out VideoH3SourceStamp stamp,
        out string error)
    {
        if (_videoSourceChoice is null)
        {
            if (!TryGetVideoGenerationSourceTile(out Tile tile))
            {
                source = null!;
                stamp = default;
                error = VideoH3Localized(
                    "UiVideoH3StatusSourceUnavailable",
                    "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。");
                return false;
            }
            if (!TryCaptureVideoSource(
                    tile,
                    requestedSource: null,
                    out VideoSourceChoice recovered,
                    out error))
            {
                source = null!;
                stamp = default;
                return false;
            }
            _videoSourceChoice = recovered;
        }

        return TryCaptureVideoH3SourceStamp(out source, out stamp, out error);
    }

    private bool TryCaptureVideoH3SourceStamp(
        out VideoSourceChoice source,
        out VideoH3SourceStamp stamp,
        out string error)
    {
        stamp = default;
        if (!TryRevalidateCapturedVideoSource(out source, out error))
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                error = VideoH3Localized(
                    "UiVideoH3StatusSourceUnavailable",
                    "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。");
            }
            return false;
        }

        try
        {
            string displayPath = Path.GetFullPath(source.DisplayPath);
            var info = new FileInfo(displayPath);
            if (!info.Exists || info.Length <= 0)
            {
                error = VideoH3Localized(
                    "UiVideoH3StatusSourceUnavailable",
                    "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。");
                return false;
            }

            stamp = new VideoH3SourceStamp(
                source.SourceIdentity,
                displayPath,
                source.ProducerJobId,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            error = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            error = VideoH3Localized(
                "UiVideoH3StatusSourceUnavailable",
                "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。");
            source = null!;
            return false;
        }
    }

    private static bool VideoH3SourceStampsEqual(
        VideoH3SourceStamp left,
        VideoH3SourceStamp right)
        => string.Equals(
                left.SourceIdentity,
                right.SourceIdentity,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.DisplayPath,
                right.DisplayPath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.ProducerJobId,
                right.ProducerJobId,
                StringComparison.Ordinal)
            && left.Length == right.Length
            && left.LastWriteUtcTicks == right.LastWriteUtcTicks;

    private static async Task<string> ComputeVideoH3SourceSha256Async(
        string path,
        CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexStringLower(digest);
    }

    private static bool TryParseVideoH3PromptRewriteResponse(
        JsonElement payload,
        out string candidate,
        out string rewriteRevision,
        out string sourceSha256)
    {
        candidate = "";
        rewriteRevision = "";
        sourceSha256 = "";
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "candidatePrompt",
            "rewriteRevision",
            "sourceSha256",
            "modelId",
            "inferenceMilliseconds",
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }
        if (!seen.Contains("candidatePrompt")
            || !seen.Contains("rewriteRevision")
            || !seen.Contains("sourceSha256")
            || !payload.TryGetProperty(
                "candidatePrompt",
                out JsonElement candidateElement)
            || candidateElement.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty(
                "rewriteRevision",
                out JsonElement revisionElement)
            || revisionElement.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty(
                "sourceSha256",
                out JsonElement shaElement)
            || shaElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        rewriteRevision = revisionElement.GetString() ?? "";
        sourceSha256 = shaElement.GetString() ?? "";
        if (!string.Equals(
                rewriteRevision,
                VideoH3PromptRewriteRevision,
                StringComparison.Ordinal)
            || !IsValidSha256(sourceSha256)
            || !TryNormalizeAndValidateVideoH3Prompt(
                candidateElement.GetString() ?? "",
                out candidate))
        {
            return false;
        }

        if (payload.TryGetProperty("modelId", out JsonElement modelId)
            && (modelId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modelId.GetString())
                || modelId.GetString()!.Length > 128))
        {
            return false;
        }
        if (payload.TryGetProperty(
                "inferenceMilliseconds",
                out JsonElement inference)
            && (!inference.TryGetDouble(out double milliseconds)
                || !double.IsFinite(milliseconds)
                || milliseconds < 0
                || milliseconds > 600_000))
        {
            return false;
        }
        return true;
    }

    private static bool TryNormalizeAndValidateVideoH3Prompt(
        string raw,
        out string normalized)
    {
        normalized = "";
        if (raw.Length is < 1 or > MaxVideoPromptLength)
            return false;

        normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')
            || !normalized.StartsWith(
                VideoH3PromptOpening + VideoH3IntegratedPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        int integratedIndex = VideoH3PromptOpening.Length;
        int soundscapeIndex = normalized.IndexOf(
            VideoH3SoundscapePrefix,
            integratedIndex + VideoH3IntegratedPrefix.Length,
            StringComparison.Ordinal);
        int musicIndex = normalized.IndexOf(
            VideoH3MusicPrefix,
            soundscapeIndex < 0
                ? integratedIndex + VideoH3IntegratedPrefix.Length
                : soundscapeIndex + VideoH3SoundscapePrefix.Length,
            StringComparison.Ordinal);
        int integratedMarkerIndex = normalized.IndexOf(
            VideoH3IntegratedMarker,
            StringComparison.Ordinal);
        int soundscapeMarkerIndex = normalized.IndexOf(
            VideoH3SoundscapeMarker,
            StringComparison.Ordinal);
        int musicMarkerIndex = normalized.IndexOf(
            VideoH3MusicMarker,
            StringComparison.Ordinal);
        if (soundscapeIndex < 0
            || musicIndex < 0
            || integratedMarkerIndex != integratedIndex + 2
            || soundscapeMarkerIndex != soundscapeIndex + 2
            || musicMarkerIndex != musicIndex + 2
            || normalized.LastIndexOf(
                VideoH3IntegratedMarker,
                StringComparison.Ordinal) != integratedMarkerIndex
            || normalized.LastIndexOf(
                VideoH3SoundscapeMarker,
                StringComparison.Ordinal) != soundscapeMarkerIndex
            || normalized.LastIndexOf(
                VideoH3MusicMarker,
                StringComparison.Ordinal) != musicMarkerIndex
            || integratedMarkerIndex >= soundscapeMarkerIndex
            || soundscapeMarkerIndex >= musicMarkerIndex)
        {
            return false;
        }

        string integrated = normalized[
            (integratedIndex + VideoH3IntegratedPrefix.Length)..soundscapeIndex];
        string soundscape = normalized[
            (soundscapeIndex + VideoH3SoundscapePrefix.Length)..musicIndex];
        string music = normalized[(musicIndex + VideoH3MusicPrefix.Length)..];
        return !string.IsNullOrWhiteSpace(integrated)
            && !string.IsNullOrWhiteSpace(soundscape)
            && !string.IsNullOrWhiteSpace(music)
            && !normalized.Any(static character =>
                char.IsControl(character)
                && character is not '\n' and not '\t');
    }

    private static bool IsValidSha256(string? value)
        => value is { Length: 64 }
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    public static VideoH3PromptRewriteTimeoutSmokeSnapshot
        VideoH3PromptRewriteTimeoutContractForSmoke()
        => new(
            ModalEnhancementHttpClient.Timeout
                == System.Threading.Timeout.InfiniteTimeSpan,
            DefaultEnhancementApiTimeoutMilliseconds,
            VideoH3PromptRewriteTimeoutMilliseconds);

    public readonly record struct VideoH3PromptRewriteTimeoutSmokeSnapshot(
        bool SharedTransportTimeoutIsInfinite,
        int DefaultRequestTimeoutMilliseconds,
        int RewriteRequestTimeoutMilliseconds);

    private string VideoH3Localized(string key, string fallback)
        => TryFindResource(key) as string ?? fallback;

    private string DescribeVideoH3PromptRewriteFailure(
        EnhancementApiResponse response)
    {
        string code = response.Payload is JsonElement payload
            && TryGetStringProperty(payload, "code", out string? parsedCode)
            ? parsedCode ?? ""
            : "";
        return code switch
        {
            "H3_PROMPT_REWRITE_BUSY" => VideoH3Localized(
                "UiVideoH3StatusBusy",
                "別のMiniMax語変換を処理中です。完了してからもう一度試してください。"),
            "H3_PROMPT_REWRITE_TIMEOUT" => VideoH3Localized(
                "UiVideoH3StatusTimedOut",
                "H3語化が6分以内に完了しませんでした。入力プロンプトは変更していません。もう一度試してください。"),
            "H3_PROMPT_REWRITE_INVALID_SOURCE" => VideoH3Localized(
                "UiVideoH3StatusSourceUnavailable",
                "表示中の画像ファイルを読み込めません。移動または削除されていないか確認してください。"),
            "H3_PROMPT_REWRITE_INVALID_MODEL_OUTPUT" => VideoH3Localized(
                "UiVideoH3StatusInvalidResponse",
                "H3語化の応答を確認できません。入力プロンプトは変更していません。"),
            "H3_PROMPT_REWRITE_UNAVAILABLE" => VideoH3Localized(
                "UiVideoH3StatusUnavailable",
                "ローカルのH3語化を利用できません。動画ジョブは追加していません。"),
            _ when response.StatusCode == 0 => VideoH3Localized(
                "UiVideoH3StatusUnavailable",
                "ローカルのH3語化を利用できません。動画ジョブは追加していません。"),
            _ when !string.IsNullOrWhiteSpace(response.Error) => response.Error,
            _ => VideoH3Localized(
                "UiVideoH3StatusInvalidResponse",
                "H3語化の応答を確認できません。入力プロンプトは変更していません。"),
        };
    }

    private void SetVideoH3PromptRewriteStatus(string message)
    {
        if (ModalVideoH3PromptRewriteStatusText is not null)
            ModalVideoH3PromptRewriteStatusText.Text = message;
    }

    public Task<bool> RewriteVideoPromptForH3ForSmokeAsync()
        => RewriteVideoPromptForH3Async();

    public bool ApplyVideoH3PromptCandidateForSmoke()
        => ApplyVideoH3PromptCandidate();

    public bool UndoAppliedVideoH3PromptForSmoke()
        => UndoAppliedVideoH3Prompt();

    public void SetAuthoritativeVideoPromptForSmoke(string prompt)
        => ModalVideoPromptTextBox.Text = prompt;

    public void SetVideoH3PromptCandidateForSmoke(string candidate)
        => ModalVideoH3PromptCandidateTextBox.Text = candidate;

    public string AuthoritativeVideoPromptForSmoke => _videoPrompt;
    public string VideoH3PromptCandidateForSmoke => _videoH3PromptCandidate;
    public bool VideoH3PromptCandidateFreshForSmoke =>
        IsVideoH3PromptCandidateFresh();
    public bool VideoH3PromptCandidateApplyEnabledForSmoke =>
        ModalVideoH3ApplyPromptButton.IsEnabled;
    public bool VideoH3PromptCandidateEditableForSmoke =>
        ModalVideoH3PromptCandidateTextBox.IsEnabled;
    public bool VideoH3PromptRewritePendingForSmoke =>
        _videoH3RewritePending;
    public bool VideoH3PromptRewriteButtonEnabledForSmoke =>
        ModalVideoH3RewritePromptButton.IsEnabled;
    public string VideoH3PromptRewriteButtonContentForSmoke =>
        ModalVideoH3RewritePromptButton.Content?.ToString() ?? "";
    public string VideoH3PromptRewriteButtonAutomationForSmoke =>
        AutomationProperties.GetName(ModalVideoH3RewritePromptButton);
    public string VideoH3PromptRewriteButtonHelpForSmoke =>
        AutomationProperties.GetHelpText(ModalVideoH3RewritePromptButton);
    public bool CancelVideoH3PromptRewriteForSmoke()
    {
        if (!_videoH3RewritePending)
            return false;

        ModalVideoH3RewritePromptButton.RaiseEvent(
            new RoutedEventArgs(
                Button.ClickEvent,
                ModalVideoH3RewritePromptButton));
        return !_videoH3RewritePending;
    }
    public bool VideoH3PromptUndoEnabledForSmoke =>
        ModalVideoH3UndoPromptButton.IsEnabled;
    public string VideoH3PromptRewriteStatusForSmoke =>
        ModalVideoH3PromptRewriteStatusText.Text;
    public string VideoH3PromptRewriteModeForSmoke =>
        VideoH3PromptRewriteModeId(_videoH3RewriteMode);

    public void SetVideoH3PromptRewriteModeForSmoke(string mode)
    {
        VideoH3PromptRewriteMode parsed = ParseVideoH3PromptRewriteMode(mode);
        ComboBoxItem? item = ModalVideoH3RewriteModeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                VideoH3PromptRewriteModeId(parsed),
                StringComparison.Ordinal));
        ModalVideoH3RewriteModeComboBox.SelectedItem = item;
    }

    public static bool TryBuildVideoH3RewriteRequestPromptForSmoke(
        string inputPrompt,
        string mode,
        out string requestPrompt)
        => TryBuildVideoH3RewriteRequestPrompt(
            inputPrompt,
            ParseVideoH3PromptRewriteMode(mode),
            out requestPrompt);

    public static bool TryParseVideoH3PromptRewriteResponseForSmoke(
        JsonElement payload,
        out string candidate)
        => TryParseVideoH3PromptRewriteResponse(
            payload,
            out candidate,
            out _,
            out _);

    public static bool TryValidateVideoH3PromptForSmoke(
        string raw,
        out string normalized)
        => TryNormalizeAndValidateVideoH3Prompt(raw, out normalized);
    public bool VideoH3PromptRewritePanelVisibleForSmoke =>
        ModalVideoH3PromptRewritePanel.Visibility == Visibility.Visible;

    public static int VideoH3PromptRewriteResponseByteLimitForSmoke =>
        MaxVideoH3PromptRewriteResponseBytes;

    public IReadOnlyList<string> VideoH3PromptRewriteSurfaceIssuesForSmoke
    {
        get
        {
            var issues = new List<string>();
            if (!IsMiniMaxH3VideoModel(_videoModelId))
                issues.Add("model");
            if (ModalVideoH3PromptRewritePanel.Visibility != Visibility.Visible)
                issues.Add("panel");
            if (ModalVideoH3PromptCandidateTextBox.MaxLength
                != MaxVideoH3CandidateEditorLength)
            {
                issues.Add("candidate-bound");
            }
            if (AutomationProperties.GetName(
                    ModalVideoH3RewritePromptButton).Length == 0)
            {
                issues.Add("rewrite-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalVideoH3RewriteModeComboBox).Length == 0)
            {
                issues.Add("mode-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalVideoH3PromptCandidateTextBox).Length == 0)
            {
                issues.Add("candidate-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalVideoH3ApplyPromptButton).Length == 0)
            {
                issues.Add("apply-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalVideoH3UndoPromptButton).Length == 0)
            {
                issues.Add("undo-a11y");
            }
            return issues;
        }
    }
}
