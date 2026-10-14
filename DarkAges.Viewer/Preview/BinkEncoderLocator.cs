using System.IO;

namespace DarkAges.Viewer.Preview;

public static class BinkEncoderLocator
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

    public const string InstallHint =
        "FFmpeg can decode Bink but cannot encode it. Install RAD Video Tools and point this viewer at radvideo64.exe (usually under Program Files (x86)\\RADVideo). Then Import cinematic can turn MP4/AVI/MKV into a .bik. You can always replace a DAT entry with an existing .bik.";

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

        string? env = Environment.GetEnvironmentVariable("RADVIDEO");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
            yield return Path.Combine(env, "radvideo64.exe");
            yield return Path.Combine(env, "radvideo.exe");
            yield return Path.Combine(env, "bink.exe");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "radvideo64.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "radvideo.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "bink.exe");

        foreach (string name in new[] { "radvideo64.exe", "radvideo.exe", "bink.exe" })
        {
            string? onPath = FindOnPath(name);
            if (onPath is not null)
            {
                yield return onPath;
            }
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string root in new[] { pf86, pf })
        {
            yield return Path.Combine(root, "RADVideo", "radvideo64.exe");
            yield return Path.Combine(root, "RADVideo", "radvideo.exe");
            yield return Path.Combine(root, "RADVideo", "bink.exe");
            yield return Path.Combine(root, "RAD Game Tools", "RADVideo", "radvideo64.exe");
        }
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
                // skip
            }
        }

        return null;
    }
}
