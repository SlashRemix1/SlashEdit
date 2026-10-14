using System.Diagnostics;
using System.IO;
using DarkAges.Formats.Imaging;

namespace DarkAges.Viewer.Preview;

public static class BinkImporter
{
    public static bool IsBink(string path) =>
        Path.GetExtension(path).Equals(".bik", StringComparison.OrdinalIgnoreCase);

    public static byte[] Import(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("That video file is gone.", path);
        }

        if (IsBink(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (!BinkFile.TryParse(existing, out BinkInfo info))
            {
                throw new InvalidOperationException("That .bik file is not a readable Bink container.");
            }

            if (info.IsBink2)
            {
                throw new InvalidOperationException("That file is Bink 2. Dark Ages plays Bink 1 (.bik with a BIK header).");
            }

            return existing;
        }

        string? rad = BinkEncoderLocator.Find();
        if (rad is null)
        {
            throw new InvalidOperationException(BinkEncoderLocator.InstallHint);
        }

        string work = Path.Combine(Path.GetTempPath(), "DarkAgesViewer", "bink-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string output = Path.Combine(work, "out.bik");
            try
            {
                Compress(rad, path, output);
            }
            catch (InvalidOperationException) when (NeedsAviBridge(path))
            {
                string? ffmpeg = FFmpegLocator.Find();
                if (ffmpeg is null)
                {
                    throw new InvalidOperationException(
                        "RAD Video Tools could not read that file directly. Install FFmpeg (More ▾ → FFmpeg for Bink) so the importer can build an AVI first.\n\n" +
                        BinkEncoderLocator.InstallHint);
                }

                string avi = Path.Combine(work, "bridge.avi");
                Run(
                    ffmpeg,
                    work,
                    ["-y", "-i", path, "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "mpeg4", "-q:v", "6", "-c:a", "pcm_s16le", avi],
                    "FFmpeg could not convert that video to AVI.");
                Compress(rad, avi, output);
            }

            byte[] data = File.ReadAllBytes(output);
            if (!BinkFile.TryParse(data, out BinkInfo info))
            {
                throw new InvalidOperationException("RAD Video Tools ran but did not write a readable .bik.");
            }

            if (info.IsBink2)
            {
                throw new InvalidOperationException(
                    "RAD wrote Bink 2. Dark Ages needs Bink 1. In RAD Video Tools, compress with the legacy Bink 1 / BIK option if your version still offers it.");
            }

            return data;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // temp
            }
        }
    }

    private static bool NeedsAviBridge(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not ".avi" and not ".wav" and not ".bmp";
    }

    private static void Compress(string rad, string input, string output)
    {
        string name = Path.GetFileName(rad);
        bool oldBink = name.Equals("bink.exe", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> args = oldBink
            ? [input, output, "/#"]
            : ["binkc", input, output, "/#"];
        Run(rad, Path.GetDirectoryName(rad) ?? "", args, "RAD Video Tools could not compress that file to Bink.");
        if (!File.Exists(output) || new FileInfo(output).Length < 32)
        {
            throw new InvalidOperationException("RAD Video Tools did not produce a .bik file.");
        }
    }

    private static void Run(string fileName, string workDir, IReadOnlyList<string> args, string fail)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start " + Path.GetFileName(fileName) + ".");
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(600_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException(Path.GetFileName(fileName) + " timed out.");
        }

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? $"exit {process.ExitCode}" : stderr.Trim();
            if (detail.Length > 800)
            {
                detail = detail[^800..];
            }

            throw new InvalidOperationException(fail + "\n" + detail);
        }
    }
}
