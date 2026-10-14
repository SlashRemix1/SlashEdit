using System.Text;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Data;

public static class DatPacker
{
    public const int EntryNameLength = 13;
    public const string IndexFileName = "_dat_index.txt";

    public static int Unpack(string datPath, string outputDirectory, IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var archive = new DataArchive(datPath);
        Directory.CreateDirectory(outputDirectory);

        var names = new List<string>(archive.Entries.Count);
        int i = 0;
        foreach (ArchiveEntry entry in archive.Entries)
        {
            string name = SafeFileName(entry.Name);
            string dest = Path.Combine(outputDirectory, name);
            File.WriteAllBytes(dest, entry.ReadAllBytes());
            names.Add(name);
            i++;
            if (i % 50 == 0 || i == archive.Entries.Count)
            {
                progress?.Report($"Unpacking {i}/{archive.Entries.Count}: {name}");
            }
        }

        File.WriteAllLines(Path.Combine(outputDirectory, IndexFileName), names);
        progress?.Report($"Unpacked {names.Count} files to {outputDirectory}");
        return names.Count;
    }

    public static int PackDirectory(string inputDirectory, string datPath, IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(datPath);
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException(inputDirectory);
        }

        IReadOnlyList<string> files = ListPackFiles(inputDirectory);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("That folder has no files to pack.");
        }

        var blobs = new List<(string Name, byte[] Data)>(files.Count);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            blobs.Add((name, File.ReadAllBytes(file)));
            if (blobs.Count % 50 == 0)
            {
                progress?.Report($"Reading {blobs.Count}/{files.Count}: {name}");
            }
        }

        int nameLength = EntryNameLength;
        if (File.Exists(datPath))
        {
            using var existing = new DataArchive(datPath);
            nameLength = existing.NameLength;
        }
        else if (blobs.Any(b => Path.GetFileName(b.Name).Length > 12))
        {
            nameLength = DataArchive.WideNameLength;
        }

        Pack(blobs, datPath, progress, nameLength);
        return blobs.Count;
    }

    /// <summary>
    /// Replace existing DAT entries by name, and optionally append new ones.
    /// Keeps the original TOC name width (13-byte NA / 32-byte LoD). First save copies <c>.bak</c>.
    /// </summary>
    public static int ReplaceEntries(
        string datPath,
        IReadOnlyList<(string Name, byte[] Data)> changes,
        bool insertIfMissing = true,
        IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datPath);
        if (changes.Count == 0)
        {
            throw new InvalidOperationException("No DAT entries to write.");
        }

        int nameLength;
        var files = new List<(string Name, byte[] Data)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new DataArchive(datPath))
        {
            nameLength = archive.NameLength;
            foreach (ArchiveEntry entry in archive.Entries)
            {
                index[entry.Name] = files.Count;
                files.Add((entry.Name, entry.ReadAllBytes()));
            }
        }

        DatCompatibility.ThrowIfUnsafe(
            ClientFamilies.FromPath(datPath),
            nameLength,
            name => index.TryGetValue(name, out int at) ? files[at].Data : null,
            changes);

        int replaced = 0;
        int inserted = 0;
        foreach ((string name, byte[] data) in changes)
        {
            string fileName = Path.GetFileName(name).Trim();
            if (index.TryGetValue(fileName, out int at))
            {
                files[at] = (files[at].Name, data);
                replaced++;
            }
            else if (insertIfMissing)
            {
                index[fileName] = files.Count;
                files.Add((fileName, data));
                inserted++;
            }
            else
            {
                throw new InvalidOperationException($"'{fileName}' is not in {Path.GetFileName(datPath)}.");
            }
        }

        string temp = datPath + ".tmpwrite";
        string backup = datPath + ".bak";
        Pack(files, temp, progress, nameLength);
        if (!File.Exists(backup))
        {
            File.Copy(datPath, backup, overwrite: false);
        }

        File.Copy(temp, datPath, overwrite: true);
        File.Delete(temp);
        progress?.Report($"Saved {replaced} replaced, {inserted} new in {datPath}");
        return replaced + inserted;
    }

    public static void Pack(
        IReadOnlyList<(string Name, byte[] Data)> files,
        string datPath,
        IProgress<string>? progress = null,
        int nameLength = EntryNameLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datPath);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No files to pack.");
        }

        if (nameLength is not DataArchive.ClassicNameLength and not DataArchive.WideNameLength)
        {
            nameLength = EntryNameLength;
        }

        string? dir = Path.GetDirectoryName(datPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        int n = files.Count;
        int dataStart = 8 + ((4 + nameLength) * n);
        var offsets = new int[n + 1];
        int cursor = dataStart;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            string encoded = DecodeName(EncodeName(files[i].Name, nameLength));
            if (!seen.Add(encoded))
            {
                throw new InvalidOperationException($"DAT names must be unique: '{encoded}'.");
            }

            offsets[i] = cursor;
            cursor = checked(cursor + files[i].Data.Length);
        }

        offsets[n] = cursor;

        using var stream = new FileStream(datPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(n + 1);
        for (int i = 0; i < n; i++)
        {
            writer.Write(offsets[i]);
            writer.Write(EncodeName(files[i].Name, nameLength));
        }

        writer.Write(offsets[n]);
        for (int i = 0; i < n; i++)
        {
            writer.Write(files[i].Data);
            if ((i + 1) % 50 == 0 || i + 1 == n)
            {
                progress?.Report($"Writing {i + 1}/{n}: {files[i].Name}");
            }
        }

        progress?.Report($"Packed {n} files to {datPath}");
    }

    public static byte[] EncodeName(string name, int nameLength = EntryNameLength)
    {
        string fileName = Path.GetFileName(name).Trim();
        int width = nameLength > 0 ? nameLength : EntryNameLength;
        var bytes = new byte[width];
        byte[] ascii = Encoding.ASCII.GetBytes(fileName);
        int copy = Math.Min(ascii.Length, width - 1);
        Buffer.BlockCopy(ascii, 0, bytes, 0, copy);
        return bytes;
    }

    public static string DecodeName(byte[] nameBytes)
    {
        string name = Encoding.ASCII.GetString(nameBytes);
        int nullChar = name.IndexOf('\0');
        return nullChar >= 0 ? name[..nullChar].Trim() : name.Trim();
    }

    public static string DefaultUnpackDirectory(string datPath)
    {
        string? dir = Path.GetDirectoryName(datPath);
        string stem = Path.GetFileNameWithoutExtension(datPath);
        return Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, stem + " unpacked data");
    }

    public static string DefaultPackPath(string inputDirectory)
    {
        string name = Path.GetFileName(inputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string suffix = " unpacked data";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^suffix.Length].Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "archive";
        }

        string? parent = Path.GetDirectoryName(inputDirectory);
        return Path.Combine(string.IsNullOrEmpty(parent) ? "." : parent, name + ".dat");
    }

    private static IReadOnlyList<string> ListPackFiles(string inputDirectory)
    {
        string indexPath = Path.Combine(inputDirectory, IndexFileName);
        if (File.Exists(indexPath))
        {
            var listed = new List<string>();
            foreach (string line in File.ReadAllLines(indexPath))
            {
                string name = line.Trim();
                if (string.IsNullOrWhiteSpace(name) || IsSkippedName(name))
                {
                    continue;
                }

                string path = Path.Combine(inputDirectory, name);
                if (File.Exists(path))
                {
                    listed.Add(path);
                }
            }

            if (listed.Count > 0)
            {
                return listed;
            }
        }

        var files = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !IsSkippedName(Path.GetFileName(f)))
            .ToList();
        files.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        return files;
    }

    private static bool IsSkippedName(string name) =>
        name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith('.');

    private static string SafeFileName(string name)
    {
        string file = Path.GetFileName(name);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(file) ? "unnamed" : file;
    }
}
