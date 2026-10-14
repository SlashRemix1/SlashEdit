namespace DarkAges.Formats.Catalog;

public enum ClientFamily
{
    Unknown,
    Classic3,
    Classic7,
    ClassicNa,
    Fragments,
    LegendOfDarkness,
    Yuki,
    AoC
}

public static class ClientFamilies
{
    public static ClientFamily FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ClientFamily.Unknown;
        }

        string text = path.Replace('/', '\\');
        if (Contains(text, "Yuki", "Japan")
            || text.Contains("_JP", StringComparison.OrdinalIgnoreCase))
        {
            return ClientFamily.Yuki;
        }

        if (Contains(text, "Fragments_Of_Temuair", "Fragments of Temuair", "Fragments"))
        {
            return ClientFamily.Fragments;
        }

        if (Contains(text, "Legends_AoC", "Legends AoC", "legends-aoc"))
        {
            return ClientFamily.AoC;
        }

        if (Contains(text, "Legend of Darkness", "Legend_of_Darkness", "LegendOfDarkness"))
        {
            return ClientFamily.LegendOfDarkness;
        }

        if (Contains(text, "7_41", "7.41") || HasVersionFolder(text, '7'))
        {
            return ClientFamily.Classic7;
        }

        if (Contains(text, "3_61", "3.61") || HasVersionFolder(text, '3'))
        {
            return ClientFamily.Classic3;
        }

        if (Contains(text, "4_52", "4.52", "5_21", "5.21", "5_51", "5.51", "orphelias", "Orphelias")
            || HasVersionFolder(text, '4')
            || HasVersionFolder(text, '5'))
        {
            return ClientFamily.ClassicNa;
        }

        return ClientFamily.Unknown;
    }

    public static bool IsGuarded(ClientFamily family) => family != ClientFamily.Yuki;

    public static bool IsClassicDarkAges(ClientFamily family) =>
        family is ClientFamily.Classic3 or ClientFamily.Classic7 or ClientFamily.ClassicNa;

    public static string Label(ClientFamily family) => family switch
    {
        ClientFamily.Classic3 => "3.x Dark Ages",
        ClientFamily.Classic7 => "7.x Dark Ages",
        ClientFamily.ClassicNa => "classic Dark Ages",
        ClientFamily.Fragments => "Fragments of Temuair",
        ClientFamily.LegendOfDarkness => "Legend of Darkness",
        ClientFamily.Yuki => "Yuki (JP)",
        ClientFamily.AoC => "Legends AoC",
        _ => "this client"
    };

    private static bool Contains(string text, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVersionFolder(string text, char major)
    {
        string needle = "\\" + major + "_";
        return text.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(major + "_", StringComparison.OrdinalIgnoreCase);
    }
}
