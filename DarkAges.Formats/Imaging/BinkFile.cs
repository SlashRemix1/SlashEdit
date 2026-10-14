using System.Buffers.Binary;
using System.Text;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Imaging;

/// <summary>
/// Bink container header (BIK / KB2). Matches FFmpeg's libavformat/bink.c layout.
/// Video decode is not implemented here — the viewer uses FFmpeg for frames.
/// </summary>
public readonly struct BinkInfo
{
    public required string Signature { get; init; }
    public required int HeaderSize { get; init; }
    public required int FrameCount { get; init; }
    public required int LargestFrame { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FpsNumerator { get; init; }
    public required int FpsDenominator { get; init; }
    public required int AudioTracks { get; init; }
    public required bool IsBink2 { get; init; }
    public required bool HasSmushPrefix { get; init; }

    public double FramesPerSecond =>
        FpsDenominator <= 0 ? 0 : (double)FpsNumerator / FpsDenominator;

    public int FrameDelayMs
    {
        get
        {
            double fps = FramesPerSecond;
            return fps <= 0 ? 67 : Math.Max(16, (int)Math.Round(1000.0 / fps));
        }
    }

    public int FrameByteCount => Width * Height * 4;

    public string Describe()
    {
        string kind = IsBink2 ? "Bink 2" : "Bink 1";
        string fps = FramesPerSecond <= 0 ? "?" : FramesPerSecond.ToString("0.##");
        string audio = AudioTracks == 0
            ? "no audio"
            : AudioTracks == 1
                ? "1 audio track"
                : $"{AudioTracks} audio tracks";
        return $"{kind} ({Signature})  {Width}×{Height}  {FrameCount} frames  {fps} fps  {audio}";
    }
}

public static class BinkFile
{
    public const int MaxWidth = 7680;
    public const int MaxHeight = 4800;
    public const int MaxFrames = 1_000_000;
    private const int SmushBlock = 512;

    public static bool LooksLike(ReadOnlySpan<byte> data) => TryParse(data, out _);

    public static bool TryParse(ArchiveEntry entry, out BinkInfo info)
    {
        try
        {
            return TryParse(entry.ReadAllBytes(), out info);
        }
        catch
        {
            info = default;
            return false;
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out BinkInfo info)
    {
        info = default;
        int offset = 0;
        bool smush = false;
        if (data.Length >= 4 && AsciiEquals(data, 0, "SMUS"))
        {
            smush = true;
            offset = FindBinkTag(data);
            if (offset < 0)
            {
                return false;
            }
        }

        if (offset + 44 > data.Length || !IsBinkTag(data, offset))
        {
            return false;
        }

        string signature = Encoding.ASCII.GetString(data.Slice(offset, 4));
        bool bink2 = data[offset] == (byte)'K';
        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8, 4));
        uint largest = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12, 4));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 20, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 24, 4));
        uint fpsNum = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 28, 4));
        uint fpsDen = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 32, 4));
        uint audio = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 40, 4));

        if (frames == 0 || frames > MaxFrames
            || width == 0 || width > MaxWidth
            || height == 0 || height > MaxHeight
            || fpsNum == 0 || fpsDen == 0
            || audio > 256)
        {
            return false;
        }

        if (largest > storedSize + 8 && storedSize > 0)
        {
            return false;
        }

        info = new BinkInfo
        {
            Signature = signature,
            HeaderSize = offset + 44,
            FrameCount = (int)frames,
            LargestFrame = (int)Math.Min(largest, int.MaxValue),
            Width = (int)width,
            Height = (int)height,
            FpsNumerator = (int)fpsNum,
            FpsDenominator = (int)fpsDen,
            AudioTracks = (int)audio,
            IsBink2 = bink2,
            HasSmushPrefix = smush
        };
        return true;
    }

    private static int FindBinkTag(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 44 <= data.Length; i += SmushBlock)
        {
            if (IsBinkTag(data, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsBinkTag(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 4 > data.Length)
        {
            return false;
        }

        byte a = data[offset];
        byte b = data[offset + 1];
        byte c = data[offset + 2];
        byte d = data[offset + 3];
        if (a == (byte)'B' && b == (byte)'I' && c == (byte)'K')
        {
            return d is (byte)'b' or (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'k';
        }

        if (a == (byte)'K' && b == (byte)'B' && c == (byte)'2')
        {
            return d is (byte)'a' or (byte)'d' or (byte)'f' or (byte)'g' or (byte)'h'
                or (byte)'i' or (byte)'j' or (byte)'k';
        }

        return false;
    }

    private static bool AsciiEquals(ReadOnlySpan<byte> data, int offset, string text)
    {
        if (offset + text.Length > data.Length)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (data[offset + i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }
}
