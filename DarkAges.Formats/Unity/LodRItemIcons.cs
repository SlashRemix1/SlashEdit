using System.Text.RegularExpressions;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Unity;

/// <summary>
/// LoD R <c>Item.bin</c> <c>TileNumber</c> → inventory sprite <c>item###_frame</c> in item000.
/// Tile numbers are 0-based indexes with a <b>fixed stride per sheet number</b>
/// (normally 266 — the full atlas width). Short sheets still reserve a full stride
/// in index space; summing only exported frame counts drifts after item005 and
/// maps later gear (e.g. Oren tunics) onto the wrong icons.
/// </summary>
public static class LodRItemIcons
{
    private static readonly Regex SheetFrame = new(
        @"^item(\d+)_(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Build sheet → frame-count map from an item bundle (e.g. item000).
    /// Returns ordered (sheetNumber, frameCount) pairs.
    /// </summary>
    public static IReadOnlyList<(int Sheet, int FrameCount)> SheetLayout(IArchive archive)
    {
        var maxFrame = new SortedDictionary<int, int>();
        foreach (ArchiveEntry entry in archive.Entries)
        {
            Match m = SheetFrame.Match(entry.DisplayName);
            if (!m.Success)
            {
                m = SheetFrame.Match(Path.GetFileNameWithoutExtension(entry.Name));
            }

            if (!m.Success)
            {
                continue;
            }

            int sheet = int.Parse(m.Groups[1].Value);
            int frame = int.Parse(m.Groups[2].Value);
            if (!maxFrame.TryGetValue(sheet, out int prev) || frame > prev)
            {
                maxFrame[sheet] = frame;
            }
        }

        return maxFrame
            .Select(kv => (kv.Key, FrameCount: kv.Value + 1))
            .ToList();
    }

    /// <summary>
    /// Atlas stride used by <c>TileNumber</c> — max frame count among full sheets.
    /// </summary>
    public static int DetectStride(IReadOnlyList<(int Sheet, int FrameCount)> layout)
    {
        int stride = 0;
        foreach ((_, int frameCount) in layout)
        {
            if (frameCount >= 200 && frameCount > stride)
            {
                stride = frameCount;
            }
        }

        return stride > 0 ? stride : 256;
    }

    /// <summary>Sprite display name for a TileNumber, or null if out of range.</summary>
    public static string? SpriteName(IReadOnlyList<(int Sheet, int FrameCount)> layout, int tileNumber)
    {
        if (tileNumber < 0 || layout.Count == 0)
        {
            return null;
        }

        int stride = DetectStride(layout);
        int sheet = tileNumber / stride + 1;
        int frame = tileNumber % stride;
        string byNumber = $"item{sheet:D3}_{frame}";

        // Sheet exists in the bundle — use fixed-stride addressing even if this
        // frame is past the exported max (caller may fail to find the sprite).
        if (layout.Any(s => s.Sheet == sheet))
        {
            return byNumber;
        }

        // Rare: sheet number gap — fall back to packing only exported frames.
        return SpriteNamePacked(layout, tileNumber);
    }

    /// <summary>Legacy packed addressing (sum of exported frame counts only).</summary>
    public static string? SpriteNamePacked(IReadOnlyList<(int Sheet, int FrameCount)> layout, int tileNumber)
    {
        if (tileNumber < 0 || layout.Count == 0)
        {
            return null;
        }

        int idx = tileNumber;
        foreach ((int sheet, int frameCount) in layout)
        {
            if (idx < frameCount)
            {
                return $"item{sheet:D3}_{idx}";
            }

            idx -= frameCount;
        }

        return null;
    }

    public static string? SpriteName(IArchive archive, int tileNumber) =>
        SpriteName(SheetLayout(archive), tileNumber);

    public static ArchiveEntry? FindSprite(IArchive archive, int tileNumber) =>
        FindSprite(archive, SheetLayout(archive), tileNumber);

    public static ArchiveEntry? FindSprite(
        IArchive archive,
        IReadOnlyList<(int Sheet, int FrameCount)> layout,
        int tileNumber)
    {
        string? name = SpriteName(layout, tileNumber);
        if (name is not null)
        {
            ArchiveEntry? match = archive.Entries.FirstOrDefault(e =>
                e.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Ghost slot on a short sheet — try packed addressing as a last resort.
        string? packed = SpriteNamePacked(layout, tileNumber);
        if (packed is null || packed.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return archive.Entries.FirstOrDefault(e =>
            e.DisplayName.Equals(packed, StringComparison.OrdinalIgnoreCase));
    }
}
