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
        program.ExpressionId = "future-unknown";
        checks["unknownActingPresetProtectsStoredStyle"] = !VideoPromptProgram.TryRead(program.Snapshot(), out _);
    }
}
