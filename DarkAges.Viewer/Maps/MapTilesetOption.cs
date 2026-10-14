namespace DarkAges.Viewer.Maps;

public sealed class MapTilesetOption
{
    public static MapTilesetOption Auto { get; } = new("(auto)", null, false);

    public MapTilesetOption(string label, MapAssets? assets, bool snow)
    {
        Label = label;
        Assets = assets;
        Snow = snow;
    }

    public string Label { get; }
    public MapAssets? Assets { get; }
    public bool Snow { get; }

    public static MapTilesetOption Ground(MapAssets assets) =>
        new(assets.Label, assets, false);

    public static MapTilesetOption SnowFloors(MapAssets assets) =>
        new($"{assets.Label}  (snow floors)", assets, true);

    public override string ToString() => Label;
}
