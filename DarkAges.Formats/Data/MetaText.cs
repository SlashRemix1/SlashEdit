using System.Text;

namespace DarkAges.Formats.Data;

public static class MetaText
{
    static MetaText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Latin = Encoding.GetEncoding(1252);
        Korean = Encoding.GetEncoding(949);
        Japanese = Encoding.GetEncoding(932);
    }

    public static Encoding Latin { get; }
    public static Encoding Korean { get; }
    public static Encoding Japanese { get; }

    public static Encoding ForPath(string? path)
    {
        string p = path ?? "";
        if (LooksJapanese(p))
        {
            return Japanese;
        }

        if (LooksKorean(p))
        {
            return Korean;
        }

        return Latin;
    }

    public static string Decode(byte[] bytes, string? pathHint)
    {
        Encoding hinted = ForPath(pathHint);
        if (!ReferenceEquals(hinted, Latin) || !HasHighBytes(bytes))
        {
            return hinted.GetString(bytes);
        }

        return Best(bytes).GetString(bytes);
    }

    public static Encoding Best(byte[] bytes)
    {
        if (!HasHighBytes(bytes))
        {
            return Latin;
        }

        Encoding best = Latin;
        int bestScore = Score(Latin, bytes);
        foreach (Encoding encoding in new[] { Korean, Japanese })
        {
            int score = Score(encoding, bytes);
            if (score > bestScore)
            {
                bestScore = score;
                best = encoding;
            }
        }

        return best;
    }

    public static bool LooksKorean(string path) =>
        path.Contains("Darkness", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Korean", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Korea", StringComparison.OrdinalIgnoreCase);

    public static bool LooksJapanese(string path) =>
        path.Contains("Yuki", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Japan", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Japanese", StringComparison.OrdinalIgnoreCase)
        || path.Contains("_JP", StringComparison.OrdinalIgnoreCase);

    private static bool HasHighBytes(byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static int Score(Encoding encoding, byte[] bytes)
    {
        string text = encoding.GetString(bytes);
        int score = 0;
        foreach (char c in text)
        {
            if (c == '\uFFFD' || c < ' ' && c is not '\t' and not '\n' and not '\r')
            {
                score -= 3;
                continue;
            }

            if (c is >= '\uAC00' and <= '\uD7AF')
            {
                score += 40;
            }
            else if (c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF')
            {
                score += 20;
            }
            else if (c >= ' ')
            {
                score++;
            }
        }

        return score;
    }
}
