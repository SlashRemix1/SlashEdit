namespace DarkAges.Viewer.Services;

public sealed class PaintMacroSlot
{
    public int Key { get; set; }
    public string Kind { get; set; } = "Floor";
    public int Id { get; set; }
    public string StampKey { get; set; } = "";
    public string StampCategory { get; set; } = "";
    public string Label { get; set; } = "";
}
