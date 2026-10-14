using System.IO;
using System.Windows.Media.Imaging;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;
using DarkAges.Viewer.Imaging;

namespace DarkAges.Viewer.Preview;

public sealed class MonsterColorVariant
{
    public MonsterColorVariant(
        string caption,
        BitmapSource image,
        string? paletteName = null,
        int paletteNumber = 0,
        bool embedded = false,
        string? siblingFile = null)
    {
        Caption = caption;
        Image = image;
        PaletteName = paletteName;
        PaletteNumber = paletteNumber;
        Embedded = embedded;
        SiblingFile = siblingFile;
    }

    public string Caption { get; }
    public BitmapSource Image { get; }
    public string? PaletteName { get; }
    public int PaletteNumber { get; }
    public bool Embedded { get; }
    public string? SiblingFile { get; }
    public bool IsSibling => SiblingFile is not null;
}

public static class MonsterColors
{
    public static IReadOnlyList<MonsterColorVariant> For(IArchive archive, ArchiveEntry entry)
    {
        try
        {
            var mpf = new MpfFile(entry, maxFrames: 1);
            return For(archive, entry, mpf);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<MonsterColorVariant> For(IArchive archive, ArchiveEntry entry, MpfFile mpf)
    {
        var list = new List<MonsterColorVariant>();
        HashSet<int> baked = mpf.PaletteNumbers.Count > 0
            ? mpf.PaletteNumbers.ToHashSet()
            : [mpf.PaletteNumber];

        if (mpf.Frames.Count > 0)
        {
            int idle = mpf.StopFrameCount > 0
                ? Math.Clamp(mpf.StopFrameIndex, 0, mpf.Frames.Count - 1)
                : 0;
            MpfFrame frame = mpf.Frames[idle];
            foreach (ArchiveEntry palEntry in PaletteLocator.Palettes(archive))
            {
                if (!palEntry.Name.StartsWith("mns", StringComparison.OrdinalIgnoreCase)
                    || !palEntry.Extension.Equals(".pal", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RelatedFiles.TryParse(palEntry.Name, out _, out int number);
                Palette? pal = PaletteLocator.Named(archive, palEntry.Name);
                if (pal is null)
                {
                    continue;
                }

                bool embedded = baked.Contains(number);
                list.Add(RenderSwap(
                    palEntry.Name,
                    pal,
                    mpf,
                    frame,
                    number,
                    embedded));
            }
        }

        RelatedFiles.TryParse(entry.Name, out string letters, out int fileNumber);
        foreach (ArchiveEntry sibling in archive.Entries)
        {
            if (sibling.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                || !sibling.Extension.Equals(".mpf", StringComparison.OrdinalIgnoreCase)
                || !sibling.Name.StartsWith("mns", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!RelatedFiles.TryParse(sibling.Name, out string otherLetters, out int otherNumber)
                || otherNumber != fileNumber
                || !otherLetters.Equals(letters, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(entry.Name);
            string otherStem = Path.GetFileNameWithoutExtension(sibling.Name);
            if (stem.Equals(otherStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var other = new MpfFile(sibling, maxFrames: 1);
                if (other.Frames.Count == 0)
                {
                    continue;
                }

                int idle = other.StopFrameCount > 0
                    ? Math.Clamp(other.StopFrameIndex, 0, other.Frames.Count - 1)
                    : 0;
                Palette pal = PaletteLocator.FindNumbered(archive, "mns", other.PaletteNumber)
                    ?? PaletteLocator.ForEntry(archive, sibling)
                    ?? new Palette(new MemoryStream(new byte[768]));
                list.Add(RenderSwap(
                    sibling.Name,
                    pal,
                    other,
                    other.Frames[idle],
                    other.PaletteNumber,
                    embedded: false,
                    siblingFile: sibling.Name));
            }
            catch
            {
                // skip unreadable color-file sibling
            }
        }

        return list
            .OrderByDescending(v => v.Embedded)
            .ThenBy(v => v.IsSibling)
            .ThenBy(v => v.PaletteNumber)
            .ThenBy(v => v.Caption, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
    }

    private static MonsterColorVariant RenderSwap(
        string caption,
        Palette pal,
        MpfFile mpf,
        MpfFrame frame,
        int paletteNumber,
        bool embedded,
        string? siblingFile = null)
    {
        PixelBuffer rendered = frame.Render(pal);
        int w = Math.Max(1, Math.Max(mpf.Width, frame.Left + rendered.Width));
        int h = Math.Max(1, Math.Max(mpf.Height, frame.Top + rendered.Height));
        var canvas = new PixelBuffer(w, h);
        canvas.Blit(rendered, frame.Left, frame.Top);
        BitmapSource bmp = WpfImages.Checkerboard(56, 56, canvas.Contain(56, 56));
        string label = siblingFile is not null
            ? siblingFile
            : embedded
                ? $"{caption} ★"
                : caption;
        return new MonsterColorVariant(
            label,
            bmp,
            siblingFile is null ? caption : null,
            paletteNumber,
            embedded,
            siblingFile);
    }
}
