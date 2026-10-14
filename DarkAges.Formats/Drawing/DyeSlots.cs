namespace DarkAges.Formats.Drawing;

/// <summary>
/// Classic DA clothing/hair dye occupies palette indices 96–101. The client recolors those
/// slots from color0.tbl / dye palettes; everything else stays as painted.
/// </summary>
public static class DyeSlots
{
    public const int Start = 96;
    public const int Count = 6;
    public const int End = Start + Count;

    public static readonly (byte R, byte G, byte B)[] DefaultRamp =
    [
        (48, 32, 24),
        (80, 52, 36),
        (120, 80, 52),
        (160, 112, 72),
        (196, 148, 96),
        (228, 188, 128)
    ];

    public static bool Contains(int index) => (uint)(index - Start) < Count;

    public static void ApplyLayout(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        for (int i = 0; i < Count; i++)
        {
            var (r, g, b) = DefaultRamp[i];
            palette.Set(Start + i, r, g, b);
        }
    }

    public static void Copy(Palette source, Palette dest)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        for (int i = Start; i < End; i++)
        {
            var (r, g, b) = source[i];
            dest.Set(i, r, g, b);
        }
    }

    public static int RankByLuminance(Palette palette, int index)
    {
        var ordered = new (int Index, int Lum)[Count];
        for (int i = 0; i < Count; i++)
        {
            var (r, g, b) = palette[Start + i];
            ordered[i] = (Start + i, r + g + b);
        }

        Array.Sort(ordered, (a, b) => a.Lum.CompareTo(b.Lum));
        for (int i = 0; i < Count; i++)
        {
            if (ordered[i].Index == index)
            {
                return i;
            }
        }

        return 0;
    }
}
