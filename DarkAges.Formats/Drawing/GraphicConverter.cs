using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Drawing;

public enum GraphicTarget
{
    Epf,
    Mpf,
    Hpf,
    Efa,
    SpfPalettized,
    SpfColorized,
    Pcx,
    Hea,
    Upf,
    Fnt
}

public readonly record struct GraphicConvertResult(
    byte[] Data,
    byte[]? PaletteBytes,
    string Extension,
    int Width,
    int Height,
    GraphicTarget Target);

public static class GraphicConverter
{
    public static GraphicConvertResult Convert(
        PixelBuffer pixels,
        GraphicTarget target,
        Palette? palette = null,
        bool dither = false,
        int paletteNumber = 0,
        short originX = EffectCenterTable.DefaultX,
        short originY = EffectCenterTable.DefaultY,
        EfaBlend blend = EfaBlend.Additive)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Palette built = palette ?? ColorQuantizer.Build(pixels);
        byte[] indexed = target == GraphicTarget.SpfColorized || target == GraphicTarget.Efa
            ? []
            : ColorQuantizer.Index(pixels, built, dither);
        byte[] palBytes = built.ToBytes();

        return target switch
        {
            GraphicTarget.Hpf => ConvertHpf(pixels, built, dither, palBytes),
            GraphicTarget.Mpf => new GraphicConvertResult(
                MpfWriter.Write(
                    pixels.Width,
                    pixels.Height,
                    paletteNumber,
                    [new MpfFrame(0, 0, pixels.Height, pixels.Width, 0, 0, indexed)],
                    stopFrameCount: 1),
                palBytes,
                ".mpf",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.Efa => new GraphicConvertResult(
                EfaWriter.FromPixels(pixels, originX, originY, blend),
                null,
                ".efa",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.SpfPalettized => new GraphicConvertResult(
                SpfWriter.WritePalettized(built, [new SpfFrame(0, 0, pixels.Width, pixels.Height, indexed)]),
                palBytes,
                ".spf",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.SpfColorized => new GraphicConvertResult(
                SpfWriter.WriteColorized([new SpfFrame(0, 0, pixels.Width, pixels.Height, [], pixels)]),
                null,
                ".spf",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.Pcx => new GraphicConvertResult(
                PcxWriter.Write(pixels, built, dither),
                palBytes,
                ".pcx",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.Hea => new GraphicConvertResult(
                HeaWriter.WriteRgb565(pixels),
                null,
                ".hea",
                pixels.Width,
                pixels.Height,
                target),
            GraphicTarget.Upf => new GraphicConvertResult(
                UpfFile.FromPixels(pixels, built).ToBytes(),
                palBytes,
                ".upf",
                16,
                16,
                target),
            GraphicTarget.Fnt => ConvertFnt(pixels),
            _ => new GraphicConvertResult(
                EpfWriter.Write(pixels.Width, pixels.Height, [new EpfFrame(0, 0, pixels.Height, pixels.Width, indexed)]),
                palBytes,
                ".epf",
                pixels.Width,
                pixels.Height,
                target)
        };
    }

    public static string Extension(GraphicTarget target) => target switch
    {
        GraphicTarget.Hpf => ".hpf",
        GraphicTarget.Mpf => ".mpf",
        GraphicTarget.Efa => ".efa",
        GraphicTarget.SpfPalettized or GraphicTarget.SpfColorized => ".spf",
        GraphicTarget.Pcx => ".pcx",
        GraphicTarget.Hea => ".hea",
        GraphicTarget.Upf => ".upf",
        GraphicTarget.Fnt => ".fnt",
        _ => ".epf"
    };

    public static GraphicTarget FromExtension(string? extension, bool colorizedSpf = false)
    {
        string ext = extension ?? "";
        if (ext.Equals(".mpf", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Mpf;
        }

        if (ext.Equals(".hpf", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Hpf;
        }

        if (ext.Equals(".efa", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Efa;
        }

        if (ext.Equals(".spf", StringComparison.OrdinalIgnoreCase))
        {
            return colorizedSpf ? GraphicTarget.SpfColorized : GraphicTarget.SpfPalettized;
        }

        if (ext.Equals(".pcx", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Pcx;
        }

        if (ext.Equals(".hea", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Hea;
        }

        if (ext.Equals(".upf", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Upf;
        }

        if (ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicTarget.Fnt;
        }

        return GraphicTarget.Epf;
    }

    private static GraphicConvertResult ConvertHpf(PixelBuffer pixels, Palette palette, bool dither, byte[] palBytes)
    {
        var fitted = new PixelBuffer(HpfWriter.TileWidth, Math.Max(1, pixels.Height));
        int dx = Math.Max(0, (HpfWriter.TileWidth - pixels.Width) / 2);
        fitted.Blit(pixels, dx, 0);
        byte[] indexed = ColorQuantizer.Index(fitted, palette, dither);
        return new GraphicConvertResult(
            HpfWriter.Write(new byte[8], indexed),
            palBytes,
            ".hpf",
            HpfWriter.TileWidth,
            fitted.Height,
            GraphicTarget.Hpf);
    }

    private static GraphicConvertResult ConvertFnt(PixelBuffer pixels)
    {
        var glyphs = new byte[FntFile.EnglishGlyphCount][];
        glyphs[0] = FntFile.Pack(pixels, FntFile.EnglishGlyphWidth, FntFile.EnglishGlyphHeight);
        for (int i = 1; i < glyphs.Length; i++)
        {
            glyphs[i] = new byte[FntFile.EnglishGlyphHeight];
        }

        return new GraphicConvertResult(
            new FntFile(FntKind.English, glyphs).ToBytes(),
            null,
            ".fnt",
            FntFile.EnglishGlyphWidth,
            FntFile.EnglishGlyphHeight,
            GraphicTarget.Fnt);
    }
}
