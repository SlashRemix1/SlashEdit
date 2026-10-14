using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Viewer.Preview;

public sealed class BinkClip : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _video;

    public required BinkInfo Info { get; init; }
    public required string RawPath { get; init; }
    public required int DecodedFrameCount { get; init; }
    public byte[]? Wav { get; init; }

    public int Width => Info.Width;
    public int Height => Info.Height;
    public int FrameCount => Math.Max(1, DecodedFrameCount);
    public double FramesPerSecond => Info.FramesPerSecond;
    public int FrameDelayMs => Info.FrameDelayMs;

    public PixelBuffer ReadFrame(int index)
    {
        var buffer = new PixelBuffer(Width, Height);
        CopyFrame(index, buffer);
        return buffer;
    }

    public void CopyFrame(int index, PixelBuffer dest)
    {
        if (dest.Width != Width || dest.Height != Height)
        {
            throw new ArgumentException("Frame buffer size does not match the cinematic.");
        }

        int count = FrameCount;
        index = Math.Clamp(index, 0, count - 1);
        long offset = (long)index * dest.Bgra.Length;
        lock (_gate)
        {
            _video ??= new FileStream(RawPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            dest.ReadBgraFrom(_video, offset);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _video?.Dispose();
            _video = null;
        }
    }
}

public static class BinkDecoder
{
    private static readonly ConcurrentDictionary<string, BinkClip> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string InstallHint =>
        "Install FFmpeg and add it to PATH, or place ffmpeg.exe next to SlashEdit.exe. Dark Ages intros are Bink 1 (.bik).";

    public static BinkClip Decode(ArchiveEntry entry, BinkInfo info, byte[] data)
    {
        if (info.IsBink2)
        {
            throw new InvalidOperationException(
                "Bink 2 (KB2) video is not decoded by FFmpeg. Dark Ages cinematics are Bink 1.");
        }

        string? ffmpeg = FFmpegLocator.Find();
        if (ffmpeg is null)
        {
            throw new InvalidOperationException(InstallHint);
        }

        long rawBytes = (long)info.FrameByteCount * Math.Max(1, info.FrameCount);
        if (rawBytes > 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("This cinematic is too large to decode as raw frames in the viewer.");
        }

        string key = CacheKey(entry, data);
        if (Cache.TryGetValue(key, out BinkClip? existing) && File.Exists(existing.RawPath))
        {
            return existing;
        }

        string dir = Path.Combine(Path.GetTempPath(), "DarkAgesViewer", "bink", key);
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "source.bik");
        string raw = Path.Combine(dir, "video.bgra");
        string wav = Path.Combine(dir, "audio.wav");
        string ready = Path.Combine(dir, "ready.txt");

        if (!File.Exists(source) || new FileInfo(source).Length != data.Length)
        {
            File.WriteAllBytes(source, data);
        }

        int stride = info.FrameByteCount;
        if (!File.Exists(ready) || !File.Exists(raw) || new FileInfo(raw).Length < stride)
        {
            Run(ffmpeg, dir, [
                "-hide_banner", "-nostdin", "-y",
                "-i", source,
                "-map", "0:v:0",
                "-f", "rawvideo",
                "-pix_fmt", "bgra",
                "-an",
                raw
            ]);
        }

        long rawSize = new FileInfo(raw).Length;
        int frames = stride <= 0 ? 0 : (int)(rawSize / stride);
        if (frames <= 0)
        {
            throw new InvalidOperationException(
                "FFmpeg produced no video frames. This Bink file may use an unsupported revision.");
        }

        if (!File.Exists(wav) || new FileInfo(wav).Length < 44)
        {
            try
            {
                Run(ffmpeg, dir, [
                    "-hide_banner", "-nostdin", "-y",
                    "-i", source,
                    "-vn",
                    "-c:a", "pcm_s16le",
                    "-ar", "44100",
                    wav
                ]);
            }
            catch
            {
                // video-only cinematic
            }
        }

        File.WriteAllText(ready, frames.ToString());
        byte[]? audio = File.Exists(wav) && new FileInfo(wav).Length > 44
            ? File.ReadAllBytes(wav)
            : null;

        var clip = new BinkClip
        {
            Info = info,
            RawPath = raw,
            DecodedFrameCount = frames,
            Wav = audio
        };
        Cache[key] = clip;
        return clip;
    }

    public static void DisposeCache()
    {
        foreach (BinkClip clip in Cache.Values)
        {
            clip.Dispose();
        }

        Cache.Clear();
    }

    private static string CacheKey(ArchiveEntry entry, byte[] data)
    {
        int take = Math.Min(64, data.Length);
        string seed = $"{entry.Name}|{entry.Size}|{Convert.ToHexString(data.AsSpan(0, take))}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void Run(string ffmpeg, string workDir, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start FFmpeg.");
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(180_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException("FFmpeg timed out while decoding the cinematic.");
        }

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? $"exit {process.ExitCode}" : stderr.Trim();
            if (detail.Length > 800)
            {
                detail = detail[^800..];
            }

            throw new InvalidOperationException("FFmpeg could not decode this Bink file.\n" + detail);
        }
    }
}
