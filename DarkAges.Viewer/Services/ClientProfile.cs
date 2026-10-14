using System.IO;
using System.Text.RegularExpressions;
using DarkAges.Formats.Catalog;

namespace DarkAges.Viewer.Services;

public sealed class ClientProfile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<FavoriteEntry> Favorites { get; set; } = [];
    public List<PaintMacroSlot> PaintMacros { get; set; } = [];
    public Dictionary<int, string> ItemNames { get; set; } = [];
    public string ServerWorldFolder { get; set; } = "";

    public ClientFamily Family => ClientFamilies.FromPath(string.IsNullOrWhiteSpace(Path) ? Name : Path);

    public override string ToString() => Name;

    public static string NameFromPath(string path)
    {
        string name = new DirectoryInfo(path).Name;
        Match version = Regex.Match(name, @"^(\d+)_(\d+)(?:_(.+))?$");
        if (version.Success)
        {
            string label = $"{version.Groups[1].Value}.{version.Groups[2].Value}";
            return version.Groups[3].Success
                ? $"{label} {version.Groups[3].Value.Replace('_', ' ')}"
                : label;
        }

        return name.Replace('_', ' ').Replace('-', ' ');
    }
}
