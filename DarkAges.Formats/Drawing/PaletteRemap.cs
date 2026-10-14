namespace DarkAges.Formats.Drawing;

public static class PaletteRemap
{
    /// <summary>
    /// Map each source index to the nearest destination color. Dye-slot pixels keep their
    /// rank in 96–101 so clothing still dyes after the remap.
    /// </summary>
    public static byte[] BuildLookup(Palette source, Palette dest, bool preserveDyeSlots = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        var lookup = new byte[256];
        for (int i = 1; i < 256; i++)
        {
            if (preserveDyeSlots && DyeSlots.Contains(i))
            {
                int rank = DyeSlots.RankByLuminance(source, i);
                lookup[i] = (byte)(DyeSlots.Start + rank);
                continue;
            }

            var (r, g, b) = source[i];
            lookup[i] = dest.NearestIndex(r, g, b, 255, preserveDyeSlots ? DyeSlots.Start : -1, DyeSlots.Count);
        }

        return lookup;
    }

    public static byte[] Apply(byte[] indexed, byte[] lookup)
    {
        ArgumentNullException.ThrowIfNull(indexed);
        ArgumentNullException.ThrowIfNull(lookup);
        var data = new byte[indexed.Length];
        for (int i = 0; i < indexed.Length; i++)
        {
            byte src = indexed[i];
            data[i] = src < lookup.Length ? lookup[src] : src;
        }

        return data;
    }

    public static byte[] Remap(byte[] indexed, Palette source, Palette dest, bool preserveDyeSlots = true) =>
        Apply(indexed, BuildLookup(source, dest, preserveDyeSlots));
}
