using System.Windows.Media.Imaging;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Maps;
using DarkAges.Viewer.Maps;

namespace DarkAges.Viewer.Preview;

public sealed class PreviewResult
{
    public BitmapSource? Image { get; init; }
    public PixelBuffer? Pixels { get; init; }
    public string Metadata { get; init; } = "";
    public int FrameCount { get; init; } = 1;
    public bool CanAnimate { get; init; }
    public int FrameDelayMs { get; init; }
    public double FramesPerSecond { get; init; }
    public byte[]? Audio { get; init; }
    public string? AudioName { get; init; }
    public BinkClip? Cinematic { get; init; }
    public string? Hex { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string>? TableHeaders { get; init; }
    public IReadOnlyList<string[]>? TableRows { get; init; }
    public MapFile? Map { get; init; }
    public MapAssets.MapView MapView { get; init; }
    public IReadOnlyList<MapWarp> MapExits { get; init; } = [];
    public string? MapKey { get; init; }
}
