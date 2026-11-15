namespace DarkAges.Formats.Unity;

public readonly record struct UnitySpriteClip(
    string Id,
    bool Effect,
    IReadOnlyList<UnitySpriteFrame> Frames);

public readonly record struct UnitySpriteFrame(
    string EntryName,
    int Index,
    float PivotX,
    float PivotY);
