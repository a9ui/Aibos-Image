namespace PhotoViewer.Wpf;

// Locate visual-section boundaries without treating quoted text or dialogue as
// structure. This is an insertion helper, not a conformance gate or a rewrite.
internal static class VideoPromptSections
{
    internal static bool TryInsertVisual(string prompt, string direction, out string result, out string error)
    {
        result = prompt;
        error = "";
        if (direction.Length == 0) return true;
        int integrated = -1, sound = -1, music = -1;
        bool dialogue = false, lineStart = true;
        var quotes = new Stack<char>();
        for (int i = 0; i < prompt.Length; i++)
        {
            char c = prompt[i];
            if (c == '\\' && i + 1 < prompt.Length && prompt[i + 1] is not ('\r' or '\n')) { i++; lineStart = false; continue; }
            if (dialogue && prompt.AsSpan(i).StartsWith("</d>", StringComparison.OrdinalIgnoreCase))
            {
                dialogue = false; i += 3; lineStart = false; continue;
            }
            if (dialogue) { lineStart = c == '\n'; continue; }
            if (quotes.Count > 0)
            {
                if (c == quotes.Peek()) quotes.Pop();
                else if (ClosingQuote(c) is char nested)
                {
                    if (quotes.Count >= 64) return Unsafe(out error);
                    quotes.Push(nested);
                }
                lineStart = c == '\n'; continue;
            }
            if (prompt.AsSpan(i).StartsWith("<d>", StringComparison.OrdinalIgnoreCase))
            { dialogue = true; i += 2; lineStart = false; continue; }
            if (ClosingQuote(c) is char quoteEnd)
            { quotes.Push(quoteEnd); lineStart = false; continue; }
            if (lineStart)
            {
                if (c is ' ' or '\t' or '\r') continue;
                var remaining = prompt.AsSpan(i);
                if (remaining.StartsWith(MiniMaxH3I2vaPromptConformance.IntegratedMarker, StringComparison.Ordinal))
                { if (integrated >= 0) return Unsafe(out error); integrated = i; }
                if (remaining.StartsWith(MiniMaxH3I2vaPromptConformance.SoundscapeMarker, StringComparison.Ordinal))
                { if (sound >= 0) return Unsafe(out error); sound = i; }
                if (remaining.StartsWith(MiniMaxH3I2vaPromptConformance.MusicMarker, StringComparison.Ordinal))
                { if (music >= 0) return Unsafe(out error); music = i; }
            }
            lineStart = c == '\n';
        }
        if (dialogue || quotes.Count > 0
            || (sound >= 0 && (integrated < 0 || sound <= integrated))
            || (music >= 0 && (integrated < 0 || music <= integrated))
            || (sound >= 0 && music >= 0 && music <= sound)) return Unsafe(out error);
        int boundary = sound >= 0 ? sound : music;
        result = boundary >= 0 ? prompt.Insert(boundary, direction + "\n\n") : prompt + "\n\n" + direction;
        return true;
    }

    private static char? ClosingQuote(char c)
        => c switch { '"' => '"', '“' => '”', '「' => '」', '『' => '』', _ => null };

    private static bool Unsafe(out string error)
    {
        error = "演技指示を入れる位置を確認できません。セリフの閉じタグ・引用符・映像と音の見出しを確認するか、演技設定を「元の指示を使う」に戻してください。";
        return false;
    }
}
