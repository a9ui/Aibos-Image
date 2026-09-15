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
        new("step-side", "横に一歩ずれる", "The woman takes one small step sideways within the available space."),
        new("approach-brisk", "軽快に一歩近づく", "The woman takes one brisk, light step toward the viewer."),
        new("half-turn", "半身になる", "The woman turns into a slight three-quarter stance without moving away."),
        new("look-back", "振り返る", "The woman turns her head and shoulders back toward the viewer."),
        new("small-bow", "軽くおじぎする", "The woman gives one small, natural bow."),
        new("nod", "小さくうなずく", "The woman gives one small nod while preserving her body position."),
        new("head-tilt", "首をかしげる", "The woman gently tilts her head to one side."),
        new("shoulder-shrug", "肩をすくめる", "The woman briefly raises her shoulders in a small shrug."),
        new("shoulders-open", "胸を張って姿勢を開く", "The woman gently opens her shoulders and straightens her upper back."),
        new("curl-in", "少し身を縮める", "The woman subtly draws her shoulders inward and makes her posture smaller."),
        new("settle-seated", "座った姿勢を整える", "If already seated, the woman makes a small seated posture adjustment without standing."),
        new("slight-rise", "上体を少し起こす", "The woman raises her upper body slightly while retaining its existing support."),
        new("bend-forward", "少し前かがみになる", "The woman bends forward slightly from the hips, maintaining balance."),
        new("knees-soften", "膝の力をゆるめる", "If standing, the woman softens her knees slightly without crouching or losing balance."),
        new("heel-lift", "かかとを少し浮かせる", "If standing securely, the woman briefly lifts her heels a little, then settles them."),
        new("breath-settle", "ひと呼吸おいて落ち着く", "The woman takes one quiet breath and settles into the existing pose, without adding sound."),
    ];
    public static readonly Choice[] Arms =
    [
        new("original", "元の指示を使う", ""),
        new("lower", "腕をすっと下ろす", "She smoothly lowers her free arms to rest beside her body."),
        new("lower-slow", "腕をゆっくり下ろす", "She slowly lowers her free arms with relaxed elbows and wrists."),
        new("behind-back", "後ろで手を組む", "She gently brings her free hands together behind her lower back."),
        new("front-clasp", "前で手を重ねる", "She loosely rests one free hand over the other in front of her lower torso."),
        new("fold-arms", "腕を組む", "She comfortably folds her free arms across her torso."),
        new("unfold-arms", "腕組みをほどく", "If her arms are folded, she gently unfolds them and lets them settle naturally."),
        new("still", "腕・手を動かさない", "She keeps the current arm and hand pose relative to her torso, without added gestures; the limbs move naturally with her body."),
        new("relaxed", "腕の力を抜く", "She releases unnecessary tension from her free arms, elbows and fingers."),
        new("fidget-fingers", "指先をモジモジさせる", "She makes occasional small, hesitant finger fidgets, keeping her hands close to their current position."),
        new("fidget-hands", "手を小さく握り直す", "She occasionally and softly clasps and releases her free hands in a self-conscious gesture."),
        new("hold-wrist", "片手でもう片方の手首を持つ", "One free hand loosely holds the opposite wrist in front of her."),
        new("hold-elbow", "片手を反対の肘に添える", "She gently rests one free hand against the opposite elbow."),
        new("hand-hip", "片手を腰に添える", "She places one free hand lightly at her waist, with a relaxed elbow."),
        new("hands-hips", "両手を腰に添える", "She places her free hands lightly at her waist without forcing her shoulders."),
        new("hands-lap", "膝の上に手を置く", "If seated, she gently rests her free hands on her lap."),
        new("hands-thighs", "太ももの上に手を添える", "She gently rests her free hands on her own upper legs when reachable from the reference pose."),
        new("tuck-hair", "髪を耳にかける", "With one free hand, she lightly tucks a strand of hair behind her ear, then settles the hand."),
        new("touch-hair", "髪にそっと触れる", "One free hand briefly brushes her own hair, then returns naturally."),
        new("touch-cheek", "頬に手を添える", "She lightly rests the fingertips of one free hand against her own cheek."),
        new("hand-chin", "顎に手を添える", "She gently brings one free hand near her chin in a thoughtful gesture."),
        new("cover-mouth", "口元に手を添える", "She briefly brings one free hand near her mouth without obscuring her whole face."),
        new("small-wave", "小さく手を振る", "She gives one small, relaxed wave with a free hand, then settles it."),
        new("palm-up", "手のひらを上に向ける", "She gently turns one free palm upward in a small presenting gesture."),
        new("open-hands", "手を軽く広げる", "She opens her free hands slightly with relaxed fingers, close to her body."),
        new("reach-forward", "片手をそっと差し出す", "She gently extends one free hand a short distance toward the viewer without touching anyone."),
        new("draw-hands-in", "手を身体の近くに引く", "She draws her free hands a little closer to her torso without changing her overall position."),
        new("stretch-arms", "腕を軽く伸ばす", "She gently extends her free arms within a comfortable range, then relaxes them."),
        new("loosen-wrists", "手首を軽くほぐす", "She makes one small, relaxed wrist movement with each free hand, then settles."),
        new("soft-fists", "手を軽く握る", "She loosely closes her free hands without straining her fingers."),
        new("unclench", "握った手をゆるめる", "She gently unclenches her free hands and relaxes her fingers."),
        new("hands-heart", "両手を胸元に重ねる", "She rests her free hands lightly over her own upper chest in a quiet gesture."),
        new("small-gesture", "会話するように手を動かす", "She uses occasional small, restrained free-hand gestures without adding dialogue."),
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
        new("warm-smile", "温かく笑いかける", "A warm, welcoming smile."),
        new("beaming", "満面の笑み", "A broad, delighted smile with natural variation."),
        new("subtle-smile", "口元だけ少しほころぶ", "A barely perceptible smile that softly lifts the corners of her mouth."),
        new("wry-smile", "苦笑い", "A small, rueful smile."),
        new("smug", "余裕のある笑み", "A quietly knowing, self-satisfied smile."),
        new("tender", "慈しむような表情", "A tender, caring expression."),
        new("attentive", "熱心に見守る", "An attentive, engaged expression."),
        new("admiring", "感心している", "A softly impressed, admiring expression."),
        new("awe", "目を見張る", "An awestruck expression that relaxes naturally between moments."),
        new("doubtful", "半信半疑", "A doubtful expression with a slightly raised brow."),
        new("disappointed", "がっかりしている", "A disappointed expression with gently lowered brows."),
        new("unimpressed", "あきれ気味", "An unimpressed, mildly exasperated expression."),
        new("bored", "退屈そう", "A bored, disengaged expression without stopping the requested action."),
        new("apologetic", "申し訳なさそう", "A quietly apologetic expression."),
        new("wistful", "切なげ", "A wistful, reflective expression."),
        new("tearful", "泣きそうな表情", "A near-tearful expression without adding a crying event or vocalization."),
        new("composed-smile", "余裕を保った微笑み", "A measured, composed smile."),
        new("bashful-smile", "照れ笑い", "A bashful smile that comes and goes naturally."),
        new("resolute-soft", "穏やかだが意志が強い", "A gentle expression with quiet resolve."),
        new("blank-surprise", "きょとんとしている", "A mildly surprised, momentarily blank expression that responds naturally."),
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
        new("casual", "気取らず普段どおり", "An easygoing, everyday manner."),
        new("polite", "丁寧で礼儀正しい", "A polite, considerate manner."),
        new("graceful", "ゆったり優雅", "An unhurried, graceful manner."),
        new("precise", "きびきび正確", "A precise, purposeful manner without adding actions."),
        new("careful", "慎重に確かめながら", "A careful, deliberate manner."),
        new("eager", "積極的で前向き", "An eager, positive manner."),
        new("bashful", "照れながら控えめに", "A bashful, self-conscious manner."),
        new("earnest", "一生懸命に", "An earnest, sincere manner."),
        new("hesitant", "ためらいがちに", "A hesitant manner, preserving the requested action."),
        new("solemn", "厳かで静か", "A solemn, restrained mood."),
        new("cool-headed", "冷静で余裕のある", "A collected, assured manner."),
        new("unhurried", "のんびりマイペース", "An unhurried, easy-paced manner."),
        new("restless", "そわそわ落ち着かない", "A restless manner expressed subtly within the requested action."),
        new("reassuring", "包み込むように穏やか", "A reassuring, patient manner."),
        new("curious", "探るように興味深く", "An inquisitive, exploratory manner."),
        new("restrained", "感情を抑えた", "An emotionally restrained manner."),
    ];

    public static bool IsValid(VideoPromptProgram program)
        => Opening.Any(c => c.Id == program.OpeningMotionId)
            && Arms.Any(c => c.Id == program.ArmMotionId)
            && Expressions.Any(c => c.Id == program.ExpressionId)
            && Moods.Any(c => c.Id == program.MoodId);

    public static bool IsSelected(VideoPromptProgram program)
        => program.OpeningMotionId != "original" || program.ArmMotionId != "original" || program.ExpressionId != "original" || program.MoodId != "original";

    public static string Instruction(VideoPromptProgram program)
    {
        var parts = new List<string>();
        string opening = Opening.Single(c => c.Id == program.OpeningMotionId).Text;
        string arms = Arms.Single(c => c.Id == program.ArmMotionId).Text;
        string expression = Expressions.Single(c => c.Id == program.ExpressionId).Text;
        string mood = Moods.Single(c => c.Id == program.MoodId).Text;
        if (opening.Length > 0) parts.Add("Opening movement, immediately after the reference frame during the first one to two seconds: " + opening
            + " Continue smoothly from the exact reference pose; respect visible supports and available space. Do not teleport or turn this into repeated movement throughout the clip.");
        if (arms.Length > 0) parts.Add("Arm and hand direction: " + arms
            + " Start smoothly from the reference pose during the opening, then retain the resulting relaxed pose or subtle gesture as appropriate. This controls only arms and hands, not travel, camera or facial expression. If opening movement suggests incidental arm gestures, use this selected arm direction instead. Preserve contact, held objects and weight-bearing support required by the main action; hand movements required by the main action take priority. Initial contact that is not required may be released when the selected arm direction explicitly calls for it. Do not invent a release, hand-off, or dropped object. Do not force an unavailable gesture, add an object, or repeat a one-off gesture.");
        if (expression.Length > 0) parts.Add("Facial direction throughout the clip: " + expression
            + " Let the expression respond naturally to the ongoing action; do not freeze the face.");
        if (mood.Length > 0) parts.Add("Overall performance mood: " + mood);
        if (parts.Count > 0) parts.Add("These selected acting directions take precedence only for their named aspects. An explicit facial direction takes precedence over mood for facial expression. Opening movement controls initial body position; arm direction controls only available arms and hands. Keep the requested main action, existing people, camera, setting, spoken lines, soundscape and music unchanged.");
        return string.Join("\n", parts);
    }

    public static bool TryApply(string prompt, VideoPromptProgram program, out string result, out string error)
        => VideoPromptSections.TryInsertVisual(prompt, Instruction(program), out result, out error);
}
