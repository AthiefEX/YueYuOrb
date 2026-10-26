using System.Text;

namespace FloatDesk.Core.Translation;

public sealed record TranslationTextChunk(string Text, string SeparatorAfter);

public static class TranslationTextProcessor
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            var clean = line.TrimEnd();
            if (clean.Length > 0 || result.Count == 0 || result[^1].Length > 0)
                result.Add(clean);
        }
        return string.Join("\n", result).Trim();
    }

    public static IReadOnlyList<TranslationTextChunk> Chunk(string text, int maxUtf8Bytes, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        text = Normalize(text);
        var chunks = new List<TranslationTextChunk>();
        var start = 0;
        while (start < text.Length)
        {
            if (chunks.Count > 0 && text.AsSpan(start).StartsWith("\n\n"))
            {
                var previous = chunks[^1];
                chunks[^1] = previous with { SeparatorAfter = previous.SeparatorAfter + "\n\n" };
                start += 2;
                continue;
            }

            var end = start;
            var bytes = 0;
            var characters = 0;
            var paragraphEnd = -1;
            var sentenceEnd = -1;
            foreach (var rune in text.AsSpan(start).EnumerateRunes())
            {
                var width = rune.Utf16SequenceLength;
                var runeBytes = rune.Utf8SequenceLength;
                if (bytes + runeBytes > maxUtf8Bytes || characters + width > maxCharacters) break;
                bytes += runeBytes;
                characters += width;
                end += width;
                if (end >= start + 2 && text[end - 2] == '\n' && text[end - 1] == '\n') paragraphEnd = end;
                if (rune.Value is '.' or '!' or '?' or '。' or '！' or '？') sentenceEnd = end;
            }
            if (end == start) throw new ArgumentException("Limits cannot fit one Unicode code point.");
            if (end < text.Length)
                end = paragraphEnd > start ? paragraphEnd : sentenceEnd > start ? sentenceEnd : end;
            var separator = end >= start + 2 && text.AsSpan(end - 2, 2).SequenceEqual("\n\n") ? "\n\n" : "";
            chunks.Add(new(text[start..(end - separator.Length)], separator));
            start = end;
        }
        return chunks;
    }

    public static string Join(IReadOnlyList<string> chunks, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var separator = targetLanguage is "zh-Hans" or "zh-Hant" or "ja" or "ko" ? "" : " ";
        return string.Join(separator, chunks);
    }

    private static bool IsComparisonCharacter(char c) =>
        !char.IsWhiteSpace(c) && c is not ('.' or '。' or '!' or '！' or '?' or '？' or ',' or '，');

    public static bool IsNearDuplicate(string previous, string current, double threshold = 0.92)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var left = new string(Normalize(previous).Where(IsComparisonCharacter).ToArray());
        var right = new string(Normalize(current).Where(IsComparisonCharacter).ToArray());
        if (left.Length == 0 || right.Length == 0) return left == right;
        var prior = Enumerable.Range(0, right.Length + 1).ToArray();
        var next = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            next[0] = i;
            for (var j = 1; j <= right.Length; j++)
                next[j] = Math.Min(Math.Min(next[j - 1] + 1, prior[j] + 1), prior[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (prior, next) = (next, prior);
        }
        return 1.0 - (double)prior[right.Length] / Math.Max(left.Length, right.Length) >= threshold;
    }
}
