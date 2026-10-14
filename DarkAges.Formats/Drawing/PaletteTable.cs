using DarkAges.Formats.Data;

namespace DarkAges.Formats.Drawing;

public sealed class PaletteTable
{
    private readonly List<PaletteTableEntry> _entries = new();

    public IReadOnlyList<PaletteTableEntry> Entries => _entries;

    public void Clear() => _entries.Clear();

    public void SetEntries(IEnumerable<PaletteTableEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        foreach (PaletteTableEntry entry in _entries)
        {
            if (entry.Misc != 0)
            {
                writer.WriteLine($"{entry.Minimum} {entry.Maximum} {entry.PaletteNumber} {entry.Misc}");
            }
            else if (entry.Minimum == entry.Maximum)
            {
                writer.WriteLine($"{entry.Minimum} {entry.PaletteNumber}");
            }
            else
            {
                writer.WriteLine($"{entry.Minimum} {entry.Maximum} {entry.PaletteNumber}");
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void AddTable(ArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int minimum))
            {
                continue;
            }

            int maximum, palette, misc;
            if (parts.Length == 2)
            {
                maximum = minimum;
                if (!int.TryParse(parts[1], out palette))
                {
                    continue;
                }

                misc = 0;
            }
            else if (parts.Length == 3)
            {
                if (!int.TryParse(parts[1], out int second) || !int.TryParse(parts[2], out int third))
                {
                    continue;
                }

                if (third < 0)
                {
                    maximum = minimum;
                    palette = second;
                    misc = third;
                }
                else
                {
                    maximum = second;
                    palette = third;
                    misc = 0;
                }
            }
            else
            {
                if (!int.TryParse(parts[1], out maximum) || !int.TryParse(parts[2], out palette) || !int.TryParse(parts[3], out misc))
                {
                    continue;
                }
            }

            _entries.Add(new PaletteTableEntry(minimum, maximum, palette, misc));
        }
    }

    public int GetPaletteNumber(int index, int misc = 0)
    {
        int palette = 0;
        foreach (PaletteTableEntry entry in _entries)
        {
            if (index >= entry.Minimum && index <= entry.Maximum && (misc == entry.Misc || entry.Misc == 0))
            {
                palette = entry.PaletteNumber;
            }
        }

        return palette;
    }
}

public readonly record struct PaletteTableEntry(int Minimum, int Maximum, int PaletteNumber, int Misc);
