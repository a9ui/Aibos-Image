using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private VideoPromptProgram _videoPromptProgram = new();
    private string _videoProgramSourceOverride = "auto";
    private string? _videoProgramOverrideSourceKey;
    private string? _videoProgramCandidateContext;
    private string? _videoProgramAppliedContext;
    private string? _videoProgramAppliedPrompt;
    private bool _videoProgramMetadataPending;
    private bool _videoProgramMetadataRead;
    private string? _videoProgramMetadataSourceKey;
    private string? _videoProgramMetadataStamp;
    private string? _videoProgramMetadataPrompt;
    private bool _syncingVideoAuthoringControls;

    private void RefreshVideoPromptAuthoringControls()
    {
        if (_syncingVideoAuthoringControls || ModalVideoPromptAuthoringHost is null || AppVideoPromptAuthoringHost is null) return;
        _syncingVideoAuthoringControls = true;
        try
        {
            ApplyDirectVideoSourceVariant();
            SeparateVideoPromptNotes();
            foreach (ContentControl host in new[] { ModalVideoPromptAuthoringHost, AppVideoPromptAuthoringHost })
            {
                if (host.Content is not VideoPromptAuthoringControl)
                {
                    var editor = new VideoPromptAuthoringControl();
                    editor.Changed += VideoAuthoringChanged;
                    editor.SourceChanged += choice =>
                    {
                        if (choice != "auto" && !_videoPromptProgram.Enabled && !_videoPromptProgram.UseSourceVariants)
                        {
                            string literal = VideoPromptAuthoringControl.EscapeLiteral(_videoPrompt);
                            if (literal.Length > 8000)
                            {
                                SetVideoStyleStatus("描写の切り替えには本文を短くする必要があります。元の文章は変更していません。");
                                RefreshVideoPromptAuthoringControls();
                                return;
                            }
                            _videoPromptProgram.Template = literal;
                            _videoPromptProgram.BaseH3Template = "";
                            _videoPromptProgram.Enabled = true;
                            MarkVideoPromptTemplateAsCustom();
                            MarkVideoStyleAsCustom();
                            RefreshVideoStyleControls(updateNameFields: false);
                        }
                        _videoProgramSourceOverride = choice;
                        _videoProgramOverrideSourceKey = VideoProgramSourceKey();
                        InvalidateVideoProgramAuthoring();
                        RefreshVideoPromptAuthoringControls();
                    };
                    editor.DetailsRequested += () => OpenVideoPromptProgram_Click(editor, new RoutedEventArgs());
                    host.Content = editor;
                }
                ((VideoPromptAuthoringControl)host.Content).Load(_videoPromptProgram, _videoPrompt,
                    _videoProgramOverrideSourceKey == VideoProgramSourceKey() ? _videoProgramSourceOverride : "auto",
                    EffectiveVideoProgramSourceKind(), VideoProgramSourcePrompt());
            }
        }
        finally { _syncingVideoAuthoringControls = false; }
    }

    private void VideoAuthoringChanged(VideoPromptAuthoringControl sender, VideoPromptProgram program, string? rawPrompt)
    {
        if (_syncingVideoAuthoringControls) return;
        _syncingVideoAuthoringControls = true;
        try
        {
            _videoPromptProgram = program;
            if (rawPrompt is not null)
            {
                if (_videoPromptProgram.UseSourceVariants && EffectiveVideoProgramSourceKind() == "photoreal")
                    _videoPromptProgram.PhotorealBaseH3Template = rawPrompt;
                else _videoPromptProgram.BaseH3Template = rawPrompt;
                ModalVideoPromptTextBox.Text = rawPrompt;
            }
            MarkVideoStyleAsCustom();
            MarkVideoPromptTemplateAsCustom();
            RefreshVideoStyleControls(updateNameFields: false);
            InvalidateVideoProgramAuthoring();
            ApplyDirectVideoSourceVariant();
            foreach (ContentControl host in new[] { ModalVideoPromptAuthoringHost, AppVideoPromptAuthoringHost })
                if (host.Content is VideoPromptAuthoringControl peer && !ReferenceEquals(peer, sender))
                    peer.Load(_videoPromptProgram, _videoPrompt, _videoProgramSourceOverride, EffectiveVideoProgramSourceKind(), VideoProgramSourcePrompt());
            SetVideoStyleStatus(_videoPromptProgram.AnnotatedH3 && !_videoPromptProgram.TryGetUnchangedH3(EffectiveVideoProgramSourceKind(), out _)
                ? "選択を変更しました。H3候補を作成して反映すると、外した部分や文のつながりが生成用に整います。残す場合はスタイルを保存してください。"
                : "変更は今回の動画に使います。残したい場合は名前を付けてスタイルを保存してください。");
        }
        finally { _syncingVideoAuthoringControls = false; }
    }

    private void InvalidateVideoProgramAuthoring()
    {
        _videoProgramCandidateContext = null;
        _videoProgramAppliedContext = null;
        _videoProgramAppliedPrompt = null;
        VideoH3PromptRewriteContextChanged();
        UpdateVideoGenerationActionControls();
    }

    private void ApplyDirectVideoSourceVariant()
    {
        string prompt;
        if (_videoPromptProgram.Enabled)
        {
            if (_changingVideoPromptForH3History
                || (_videoProgramAppliedContext == VideoProgramContext() && _videoProgramAppliedPrompt == _videoPrompt)
                || !_videoPromptProgram.TryGetUnchangedH3(EffectiveVideoProgramSourceKind(), out prompt)) return;
        }
        else
        {
            if (!_videoPromptProgram.UseSourceVariants) return;
            prompt = _videoPromptProgram.BaseTemplateFor(EffectiveVideoProgramSourceKind());
        }
        if (_videoPrompt == prompt) return;
        _videoPrompt = prompt;
        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try { ModalVideoPromptTextBox.Text = prompt; AppVideoPromptTextBox.Text = prompt; }
        finally { _syncingVideoGenerationSettings = wasSyncing; }
        InvalidateVideoProgramAuthoring();
    }

    private void UpdateDirectVideoSourceVariant(string prompt)
    {
        if (!_videoPromptProgram.UseSourceVariants || _videoPromptProgram.Enabled) return;
        if (EffectiveVideoProgramSourceKind() == "photoreal") _videoPromptProgram.PhotorealBaseH3Template = prompt;
        else _videoPromptProgram.BaseH3Template = prompt;
    }

    private void SeparateVideoPromptNotes()
    {
        // Mechanical, lossless split of the existing explicit notes delimiter.
        // Keep the complete original in compatible extension data. Persist only
        // when the user saves this style; never rewrite the saved library here.
        if (_videoPromptProgram.Enabled || _videoPromptProgram.Description.Length != 0) return;
        Match marker = Regex.Match(_videoPrompt, @"(?m)^▼▼▼ 使用時はこの行から末尾まで全削除｜日本語訳 ▼▼▼[^\S\r\n]*(?:\r?\n|$)");
        if (!marker.Success || marker.NextMatch().Success) return;
        string original = _videoPrompt;
        string body = original[..marker.Index];
        _videoPromptProgram.Description = original[(marker.Index + marker.Length)..];
        _videoPromptProgram.BaseH3Template = body;
        _videoPromptProgram.ExtensionData ??= new(StringComparer.Ordinal);
        _videoPromptProgram.ExtensionData.TryAdd("OriginalStyleText", JsonSerializer.SerializeToElement(new
        {
            Prompt = original,
            Separator = marker.Value,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))),
        }));
        _videoPrompt = body;
        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try { ModalVideoPromptTextBox.Text = body; AppVideoPromptTextBox.Text = body; }
        finally { _syncingVideoGenerationSettings = wasSyncing; }
    }

    private string VideoProgramSourceKey()
        => _videoSourceChoice is { } source
            ? source.SourceIdentity + "\n" + source.DisplayPath + "\n" + source.ProducerJobId
            : "";

    private string VideoProgramAutomaticKind()
    {
        if (_videoSourceChoice is not { } source) return "anime";
        // The captured source has already passed the managed-source boundary.
        // Do not infer a type from an arbitrary filename or a style name.
        string relative = Path.GetRelativePath(ResolvedManagedEnhancementOutputsRoot, source.DisplayPath);
        string first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])[0];
        return first == "Photorealized" ? "photoreal" : "anime";
    }

    private string EffectiveVideoProgramSourceKind()
        => _videoProgramSourceOverride != "auto"
            && _videoProgramOverrideSourceKey == VideoProgramSourceKey()
                ? _videoProgramSourceOverride
                : VideoProgramAutomaticKind() == "photoreal" ? "photoreal" : _videoPromptProgram.OriginalDefault;

    private string? VideoProgramSourcePrompt()
    {
        if (_videoProgramMetadataRead && _videoProgramMetadataSourceKey == VideoProgramSourceKey())
            return _videoProgramMetadataStamp == VideoProgramOriginalStamp() ? _videoProgramMetadataPrompt : null;
        if (!TryGetVideoGenerationSourceTile(out Tile tile)) return null;
        if (!string.IsNullOrWhiteSpace(tile.Prompt)) return tile.Prompt;
        if (_currentModalMetadata is not null && string.Equals(_currentModalMetadataPath, tile.Path, StringComparison.OrdinalIgnoreCase))
            return _currentModalMetadata.Prompt;
        if (_currentPreviewMetadata is not null && string.Equals(_currentPreviewMetadataPath, tile.Path, StringComparison.OrdinalIgnoreCase))
            return _currentPreviewMetadata.Prompt;
        return null;
    }

    private string? VideoProgramOriginalStamp()
    {
        try
        {
            if (_videoSourceChoice is not { } source) return null;
            var file = new FileInfo(source.SourceIdentity);
            return file.Exists ? file.FullName + "\n" + file.Length + "\n" + file.LastWriteTimeUtc.Ticks : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<bool> ReadVideoProgramMetadataForExplicitRewriteAsync()
    {
        if (_videoSourceChoice is not { } source) return false;
        string sourceKey = VideoProgramSourceKey();
        string programJson = JsonSerializer.Serialize(_videoPromptProgram);
        string? stamp = VideoProgramOriginalStamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (bool Known, string? Prompt) result = (false, null);
        _videoProgramMetadataPending = true;
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        try
        {
            result = await Task.Run(() =>
            {
                PngParametersMetadata? metadata = ReadPngParametersMetadata(source.SourceIdentity, timeout.Token, out bool known);
                return (known, metadata?.Prompt);
            }, timeout.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _videoProgramMetadataPending = false;
            RefreshVideoH3PromptRewriteControls(updateStatus: false);
        }
        if (sourceKey != VideoProgramSourceKey() || stamp != VideoProgramOriginalStamp()
            || programJson != JsonSerializer.Serialize(_videoPromptProgram))
            return false;
        _videoProgramMetadataRead = true;
        _videoProgramMetadataSourceKey = sourceKey;
        _videoProgramMetadataStamp = stamp;
        _videoProgramMetadataPrompt = result.Known ? result.Prompt ?? "" : null;
        return true;
    }

    private string VideoProgramContext()
    {
        string data = JsonSerializer.Serialize(new
        {
            Program = _videoPromptProgram, Source = VideoProgramSourceKey(),
            Kind = EffectiveVideoProgramSourceKind(), SourcePrompt = VideoProgramSourcePrompt(),
            Duration = _videoDurationSeconds, Model = _videoModelId,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    private async void OpenVideoPromptProgram_Click(object sender, RoutedEventArgs e)
    {
        if (_videoH3RewritePending || _videoProgramMetadataPending || _videoGenerationRequestPending) return;
        string sourceKey = VideoProgramSourceKey();
        string selection = _videoProgramOverrideSourceKey == sourceKey ? _videoProgramSourceOverride : "auto";
        VideoPromptProgram draft = _videoPromptProgram.Clone();
        if (string.IsNullOrWhiteSpace(draft.Template) && string.IsNullOrWhiteSpace(draft.BaseH3Template))
            draft.BaseH3Template = _videoPrompt;
        var editor = new VideoPromptEditorWindow(draft, selection,
            VideoProgramAutomaticKind(), VideoProgramSourcePrompt(),
            _videoStyles.ToDictionary(s => s.Name, s => s.Prompt, StringComparer.OrdinalIgnoreCase)) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _videoPromptProgram = editor.Program;
        _videoProgramSourceOverride = editor.SourceOverride;
        _videoProgramOverrideSourceKey = sourceKey;
        _videoProgramAppliedContext = null;
        _videoProgramCandidateContext = null;
        VideoH3PromptRewriteContextChanged();
        SetVideoStyleStatus("指示言語の編集を反映しました。再起動後も残すにはStyleを保存してください。");
        MarkVideoStyleAsCustom();
        RefreshVideoPromptAuthoringControls();
        if (editor.CreateCandidate) await RewriteVideoPromptProgramAsync();
    }

    private async Task<bool> RewriteVideoPromptProgramAsync()
    {
        if (!_videoPromptProgram.Enabled) return await RewriteVideoPromptForH3Async();
        if (_videoH3RewritePending || _videoProgramMetadataPending) return false;
        if (!await ReadVideoProgramMetadataForExplicitRewriteAsync())
        {
            SetVideoH3PromptRewriteStatus("元画像または指示設定が変わりました。画像を選び直して候補を作成してください。");
            return false;
        }
        int frames = MiniMaxH3FrameCountForDuration(_videoDurationSeconds);
        if (!_videoPromptProgram.TryCompile(EffectiveVideoProgramSourceKind(), VideoProgramSourcePrompt(), frames,
                out string request, out string error))
        {
            SetVideoH3PromptRewriteStatus(error);
            return false;
        }
        string context = VideoProgramContext();
        _videoProgramCandidateContext = null;
        bool success = await RewriteVideoPromptForH3Async(request);
        if (!success) return false;
        if (!string.Equals(context, VideoProgramContext(), StringComparison.Ordinal))
        {
            VideoH3PromptRewriteContextChanged();
            SetVideoH3PromptRewriteStatus("指示言語の設定か元画像プロンプトが変わったため、候補を採用しませんでした。");
            return false;
        }
        _videoProgramCandidateContext = context;
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        if (!string.IsNullOrEmpty(error)) SetVideoH3PromptRewriteStatus(error + " 候補を確認して反映してください。");
        return true;
    }

    private bool VideoProgramCandidateCanApply()
        => !_videoPromptProgram.Enabled || (_videoProgramCandidateContext is not null
            && _videoProgramCandidateContext == VideoProgramContext());

    private void RecordAppliedVideoProgram()
    {
        if (!_videoPromptProgram.Enabled) return;
        _videoProgramAppliedContext = _videoProgramCandidateContext;
        _videoProgramAppliedPrompt = _videoPrompt;
    }

    public void SetVideoPromptProgramForSmoke(VideoPromptProgram program, string sourceOverride = "auto")
    {
        _videoPromptProgram = program.Clone();
        _videoProgramSourceOverride = sourceOverride;
        _videoProgramOverrideSourceKey = VideoProgramSourceKey();
        _videoProgramCandidateContext = null;
        _videoProgramAppliedContext = null;
        VideoH3PromptRewriteContextChanged();
    }
    public Task<bool> RewriteVideoPromptProgramForSmokeAsync() => RewriteVideoPromptProgramAsync();
    public string? VideoPromptProgramEnqueueErrorForSmoke => ValidateVideoProgramForEnqueue();
    public string VideoPromptProgramKindForSmoke => EffectiveVideoProgramSourceKind();
    public string? VideoPromptProgramSourcePromptForSmoke => VideoProgramSourcePrompt();
    public JsonElement VideoPromptProgramSnapshotForSmoke => _videoPromptProgram.Snapshot();
    public void ResetVideoProgramOverrideForSmoke() => ResetVideoProgramOverrideAfterEnqueue();

    private string? ValidateVideoProgramForEnqueue()
    {
        if (!_videoPromptProgram.Enabled) return null;
        if (_videoPromptProgram.TryGetUnchangedH3(EffectiveVideoProgramSourceKind(), out string original)
            && _videoPrompt == original) return null;
        return _videoProgramAppliedContext == VideoProgramContext()
            && _videoProgramAppliedPrompt == _videoPrompt
            ? null : "指示言語の設定に対応するH3候補を作成し、確認して反映してください。動画ジョブは追加していません。";
    }

    private string? ValidateCapturedVideoProgram(string? context, string prompt)
    {
        if (context is null && !_videoPromptProgram.Enabled && !_videoPromptProgram.UseSourceVariants) return null;
        if (context is null || (!_videoPromptProgram.Enabled && !_videoPromptProgram.UseSourceVariants) || context != VideoProgramContext()
            || !string.Equals(prompt, _videoPrompt.Trim(), StringComparison.Ordinal))
            return "動画化の準備中に指示言語の設定が変わりました。確認して追加し直してください。";
        return ValidateVideoProgramForEnqueue();
    }

    private void RestoreVideoPromptProgram(JsonElement? snapshot)
    {
        // Unsupported documents are blocked by the style-store reader before
        // this method. Never turn a future program into a usable legacy style.
        if (!VideoPromptProgram.TryRead(snapshot, out VideoPromptProgram program)) return;
        _videoPromptProgram = program;
        _videoProgramCandidateContext = null;
        _videoProgramAppliedContext = null;
    }

    private void ResetVideoProgramOverrideAfterEnqueue()
    {
        _videoProgramSourceOverride = "auto";
        _videoProgramOverrideSourceKey = null;
        RefreshVideoPromptAuthoringControls();
    }

    public bool ExerciseVideoAuthoringForSmoke(Action<string, FrameworkElement> capture)
    {
        RefreshVideoPromptAuthoringControls();
        UpdateLayout();
        var editor = (VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content;
        bool result = editor.ExerciseInlineForSmoke(v => { UpdateLayout(); capture("native-video-edit", v); });
        UpdateLayout();
        capture("native-video-menu", ModalVideoGenerationBoardBorder);
        return result;
    }

    public void SelectInlineVideoSourceForSmoke(string kind)
        => ((VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content).SelectSourceForSmoke(kind);

    public bool SelectInlineVideoOptionForSmoke(string category, int choiceIndex, Action<FrameworkElement>? capture = null)
        => ((VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content).SelectOptionForSmoke(category, choiceIndex, capture);

    public void EditInlineVideoVariantForSmoke(string body, string note)
        => ((VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content).EditVariantForSmoke(body, note);

    public void WrapInlineVideoPhraseForSmoke(string phrase)
        => ((VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content).WrapPhraseForSmoke(phrase);

    public bool DirectVariantCaptureInvalidatesForSmoke()
    {
        string context = VideoProgramContext();
        string prompt = _videoPrompt.Trim();
        bool valid = ValidateCapturedVideoProgram(context, prompt) is null;
        SelectInlineVideoSourceForSmoke(EffectiveVideoProgramSourceKind() == "photoreal" ? "anime" : "photoreal");
        return valid && ValidateCapturedVideoProgram(context, prompt) is not null;
    }

    public void CaptureVideoVariantForSmoke(Action<string, FrameworkElement> capture)
    {
        UpdateLayout();
        capture("native-video-source-variants", ModalVideoGenerationBoardBorder);
    }
}
