namespace PhotoViewer.Wpf;

// Shared, optional acting directions. No source inspection or style rewriting.
public static class VideoSubjectDirection
{
    public sealed record Choice(string Id, string Label, string Text);
    public static readonly Choice[] Opening =
    [
        new("original", "元の指示を使う", ""),
        new("approach", "こちらへ近づく", "The woman begins moving closer to the viewer."),
        new("approach-slow", "ゆっくり近づく", "The woman cautiously starts approaching the viewer at a slow pace."),
        new("step-back", "後ずさる", "The woman takes a small step backward."),
        new("stay", "その場にとどまる", "The woman stays in her current position, with natural breathing and small movements."),
        new("sway", "身体をゆっくり揺らす", "The woman starts gently swaying her body."),
        new("sway-rhythm", "リズムに合わせて揺れる", "The woman begins a small rhythmic body sway."),
        new("tense", "身体を強張らせる", "The woman briefly tenses her shoulders and posture."),
        new("relax", "力を抜く", "The woman gradually relaxes her shoulders and posture."),
        new("lean-in", "その場で身を乗り出す", "The woman leans slightly toward the viewer without changing her footing."),
        new("lean-back", "その場で身を引く", "The woman leans her upper body slightly away without changing her footing."),
        new("turn-viewer", "こちらへ身体を向ける", "The woman begins turning toward the viewer."),
        new("turn-away", "少し身体をそらす", "The woman turns her upper body slightly away."),
        new("weight-shift", "重心を移す", "The woman gently shifts her weight while staying in place."),
        new("straighten", "姿勢を正す", "The woman gradually straightens her posture."),
        new("small-startle", "少し驚いて反応する", "The woman gives a brief small startle, then settles into the requested action."),
        new("pause", "一瞬ためらってから動く", "The woman briefly hesitates, then begins the requested action."),
    ];
    public static readonly Choice[] Expressions =
    [
        new("original", "元の指示を使う", ""),
        new("bright", "明るい表情", "A bright, open expression."),
        new("cheerful", "楽しそう", "She looks cheerful and appears to enjoy performing the requested action."),
        new("gentle-smile", "穏やかな微笑み", "A gentle, relaxed smile."),
        new("laughing", "笑いをこらえきれない", "An amused expression with occasional natural smiles, without adding dialogue."),
        new("playful", "いたずらっぽい", "A playful, mischievous smile."),
        new("confident", "自信たっぷり", "A composed, self-assured expression."),
        new("proud", "得意げ", "A pleased, slightly proud expression."),
        new("shy", "はにかんだ表情", "A shy, restrained smile."),
        new("embarrassed", "照れくさい", "A self-conscious, embarrassed expression with subtle changes."),
        new("annoyed", "ムッとしている", "A mildly annoyed expression while continuing the requested action."),
        new("pouting", "ふくれっ面", "A small pout and slightly furrowed brows."),
        new("irritated", "いらだっている", "An irritated expression with a tense brow."),
        new("stern", "きりっと厳しい", "A stern, firm expression."),
        new("serious", "真剣", "A serious, attentive expression."),
        new("focused", "集中している", "A focused expression, absorbed in the requested action."),
        new("determined", "決意を感じる", "A determined, resolute expression."),
        new("curious", "興味津々", "A curious, interested expression."),
        new("expectant", "期待している", "An expectant, hopeful expression."),
        new("surprised", "驚いている", "A surprised expression that changes naturally rather than staying frozen."),
        new("confused", "戸惑っている", "A puzzled, uncertain expression."),
        new("uneasy", "不安そう", "A quietly uneasy expression."),
        new("suspicious", "疑いのまなざし", "A questioning, skeptical expression."),
        new("somber", "暗く沈んだ表情", "A subdued, somber expression."),
        new("sad", "悲しげ", "A softly sad expression, without inventing a new event."),
        new("lonely", "寂しげ", "A distant, lonely expression."),
        new("weary", "疲れた表情", "A tired, weary expression."),
        new("sleepy", "眠たげ", "A drowsy expression with relaxed eyelids."),
        new("calm", "落ち着いている", "A calm, composed expression."),
        new("neutral", "淡々とした表情", "A restrained, neutral expression."),
        new("thoughtful", "考え込んでいる", "A thoughtful, reflective expression."),
        new("relieved", "ほっとしている", "A relieved expression that gently softens."),
    ];
    public static readonly Choice[] Moods =
    [
        new("original", "元の指示を使う", ""),
        new("lighthearted", "明るく軽やか", "A lighthearted, upbeat performance."),
        new("lively", "元気いっぱい", "A lively, energetic performance."),
        new("friendly", "親しみやすい", "A warm, friendly manner."),
        new("soft", "やさしく穏やか", "A soft, gentle manner."),
        new("relaxed", "自然体でくつろいだ", "A relaxed, natural manner."),
        new("quiet", "静かで落ち着いた", "A quiet, composed mood."),
        new("elegant", "上品でしなやか", "An elegant, poised manner."),
        new("cool", "クールで淡々と", "A cool, understated manner."),
        new("bold", "堂々と力強く", "A bold, confident manner."),
        new("playful", "お茶目で遊び心のある", "A playful, lightly teasing manner."),
        new("comedic", "コミカル", "A lightly comedic performance without adding new actions or characters."),
        new("awkward", "ぎこちなく不器用", "A slightly awkward, hesitant manner."),
        new("reserved", "控えめで遠慮がち", "A reserved, tentative manner."),
        new("reluctant", "気乗りしない様子", "A mildly reluctant, unenthusiastic manner, without adding events or changing the requested action."),
        new("tense", "張りつめた空気", "A tense, restrained performance."),
        new("serious", "真面目で真剣", "A serious, purposeful manner."),
        new("dramatic", "ドラマチック", "An emotionally expressive, dramatic performance."),
        new("melancholy", "物憂げでしっとり", "A subdued, wistful mood."),
        new("nostalgic", "懐かしさのある", "A reflective, nostalgic mood."),
        new("mysterious", "ミステリアス", "A quiet, enigmatic manner."),
        new("dreamy", "夢見心地", "A soft, dreamy manner."),
    ];

    public static bool IsValid(VideoPromptProgram program)
        => Opening.Any(c => c.Id == program.OpeningMotionId)
            && Expressions.Any(c => c.Id == program.ExpressionId)
            && Moods.Any(c => c.Id == program.MoodId);

    public static bool IsSelected(VideoPromptProgram program)
        => program.OpeningMotionId != "original" || program.ExpressionId != "original" || program.MoodId != "original";

    public static string Instruction(VideoPromptProgram program)
    {
        var parts = new List<string>();
        string opening = Opening.Single(c => c.Id == program.OpeningMotionId).Text;
        string expression = Expressions.Single(c => c.Id == program.ExpressionId).Text;
        string mood = Moods.Single(c => c.Id == program.MoodId).Text;
        if (opening.Length > 0) parts.Add("Opening movement, immediately after the reference frame during the first one to two seconds: " + opening
            + " Continue smoothly from the exact reference pose; respect visible supports and available space. Do not teleport or turn this into repeated movement throughout the clip.");
        if (expression.Length > 0) parts.Add("Facial direction throughout the clip: " + expression
            + " Let the expression respond naturally to the ongoing action; do not freeze the face.");
        if (mood.Length > 0) parts.Add("Overall performance mood: " + mood);
        if (parts.Count > 0) parts.Add("These selected acting directions take precedence only for their named aspects. Keep the requested main action, existing people, camera, setting, spoken lines, soundscape and music unchanged.");
        return string.Join("\n", parts);
    }

    public static string Apply(string prompt, VideoPromptProgram program)
    {
        string direction = Instruction(program);
        if (direction.Length == 0) return prompt;
        int sound = prompt.IndexOf("overall_soundscape:", StringComparison.Ordinal);
        return sound >= 0 ? prompt.Insert(sound, direction + "\n\n") : prompt + "\n\n" + direction;
    }
}
