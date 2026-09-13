using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

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
        try
        {
            result = await Task.Run(() =>
            {
                PngParametersMetadata? metadata = ReadPngParametersMetadata(source.SourceIdentity, timeout.Token, out bool known);
                return (known, metadata?.Prompt);
            }, timeout.Token);
        }
        catch (OperationCanceledException) { }
        finally { _videoProgramMetadataPending = false; }
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
        return _videoProgramAppliedContext == VideoProgramContext()
            && _videoProgramAppliedPrompt == _videoPrompt
            ? null : "指示言語の設定に対応するH3候補を作成し、確認して反映してください。動画ジョブは追加していません。";
    }

    private string? ValidateCapturedVideoProgram(string? context, string prompt)
    {
        if (context is null && !_videoPromptProgram.Enabled) return null;
        if (context is null || !_videoPromptProgram.Enabled || context != VideoProgramContext()
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
    }
}
