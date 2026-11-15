using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Unity;

public sealed class UnityObjectPreview
{
    public PixelBuffer? Pixels { get; init; }
    public byte[]? Audio { get; init; }
    public string? Text { get; init; }
    public string Metadata { get; init; } = "";
    public string? Error { get; init; }
    public int FrameCount { get; init; } = 1;
    public bool CanAnimate { get; init; }
    public bool AutoPlay { get; init; }
    public string? ObjectName { get; init; }
    public IReadOnlyList<PixelBuffer>? Frames { get; init; }
}
