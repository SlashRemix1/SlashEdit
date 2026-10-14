using System.IO;
using System.Text.Json;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Maps;

namespace DarkAges.Viewer.Services;

public sealed class StructurePresetStore
{
    public List<string> HiddenKeys { get; set; } = [];
    public List<StoredStructureStamp> Custom { get; set; } = [];

    private static string PathName =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkAgesViewer", "structure-presets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static StructurePresetStore Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                return JsonSerializer.Deserialize<StructurePresetStore>(File.ReadAllText(PathName), JsonOptions)
                    ?? new StructurePresetStore();
            }
        }
        catch
        {
            // ignore corrupt store
        }

        return new StructurePresetStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            HiddenKeys = HiddenKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Custom = Custom.Where(s => s.IsValid()).ToList();
            File.WriteAllText(PathName, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // ignore
        }
    }

    public IReadOnlyList<StructureStamp> CustomStamps()
    {
        var list = new List<StructureStamp>();
        foreach (StoredStructureStamp stored in Custom)
        {
            if (stored.TryToStamp(out StructureStamp stamp))
            {
                list.Add(stamp);
            }
        }

        return list;
    }

    public void AddOrUpdate(StructureStamp stamp)
    {
        HiddenKeys.RemoveAll(k => string.Equals(k, stamp.Key, StringComparison.Ordinal));
        Custom.RemoveAll(c => string.Equals(c.Key, stamp.Key, StringComparison.Ordinal));
        Custom.Insert(0, StoredStructureStamp.From(stamp));
        Save();
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        Custom.RemoveAll(c => string.Equals(c.Key, key, StringComparison.Ordinal));
        if (!HiddenKeys.Contains(key, StringComparer.Ordinal))
        {
            HiddenKeys.Add(key);
        }

        Save();
    }
}

public sealed class StoredStructureStamp
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Uses { get; set; }
    public bool CopyFloors { get; set; } = true;
    public string Category { get; set; } = "";
    public List<StoredMapCell> Cells { get; set; } = [];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Key)
        && Width > 0
        && Height > 0
        && Cells.Count == Width * Height;

    public bool TryToStamp(out StructureStamp stamp)
    {
        if (!IsValid())
        {
            stamp = default;
            return false;
        }

        var tiles = new MapTile[Cells.Count];
        for (int i = 0; i < Cells.Count; i++)
        {
            StoredMapCell cell = Cells[i];
            tiles[i] = new MapTile(cell.Floor, cell.Left, cell.Right);
        }

        stamp = new StructureStamp(
            Key,
            string.IsNullOrWhiteSpace(Label) ? Key : Label,
            Width,
            Height,
            tiles,
            Math.Max(1, Uses),
            CopyFloors,
            string.IsNullOrWhiteSpace(Category)
                ? StampCategories.Classify(tiles, Width, Height)
                : StampCategories.Parse(Category));
        return true;
    }

    public static StoredStructureStamp From(StructureStamp stamp) => new()
    {
        Key = stamp.Key,
        Label = stamp.Label,
        Width = stamp.Width,
        Height = stamp.Height,
        Uses = Math.Max(1, stamp.Uses),
        CopyFloors = stamp.CopyFloors,
        Category = stamp.Category.ToString(),
        Cells = stamp.Cells.Select(c => new StoredMapCell
        {
            Floor = c.Floor,
            Left = c.LeftWall,
            Right = c.RightWall
        }).ToList()
    };
}

public sealed class StoredMapCell
{
    public ushort Floor { get; set; }
    public ushort Left { get; set; }
    public ushort Right { get; set; }
}
