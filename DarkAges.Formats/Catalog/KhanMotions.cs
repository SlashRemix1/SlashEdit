namespace DarkAges.Formats.Catalog;

public readonly record struct KhanMotion(int Number, int FileNumber, int StartIndex, int FrameCount)
{
    public override string ToString() => $"Motion {Number}";
}

public static class KhanMotions
{
    public static IReadOnlyList<KhanMotion> Presets { get; } =
    [
        new(1, 0, 0, 2),
        new(6, 0, 0, 1),
        new(128, 1, 0, 3),
        new(129, 2, 0, 4),
        new(130, 2, 8, 3),
        new(131, 3, 0, 3),
        new(132, 3, 6, 2),
        new(133, 3, 10, 4),
        new(134, 4, 0, 2),
        new(135, 4, 4, 2),
        new(136, 5, 0, 2),
        new(137, 1, 6, 3),
        new(138, 1, 12, 1),
        new(139, 2, 14, 2),
        new(140, 2, 18, 3),
        new(141, 2, 24, 3),
        new(142, 4, 8, 4),
        new(143, 4, 16, 6),
        new(144, 4, 28, 4),
        new(145, 5, 4, 4)
    ];

    public static string BodyFile(bool male, KhanMotion motion)
    {
        string prefix = male ? "mb001" : "wb001";
        if (motion.Number == 1)
        {
            return $"{prefix}02.epf";
        }

        if (motion.Number == 6)
        {
            return $"{prefix}03.epf";
        }

        return $"{prefix}{(char)('a' + motion.FileNumber)}.epf";
    }

    public static string IdleFile(bool male) => male ? "mb00101.epf" : "wb00101.epf";

    public static string FileSuffix(KhanMotion motion)
    {
        if (motion.Number == 1)
        {
            return "02";
        }

        if (motion.Number == 6)
        {
            return "03";
        }

        return ((char)('a' + motion.FileNumber)).ToString();
    }
}
