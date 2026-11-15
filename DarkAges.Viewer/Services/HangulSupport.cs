using Media = System.Windows.Media;

namespace DarkAges.Viewer.Services;

/// <summary>
/// Detects whether this PC can render Hangul. Unicode Korean works without a
/// Korean language pack, but glyphs need a font such as Malgun Gothic
/// (often via Optional features → Korean Supplemental Fonts).
/// </summary>
public static class HangulSupport
{
    private static readonly int[] ProbeCodePoints =
    [
        0xAC00, // 가
        0xD55C, // 한
        0xAE00, // 글
        0xB9AC, // 리
    ];

    private static readonly string[] PreferredFamilies =
    [
        "Malgun Gothic",
        "Malgun Gothic Semilight",
        "Gulim",
        "Dotum",
        "Batang",
        "Gungsuh",
        "Noto Sans KR",
        "Noto Sans CJK KR",
        "Apple SD Gothic Neo",
        "Source Han Sans KR",
    ];

    private static bool? _canRender;
    private static string? _fontName;

    public static bool CanRenderHangul
    {
        get
        {
            _canRender ??= Probe(out _fontName);
            return _canRender.Value;
        }
    }

    public static string? AvailableFontName
    {
        get
        {
            _ = CanRenderHangul;
            return _fontName;
        }
    }

    public static string InstallHint =>
        "Korean text needs a Hangul font (for example Malgun Gothic). " +
        "On Windows: Settings → Apps → Optional features → Add → “Korean Supplemental Fonts”, " +
        "or add Korean under Language & region. Restart SlashEdit after installing.";

    public static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (IsHangul(c))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsHangul(char c) =>
        c is (>= '\uAC00' and <= '\uD7A3') // syllables
            or (>= '\u1100' and <= '\u11FF') // jamo
            or (>= '\u3130' and <= '\u318F'); // compatibility jamo

    public static void Refresh()
    {
        _canRender = null;
        _fontName = null;
    }

    private static bool Probe(out string? fontName)
    {
        fontName = null;
        try
        {
            // Only probe known Korean UI fonts — enumerating every SystemFontFamily
            // freezes WPF for seconds on first search.
            foreach (string preferred in PreferredFamilies)
            {
                if (FamilyHasHangul(new Media.FontFamily(preferred)))
                {
                    fontName = preferred;
                    return true;
                }
            }

            // Cheap Segoe UI check (often has limited CJK fallback, not full Hangul).
            if (FamilyHasHangul(new Media.FontFamily("Segoe UI")))
            {
                fontName = "Segoe UI";
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool FamilyHasHangul(Media.FontFamily family)
    {
        try
        {
            foreach (Media.Typeface typeface in family.GetTypefaces())
            {
                if (!typeface.TryGetGlyphTypeface(out Media.GlyphTypeface? glyph) || glyph is null)
                {
                    continue;
                }

                IDictionary<int, ushort> map = glyph.CharacterToGlyphMap;
                bool all = true;
                foreach (int cp in ProbeCodePoints)
                {
                    if (!map.ContainsKey(cp))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
