namespace DarkAges.Formats.IO;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer OrdinalIgnoreCase { get; } = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i];
            char cb = b[j];
            bool da = char.IsDigit(ca);
            bool db = char.IsDigit(cb);
            if (da && db)
            {
                int ie = i;
                while (ie < a.Length && char.IsDigit(a[ie]))
                {
                    ie++;
                }

                int je = j;
                while (je < b.Length && char.IsDigit(b[je]))
                {
                    je++;
                }

                int cmp = CompareNumber(a.AsSpan(i, ie - i), b.AsSpan(j, je - j));
                if (cmp != 0)
                {
                    return cmp;
                }

                i = ie;
                j = je;
                continue;
            }

            int letter = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (letter != 0)
            {
                return letter;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static int CompareNumber(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int ia = 0;
        int ib = 0;
        while (ia < a.Length - 1 && a[ia] == '0')
        {
            ia++;
        }

        while (ib < b.Length - 1 && b[ib] == '0')
        {
            ib++;
        }

        int la = a.Length - ia;
        int lb = b.Length - ib;
        if (la != lb)
        {
            return la.CompareTo(lb);
        }

        int cmp = a[ia..].CompareTo(b[ib..], StringComparison.Ordinal);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    }
}
