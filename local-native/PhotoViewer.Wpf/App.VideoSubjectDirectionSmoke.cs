namespace PhotoViewer.Wpf;

public partial class App
{
    private static void VerifySubjectDirections(Dictionary<string, bool> checks)
    {
        string original = CreateVideoH3Candidate("The adult presenter says <d>[Japanese]こんにちは。</d> and waves.");
        var program = VideoPromptAnnotation.BuiltIn(original);
        checks["actingDefaultPreservesOriginal"] = program.TryGetUnchangedH3("anime", out string unchanged) && unchanged == original;
        bool valid = true;
        foreach (var choice in VideoSubjectDirection.Opening)
        {
            program.OpeningMotionId = choice.Id;
            valid &= program.TryResolveH3("anime", null, out string prompt, out _)
                && prompt.Contains("<d>[Japanese]こんにちは。</d>")
                && (choice.Id == "original" || prompt.Contains("first one to two seconds"));
        }
        program.OpeningMotionId = "original";
        foreach (var choice in VideoSubjectDirection.Arms)
        {
            program.ArmMotionId = choice.Id;
            foreach (string kind in new[] { "anime", "photoreal" })
                valid &= program.TryResolveH3(kind, null, out string prompt, out _)
                    && prompt.Contains("<d>[Japanese]こんにちは。</d>")
                    && (choice.Id == "original" || (prompt.Contains(choice.Text)
                        && prompt.Contains("hand movements required by the main action take priority")
                        && prompt.Contains("only arms and hands, not travel")));
        }
        program.ArmMotionId = "original";
        foreach (var choice in VideoSubjectDirection.Expressions)
        {
            program.ExpressionId = choice.Id;
            valid &= program.TryResolveH3("anime", null, out string prompt, out _)
                && (choice.Id == "original" || prompt.Contains("Facial direction throughout the clip:"));
        }
        program.ExpressionId = "original";
        foreach (var choice in VideoSubjectDirection.Moods)
        {
            program.MoodId = choice.Id;
            valid &= program.TryResolveH3("anime", null, out string prompt, out _)
                && (choice.Id == "original" || prompt.Contains("Overall performance mood:"));
        }
        checks["allActingPresetsHaveIndependentTimingAndKeepDialogue"] = valid;
        program.ArmMotionId = "lower"; program.OpeningMotionId = "step-back"; program.ExpressionId = "bright"; program.MoodId = "relaxed";
        checks["actingChoicesRoundTripWithStyle"] = VideoPromptProgram.TryRead(program.Snapshot(), out var restored)
            && restored.ArmMotionId == "lower" && restored.OpeningMotionId == "step-back" && restored.ExpressionId == "bright" && restored.MoodId == "relaxed";
        checks["actingChoicesReachDeferredAiInstruction"] = program.TryCompile("anime", null, 362, out string instruction, out _)
            && instruction.Contains("takes a small step backward") && instruction.Contains("A bright, open expression")
            && instruction.Contains("A relaxed, natural manner") && instruction.Contains("smoothly lowers her free arms");
        program.ArmMotionId = program.OpeningMotionId = program.ExpressionId = program.MoodId = "original";
        checks["resetActingPresetsRestoresExactOriginal"] = program.TryGetUnchangedH3("anime", out unchanged) && unchanged == original;
        using var legacy = System.Text.Json.JsonDocument.Parse("{\"Version\":1,\"Enabled\":false,\"FutureCompatible\":{\"value\":42}}");
        checks["oldStylesDefaultArmsAndKeepUnknownFields"] = VideoPromptProgram.TryRead(legacy.RootElement, out var old)
            && old.ArmMotionId == "original" && old.Snapshot().GetProperty("FutureCompatible").GetProperty("value").GetInt32() == 42;
        program.ArmMotionId = "future-unknown";
        checks["unknownArmPresetProtectsStoredStyle"] = !VideoPromptProgram.TryRead(program.Snapshot(), out _);
        program.ArmMotionId = "original";
        var selected = new VideoPromptProgram { ExpressionId = "bright", ArmMotionId = "lower" };
        string spoken = "<d>[Japanese]見出しは\noverall_soundscape: と\nnon_diegetic_music: です。</d>";
        string structural = CreateVideoH3Candidate("The presenter says " + spoken + ".\nShe reads \"\noverall_soundscape: quoted\".");
        string selectedDirection = VideoSubjectDirection.Instruction(selected);
        checks["actingInsertionSkipsDialogueAndQuotedSectionMarkers"] =
            VideoSubjectDirection.TryApply(structural, selected, out string inserted, out _)
            && inserted.Replace(selectedDirection + "\n\n", "", StringComparison.Ordinal) == structural
            && inserted.Contains(spoken) && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) > inserted.IndexOf("quoted", StringComparison.Ordinal)
            && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) < inserted.LastIndexOf("overall_soundscape:", StringComparison.Ordinal);
        string musicOnly = MiniMaxH3I2vaPromptConformance.Opening + MiniMaxH3I2vaPromptConformance.IntegratedPrefix
            + "The presenter waves." + MiniMaxH3I2vaPromptConformance.MusicPrefix + "N/A";
        checks["actingInsertionUsesMusicBoundaryWhenSoundIsMissing"] =
            VideoSubjectDirection.TryApply(musicOnly, selected, out inserted, out _)
            && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) < inserted.IndexOf("non_diegetic_music:", StringComparison.Ordinal)
            && inserted.Replace(selectedDirection + "\n\n", "", StringComparison.Ordinal) == musicOnly;
        string nestedQuotation = MiniMaxH3I2vaPromptConformance.IntegratedMarker + " [Shot 1]\nA sign displays:\n"
            + "「説明は『「例」と書き、\noverall_soundscape: という文字を表示する』です」\nnon_diegetic_music: N/A";
        checks["nestedQuotesCannotBecomeAudioStructure"] =
            VideoSubjectDirection.TryApply(nestedQuotation, selected, out inserted, out _)
            && inserted.Replace(selectedDirection + "\n\n", "", StringComparison.Ordinal) == nestedQuotation
            && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) > inserted.IndexOf("です」", StringComparison.Ordinal)
            && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) < inserted.IndexOf("non_diegetic_music:", StringComparison.Ordinal);
        bool lineEndings = true;
        foreach (string newline in new[] { "\n", "\r\n" })
        {
            string escapedLine = "integrated_multimodal_description: [Shot 1]" + newline
                + "A sign displays the character \\" + newline + "overall_soundscape: Quiet room."
                + newline + "non_diegetic_music: N/A";
            lineEndings &= VideoSubjectDirection.TryApply(escapedLine, selected, out inserted, out _)
                && inserted.Replace(selectedDirection + "\n\n", "", StringComparison.Ordinal) == escapedLine
                && inserted.IndexOf(selectedDirection, StringComparison.Ordinal) < inserted.IndexOf("overall_soundscape:", StringComparison.Ordinal);
        }
        checks["backslashCannotConsumeLfOrCrlfSectionBoundary"] = lineEndings;
        string malformed = structural.Replace("</d>", "", StringComparison.Ordinal);
        checks["ambiguousBoundaryFailsOnlySelectedDirections"] =
            !VideoSubjectDirection.TryApply(malformed, selected, out _, out string insertionError) && insertionError.Length > 0
            && VideoSubjectDirection.TryApply(malformed, new(), out inserted, out _) && inserted == malformed;
        checks["armPriorityPermitsExplicitReleaseWithoutDroppingRequiredSupport"] =
            selectedDirection.Contains("support required by the main action")
            && selectedDirection.Contains("may be released when the selected arm direction explicitly calls for it")
            && selectedDirection.Contains("Do not invent a release, hand-off, or dropped object");
        var bound = new VideoPromptProgram
        {
            Enabled = true, AnnotatedH3 = true,
            Template = "[She steps forward.] [She folds her arms.] [She looks stern.] [The mood is serious.] The presenter holds the cup and says <d>\\[Japanese\\]こんにちは。</d>.",
        };
        foreach (var pair in new[]
        {
            ("[She steps forward.]", "opening"), ("[She folds her arms.]", "arms"),
            ("[She looks stern.]", "expression"), ("[The mood is serious.]", "mood"),
        }) bound.Options[pair.Item1] = new() { DirectionAspect = pair.Item2 };
        bound.TryResolveH3("anime", null, out string allOriginal, out _);
        bound.BaseH3Template = allOriginal;
        bound.PhotorealTemplate = bound.Template;
        bound.PhotorealBaseH3Template = allOriginal;
        var snapshot = bound.Snapshot();
        bool replaces = true;
        foreach (string kind in new[] { "anime", "photoreal" })
        {
            bound.OpeningMotionId = "step-back"; bound.ArmMotionId = "lower";
            bound.ExpressionId = "bright"; bound.MoodId = "relaxed";
            replaces &= bound.TryResolveH3(kind, null, out string resolved, out _)
                && !resolved.Contains("She steps forward.") && !resolved.Contains("She folds her arms.")
                && !resolved.Contains("She looks stern.") && !resolved.Contains("The mood is serious.")
                && resolved.Contains("holds the cup") && resolved.Contains("<d>[Japanese]こんにちは。</d>")
                && bound.TryCompile(kind, null, 362, out string compiled, out _)
                && !compiled.Contains("She looks stern.") && !compiled.Contains("The mood is serious.");
        }
        checks["reviewedClausesAreReplacedInsteadOfContradicted"] = replaces;
        bound.RestoreOriginalDirection("opening"); bound.RestoreOriginalDirection("arms");
        bound.RestoreOriginalDirection("expression"); bound.RestoreOriginalDirection("mood");
        checks["resetBindingsRetainsOriginalOptionsAndContent"] = bound.TryResolveH3("anime", null, out string reset, out _)
            && reset == allOriginal && bound.Options.Values.All(option => option.Mode == "on")
            && VideoPromptProgram.TryRead(snapshot, out var boundCopy) && boundCopy.Options.Values.All(option => option.DirectionAspect.Length > 0);
        var mixed = new VideoPromptProgram
        {
            Enabled = true, AnnotatedH3 = true,
            Template = "She holds the cup [with a stern expression].",
            ExpressionId = "bright",
            Options = new() { ["[with a stern expression]"] = new()
            { DirectionAspect = "expression", DirectionReplacement = "with the selected expression" } },
        };
        checks["mixedDirectionClauseKeepsMainActionAndGrammar"] = mixed.TryResolveH3("anime", null, out string mixedPrompt, out _)
            && mixedPrompt.Contains("She holds the cup with the selected expression.") && !mixedPrompt.Contains("stern")
            && mixed.TryCompile("anime", null, 362, out string mixedInstruction, out _) && !mixedInstruction.Contains("stern");
        var editor = new VideoPromptAuthoringControl();
        editor.Load(mixed, "", "anime", "anime", null);
        checks["replacedDirectionIsVisibleAsStruckTextWithReplacement"] =
            ((System.Windows.Controls.TextBlock)editor.ReadingSurfaceForSmoke).Inlines
                .OfType<System.Windows.Documents.Hyperlink>().Single().TextDecorations == System.Windows.TextDecorations.Strikethrough
            && editor.ReadingTextForSmoke.Contains("with the selected expression");
        bound.Options.Values.First().DirectionAspect = "future-aspect";
        checks["unknownDirectionBindingProtectsStyle"] = !VideoPromptProgram.TryRead(bound.Snapshot(), out _);
        program.ExpressionId = "future-unknown";
        checks["unknownActingPresetProtectsStoredStyle"] = !VideoPromptProgram.TryRead(program.Snapshot(), out _);
    }
}
