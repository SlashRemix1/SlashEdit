using DarkAges.Formats.Maps;

namespace DarkAges.Viewer.Maps;

public readonly record struct MapSnapshot(
    int Width,
    int Height,
    MapSizeHeader Header,
    MapTile[] Tiles,
    string Name,
    byte[]? Light = null,
    byte[]? Effect = null);

public sealed class MapUndoHistory
{
    private readonly List<MapSnapshot> _undo = [];
    private readonly List<MapSnapshot> _redo = [];
    private readonly int _max;

    public MapUndoHistory(int max = 100) => _max = Math.Max(1, max);

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public MapSnapshot Capture(MapFile map) =>
        new(map.Width, map.Height, map.SizeHeader, (MapTile[])map.Tiles.Clone(), map.Name);

    public void Restore(MapFile map, MapSnapshot snap)
    {
        map.Name = snap.Name;
        map.LoadState(snap.Width, snap.Height, (MapTile[])snap.Tiles.Clone(), snap.Header);
    }

    public void Commit(MapSnapshot before)
    {
        _undo.Add(before);
        if (_undo.Count > _max)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }

    public bool TryUndo(MapFile map, MapSnapshot current, out MapSnapshot applied)
    {
        applied = default;
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add(current);
        applied = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(map, applied);
        return true;
    }

    public bool TryRedo(MapFile map, MapSnapshot current, out MapSnapshot applied)
    {
        applied = default;
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add(current);
        applied = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(map, applied);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
