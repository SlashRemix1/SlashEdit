using DarkAges.Formats.Data;

namespace DarkAges.Formats.Catalog;

public static class RelatedFiles
{
    public static IReadOnlyList<ArchiveEntry> Find(IArchive archive, ArchiveEntry current, int max = 40)
    {
        if (!TryParse(current.Name, out string letters, out int number))
        {
            return [current];
        }

        string ext = current.Extension;
        int family = number / 10;
        var matches = new List<(int Number, string Letters, ArchiveEntry Entry)>();
        foreach (ArchiveEntry entry in archive.Entries)
        {
            if (!entry.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParse(entry.Name, out string otherLetters, out int otherNumber))
            {
                continue;
            }

            bool sameFamily = otherLetters.Equals(letters, StringComparison.OrdinalIgnoreCase) && otherNumber / 10 == family;
            bool sameIdDifferentPrefix = otherNumber == number &&
                                         otherLetters.Length > 0 && letters.Length > 0 &&
                                         (otherLetters.StartsWith(letters, StringComparison.OrdinalIgnoreCase) ||
                                          letters.StartsWith(otherLetters, StringComparison.OrdinalIgnoreCase) ||
                                          ShareStem(letters, otherLetters));
            if (sameFamily || sameIdDifferentPrefix)
            {
                matches.Add((otherNumber, otherLetters, entry));
            }
        }

        if (matches.Count == 0)
        {
            return [current];
        }

        return matches
            .OrderBy(m => m.Letters, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Number)
            .Select(m => m.Entry)
            .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public static bool TryParse(string fileName, out string letters, out int number)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int i = 0;
        while (i < stem.Length && char.IsLetter(stem[i]))
        {
            i++;
        }

        letters = i > 0 ? stem[..i] : "";
        string digits = i < stem.Length ? stem[i..] : "";
        while (digits.Length > 0 && !char.IsDigit(digits[^1]))
        {
            digits = digits[..^1];
        }

        return int.TryParse(digits, out number) && letters.Length > 0;
    }

    private static bool ShareStem(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return false;
        }

        string shortStem = a.Length <= b.Length ? a : b;
        string longStem = a.Length <= b.Length ? b : a;
        return longStem.StartsWith(shortStem[..Math.Min(2, shortStem.Length)], StringComparison.OrdinalIgnoreCase);
    }
}
