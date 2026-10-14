using System.Diagnostics;
using System.IO;

namespace DarkAges.Viewer.Preview;

public static class FFmpegLocator
{
    private static string? _cached;
    private static string? _preferred;
    private static bool _searched;

    public static string? Find()
    {
        if (_searched)
        {
            return _cached;
        }

        _searched = true;
        _cached = FindCore();
        return _cached;
    }

    public static void Prefer(string? path)
    {
        _preferred = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Reset();
    }

    public static void Reset()
    {
        _searched = false;
        _cached = null;
    }

    private static string? FindCore()
    {
        foreach (string? candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(_preferred))
        {
            yield return _preferred;
        }

        foreach (string key in new[] { "FFMPEG", "FFMPEG_PATH" })
        {
            string? env = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(env))
            {
                continue;
            }

            yield return env;
            yield return Path.Combine(env, "ffmpeg.exe");
            yield return Path.Combine(env, "ffmpeg");
            yield return Path.Combine(env, "bin", "ffmpeg.exe");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");

        string? pathExe = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (pathExe is not null)
        {
            yield return pathExe;
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(pf, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(pf86, "ffmpeg", "bin", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(local, "Programs", "ffmpeg", "bin", "ffmpeg.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // skip bad PATH entries
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            string? line = process?.StandardOutput.ReadLine();
            process?.WaitForExit(2000);
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
            {
                return line.Trim();
            }
        }
        catch
        {
            // where.exe is Windows-only
        }

        return null;
    }
}
