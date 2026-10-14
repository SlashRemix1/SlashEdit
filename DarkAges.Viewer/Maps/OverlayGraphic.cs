using System.IO;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Drawing;
using DarkAges.Formats.Imaging;

namespace DarkAges.Viewer.Maps;

public static class OverlayGraphic
{
    public static bool IsOverlaySource(string name)
    {
        string ext = Path.GetExtension(name);
        return ext.Equals(".efa", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".epf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hea", StringComparison.OrdinalIgnoreCase);
    }

    public static PixelBuffer? TryRender(IArchive archive, ArchiveEntry entry)
    {
        try
        {
            string ext = entry.Extension;
            if (ext.Equals(".efa", StringComparison.OrdinalIgnoreCase))
            {
                var efa = new EfaFile(entry, maxFrames: 1);
                return efa.Frames.Count > 0 ? efa.Frames[0].Render(efa.Blend) : null;
            }

            if (ext.Equals(".epf", StringComparison.OrdinalIgnoreCase))
            {
                var epf = new EpfFile(entry, 0, 1);
                if (epf.Frames.Count == 0)
                {
                    return null;
                }

                Palette pal = PaletteLocator.ForEntry(archive, entry)
                    ?? PaletteLocator.Find(archive)
                    ?? new Palette();
                return epf.Frames[0].Render(pal, PaletteLocator.BlitFor(archive, entry));
            }

            if (ext.Equals(".hea", StringComparison.OrdinalIgnoreCase))
            {
                return new HeaFile(entry).TryRender();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
