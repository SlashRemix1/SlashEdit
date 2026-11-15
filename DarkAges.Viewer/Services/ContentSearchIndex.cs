using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Unity;

namespace DarkAges.Viewer.Services;

public sealed class ContentSearchHit
{
    public required string Label { get; init; }
    public required string TableFile { get; init; }
    public required string MatchedText { get; init; }
    public required CatalogEntry TableCatalog { get; init; }
    public string? PrefabOrIcon { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ContentSearchIndex
{
    private const int CacheVersion = 1;

    private static readonly HashSet<string> SearchableStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item",
        "ItemDescription",
        "ItemHelp",
        "Skills",
        "Spells",
        "SkillSpellDesc",
        "ClientStrings",
        "ActionDesc",
        "ActionInfo",
        "ActionLearnString",
        "Job",
        "Emblem",
        "Titles",
        "Quest",
        "QuestStep",
        "StartQuestInfo",
        "EventMenu",
        "Effect",
        "NationDesc"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly object ArchiveReadGate = new();

    private readonly List<IndexedRow> _rows = [];

    public int RowCount => _rows.Count;
    public int TableCount { get; private set; }

    public static ContentSearchIndex Build(
        IReadOnlyList<CatalogEntry> catalogs,
        ArchiveCache cache,
        IProgress<(int Done, int Total, string Name)>? progress,
        CancellationToken ct)
    {
        var work = CollectWork(catalogs, cache);
        int total = work.Count;
        if (total == 0)
        {
            progress?.Report((1, 1, "Done"));
            return new ContentSearchIndex();
        }

        string fingerprint = Fingerprint(work);
        if (TryLoadCache(fingerprint, catalogs, out ContentSearchIndex? cached) && cached is not null)
        {
            progress?.Report((total, total, "Cached"));
            return cached;
        }

        var bag = new ConcurrentBag<IndexedRow>();
        int tableCount = 0;
        int done = 0;

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6)
        };

        try
        {
            Parallel.ForEach(work, options, item =>
            {
                string label = item.FromDisk ? Path.GetFileName(item.PathOrName) : item.PathOrName;
                try
                {
                    byte[]? data = item.FromDisk
                        ? TryReadFile(item.PathOrName)
                        : ReadArchiveEntry(cache, item.Catalog, item.PathOrName);
                    if (data is not null && data.Length > 0 && LodTableFile.LooksLike(data))
                    {
                        List<IndexedRow> rows = ExtractRows(item.Catalog, label, LodTableFile.Load(data));
                        if (rows.Count > 0)
                        {
                            foreach (IndexedRow row in rows)
                            {
                                bag.Add(row);
                            }

                            Interlocked.Increment(ref tableCount);
                        }
                    }
                }
                catch
                {
                    // skip corrupt / locked table
                }

                int n = Interlocked.Increment(ref done);
                progress?.Report((n, total, label));
            });
        }
        catch (OperationCanceledException)
        {
            // return whatever we built so the UI can keep a partial index
        }

        var index = new ContentSearchIndex { TableCount = tableCount };
        index._rows.AddRange(bag);
        if (!ct.IsCancellationRequested)
        {
            TrySaveCache(fingerprint, index);
            progress?.Report((total, total, "Done"));
        }
        else
        {
            progress?.Report((done, Math.Max(1, total), "Cancelled"));
        }

        return index;
    }

    private static List<WorkItem> CollectWork(IReadOnlyList<CatalogEntry> catalogs, ArchiveCache cache)
    {
        var work = new List<WorkItem>();
        foreach (CatalogEntry catalog in catalogs)
        {
            if (!IsTableCatalog(catalog))
            {
                continue;
            }

            try
            {
                if (catalog.IsFolder && Directory.Exists(catalog.SourcePath))
                {
                    foreach (string path in Directory.EnumerateFiles(catalog.SourcePath, "*.bin"))
                    {
                        string name = Path.GetFileName(path);
                        if (!IsSearchableTableFile(name))
                        {
                            continue;
                        }

                        long length = 0;
                        long ticks = 0;
                        try
                        {
                            var info = new FileInfo(path);
                            length = info.Length;
                            ticks = info.LastWriteTimeUtc.Ticks;
                        }
                        catch
                        {
                            // fingerprint still usable without mtime
                        }

                        work.Add(new WorkItem(catalog, path, FromDisk: true, length, ticks));
                    }
                }
                else
                {
                    foreach (ArchiveEntry entry in cache.Get(catalog).Entries)
                    {
                        if (entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && IsSearchableTableFile(entry.Name))
                        {
                            work.Add(new WorkItem(
                                catalog,
                                entry.Name,
                                FromDisk: false,
                                entry.Size,
                                0));
                        }
                    }
                }
            }
            catch
            {
                // skip unreadable catalog
            }
        }

        return work;
    }

    private static byte[]? ReadArchiveEntry(ArchiveCache cache, CatalogEntry catalog, string name)
    {
        lock (ArchiveReadGate)
        {
            return TryReadEntry(cache.Get(catalog).GetEntry(name));
        }
    }

    private static List<IndexedRow> ExtractRows(CatalogEntry catalog, string tableFile, LodTableFile table)
    {
        if (table.Headers.Count == 0 || table.Rows.Count == 0)
        {
            return [];
        }

        var stringCols = new List<int>();
        for (int c = 0; c < table.Headers.Count; c++)
        {
            if (IsSearchableHeader(table.Headers[c]))
            {
                stringCols.Add(c);
            }
        }

        if (stringCols.Count == 0)
        {
            for (int c = 0; c < table.Headers.Count; c++)
            {
                stringCols.Add(c);
            }
        }

        var rows = new List<IndexedRow>();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            string[] row = table.Rows[r];
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < table.Headers.Count && c < row.Length; c++)
            {
                fields[table.Headers[c]] = row[c] ?? "";
            }

            var blob = new List<string>(stringCols.Count);
            foreach (int c in stringCols)
            {
                if (c < row.Length && !string.IsNullOrWhiteSpace(row[c]))
                {
                    blob.Add(row[c]);
                }
            }

            if (blob.Count == 0)
            {
                continue;
            }

            rows.Add(new IndexedRow(
                catalog,
                tableFile,
                r,
                string.Join('\n', blob),
                fields));
        }

        return rows;
    }

    public IReadOnlyList<ContentSearchHit> Search(string query, int limit = 80)
    {
        if (string.IsNullOrWhiteSpace(query) || _rows.Count == 0)
        {
            return [];
        }

        string q = query.Trim();
        var hits = new List<ContentSearchHit>();
        foreach (IndexedRow row in _rows)
        {
            if (hits.Count >= limit * 3)
            {
                break;
            }

            if (!SearchLexicon.Matches(row.SearchText, q))
            {
                continue;
            }

            string term = SearchLexicon.FindMatchedTerm(row.SearchText, q) ?? q;
            string matched = FindSnippet(row.SearchText, term);
            string title = FirstField(row.Fields, "DisplayName", "Name", "ID", "PrefabName", "Key")
                ?? Path.GetFileNameWithoutExtension(row.TableFile);
            hits.Add(new ContentSearchHit
            {
                Label = $"{row.TableFile} / {title}",
                TableFile = row.TableFile,
                MatchedText = matched,
                TableCatalog = row.Catalog,
                PrefabOrIcon = FirstField(row.Fields, "PrefabName", "UIIconName", "Name", "ID"),
                Fields = row.Fields
            });
        }

        return hits
            .OrderByDescending(h => Score(h, q))
            .ThenBy(h => h.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static int Score(ContentSearchHit hit, string query)
    {
        int score = 0;
        foreach (string key in new[] { "DisplayName", "Name", "PrefabName", "UIIconName", "ID", "Key" })
        {
            if (hit.Fields.TryGetValue(key, out string? value)
                && SearchLexicon.Matches(value, query))
            {
                score += key is "DisplayName" or "Name" ? 50 : 30;
                if (value.Equals(query, StringComparison.OrdinalIgnoreCase)
                    || SearchLexicon.ExpandTerms(query)
                        .Any(t => value.Equals(t, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 40;
                }
            }
        }

        if (hit.Fields.TryGetValue("Description", out string? desc)
            && SearchLexicon.Matches(desc, query))
        {
            score += 10;
        }

        if (hit.Fields.TryGetValue("UIDescription", out string? ui)
            && SearchLexicon.Matches(ui, query))
        {
            score += 10;
        }

        return score;
    }

    private static string? FirstField(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string FindSnippet(string text, string query)
    {
        int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return text.Length <= 80 ? text : text[..80] + "…";
        }

        int start = Math.Max(0, idx - 20);
        int end = Math.Min(text.Length, idx + query.Length + 40);
        string slice = text[start..end].Replace('\n', ' ');
        return (start > 0 ? "…" : "") + slice + (end < text.Length ? "…" : "");
    }

    private static bool IsTableCatalog(CatalogEntry catalog) =>
        catalog.IsFolder
            && (catalog.DisplayName.Equals("Tables", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(catalog.SourcePath).Equals("Table", StringComparison.OrdinalIgnoreCase)
                || catalog.Variant == "LoD R" && catalog.Role == ArchiveRole.Metadata);

    private static bool IsSearchableTableFile(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return SearchableStems.Contains(stem);
    }

    private static bool IsSearchableHeader(string header) =>
        header.Equals("ID", StringComparison.OrdinalIgnoreCase)
        || header.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Name", StringComparison.OrdinalIgnoreCase)
        || header.Equals("PrefabName", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Description", StringComparison.OrdinalIgnoreCase)
        || header.Equals("ColorHexCode", StringComparison.OrdinalIgnoreCase)
        || header.Equals("UIIconName", StringComparison.OrdinalIgnoreCase)
        || header.Equals("UIDescription", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Key", StringComparison.OrdinalIgnoreCase)
        || header.Equals("AnimationState", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Name", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Desc", StringComparison.OrdinalIgnoreCase)
        || header.Contains("String", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Text", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Title", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Prefab", StringComparison.OrdinalIgnoreCase)
        || header.Contains("Icon", StringComparison.OrdinalIgnoreCase);

    private static byte[]? TryReadFile(string path)
    {
        try
        {
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? TryReadEntry(ArchiveEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            using Stream stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Fingerprint(IReadOnlyList<WorkItem> work)
    {
        var sb = new StringBuilder(work.Count * 48);
        sb.Append(CacheVersion).Append('|');
        foreach (WorkItem item in work.OrderBy(w => w.Catalog.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(w => w.PathOrName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(item.Catalog.SourcePath).Append('\t')
                .Append(item.PathOrName).Append('\t')
                .Append(item.Length).Append('\t')
                .Append(item.Ticks).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string CachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlashEdit",
            "content-search-index.json");

    private static bool TryLoadCache(
        string fingerprint,
        IReadOnlyList<CatalogEntry> catalogs,
        out ContentSearchIndex? index)
    {
        index = null;
        try
        {
            string path = CachePath;
            if (!File.Exists(path))
            {
                return false;
            }

            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            if (file is null
                || file.Version != CacheVersion
                || !string.Equals(file.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                || file.Rows is null
                || file.Rows.Count == 0)
            {
                return false;
            }

            var byPath = catalogs
                .GroupBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var loaded = new ContentSearchIndex { TableCount = file.TableCount };
            foreach (CacheRow row in file.Rows)
            {
                if (!byPath.TryGetValue(row.CatalogPath ?? "", out CatalogEntry? catalog))
                {
                    return false;
                }

                loaded._rows.Add(new IndexedRow(
                    catalog,
                    row.TableFile ?? "",
                    row.RowIndex,
                    row.SearchText ?? "",
                    row.Fields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
            }

            index = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySaveCache(string fingerprint, ContentSearchIndex index)
    {
        try
        {
            string path = CachePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = new CacheFile
            {
                Version = CacheVersion,
                Fingerprint = fingerprint,
                TableCount = index.TableCount,
                Rows = index._rows.Select(r => new CacheRow
                {
                    CatalogPath = r.Catalog.SourcePath,
                    TableFile = r.TableFile,
                    RowIndex = r.RowIndex,
                    SearchText = r.SearchText,
                    Fields = r.Fields
                }).ToList()
            };

            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // cache is best-effort
        }
    }

    private sealed record WorkItem(
        CatalogEntry Catalog,
        string PathOrName,
        bool FromDisk,
        long Length,
        long Ticks);

    private sealed record IndexedRow(
        CatalogEntry Catalog,
        string TableFile,
        int RowIndex,
        string SearchText,
        Dictionary<string, string> Fields);

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public string? Fingerprint { get; set; }
        public int TableCount { get; set; }
        public List<CacheRow>? Rows { get; set; }
    }

    private sealed class CacheRow
    {
        public string? CatalogPath { get; set; }
        public string? TableFile { get; set; }
        public int RowIndex { get; set; }
        public string? SearchText { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
    }
}
