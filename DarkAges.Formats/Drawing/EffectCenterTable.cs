using System.Globalization;
using System.Text;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Drawing;

/// <summary>
/// Per-frame effect origin. Missing or empty tables default to (28, 70), the stock iso foot.
/// </summary>
public sealed class EffectCenterTable
{
    public const short DefaultX = 28;
    public const short DefaultY = 70;

    private readonly List<(short X, short Y)> _points = [];

    public IReadOnlyList<(short X, short Y)> Points => _points;

    public static bool IsCenterTableName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        return ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase)
            && stem.StartsWith("efct", StringComparison.OrdinalIgnoreCase);
    }

    public static EffectCenterTable Load(byte[] data, int frameCount)
    {
        var table = new EffectCenterTable();
        table.Read(data);
        table.EnsureCount(frameCount);
        return table;
    }

    public static EffectCenterTable Load(ArchiveEntry entry, int frameCount)
    {
        try
        {
            return Load(entry.ReadAllBytes(), frameCount);
        }
        catch
        {
            return ForFrames(frameCount);
        }
    }

    public static EffectCenterTable ForFrames(int frameCount)
    {
        var table = new EffectCenterTable();
        table.EnsureCount(frameCount);
        return table;
    }

    public (short X, short Y) this[int index] =>
        _points.Count == 0 ? (DefaultX, DefaultY) : _points[Math.Clamp(index, 0, _points.Count - 1)];

    public void Set(int index, short x, short y)
    {
        EnsureCount(index + 1);
        _points[index] = (x, y);
    }

    public void EnsureCount(int frameCount)
    {
        frameCount = Math.Max(0, frameCount);
        while (_points.Count < frameCount)
        {
            _points.Add((DefaultX, DefaultY));
        }
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach ((short x, short y) in _points)
        {
            sb.Append(x.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.AppendLine(y.ToString(CultureInfo.InvariantCulture));
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private void Read(byte[] data)
    {
        _points.Clear();
        if (data.Length == 0)
        {
            return;
        }

        if (LooksLikeText(data) && TryReadText(data))
        {
            return;
        }

        int pairs = data.Length / 4;
        for (int i = 0; i < pairs; i++)
        {
            short x = BitConverter.ToInt16(data, i * 4);
            short y = BitConverter.ToInt16(data, i * 4 + 2);
            if (x is < -512 or > 1024 || y is < -512 or > 1024)
            {
                _points.Clear();
                return;
            }

            _points.Add((x, y));
        }
    }

    private bool TryReadText(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data);
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            int skip = parts.Length >= 3 && int.TryParse(parts[0], out _) ? 1 : 0;
            if (!short.TryParse(parts[skip], NumberStyles.Integer, CultureInfo.InvariantCulture, out short x)
                || !short.TryParse(parts[skip + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out short y))
            {
                return false;
            }

            _points.Add((x, y));
        }

        return _points.Count > 0;
    }

    private static bool LooksLikeText(byte[] data)
    {
        int n = Math.Min(data.Length, 64);
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            if (b is < 9 or (> 13 and < 32) || b > 126)
            {
                return false;
            }
        }

        return true;
    }
}
