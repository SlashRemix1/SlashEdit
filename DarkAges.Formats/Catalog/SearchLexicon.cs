using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkAges.Formats.Catalog;

/// <summary>
/// English → Korean (and related) search synonyms for LoD R table text.
/// Ships with an embedded lexicon; an optional <c>search-lexicon.json</c>
/// next to the exe merges on top (same keys replace).
/// </summary>
public static class SearchLexicon
{
    private static readonly Regex TokenSplit = new(@"[^\p{L}\p{N}+]+", RegexOptions.Compiled);
    private static readonly object Gate = new();
    private static Dictionary<string, string[]>? _map;
    /// <summary>Korean synonym → preferred English, longest keys first for greedy translate.</summary>
    private static List<KeyValuePair<string, string>>? _krToEn;
    private static string? _overridePath;
    private static DateTime _overrideStamp = DateTime.MinValue;

    /// <summary>
    /// Short Hangul stems that appear inside unrelated compounds (e.g. 검 in 검은 = black).
    /// </summary>
    private static readonly Dictionary<string, string[]> FalseFriendPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["검"] = ["검은", "검정", "검색", "검거", "검토", "검사지", "검투", "검사"],
            ["화"] = ["화살", "화면", "화요일", "화장", "화재"],
            ["성"] = ["성적", "성공", "성별", "성인", "성격"],
            ["독"] = ["독립", "독서", "독특"],
            ["불"] = ["불안", "불편", "불구", "불가", "불법"],
            ["금"] = ["금지", "금액", "금요일", "금방", "금속"],
            ["은"] = ["있는", "없는", "은은", "은근", "은행"],
            ["알"] = ["알수없", "알려", "알림", "알수"],
        };

    /// <summary>
    /// Trailing element suffixes on gear names (레더튜닉화, 어설픈연단검풍).
    /// Kept out of the main lexicon so searching "wind" does not flood every 풍 item.
    /// </summary>
    private static readonly Dictionary<string, string> TrailingElements =
        new(StringComparer.Ordinal)
        {
            ["풍"] = "Wind",
            ["수"] = "Water",
            ["화"] = "Fire",
            ["토"] = "Earth",
        };

    public static int EntryCount
    {
        get
        {
            EnsureLoaded();
            return _map!.Count;
        }
    }

    /// <summary>All English keys with their Korean (and related) synonyms, A–Z.</summary>
    public static IReadOnlyList<LexiconEntry> AllEntries()
    {
        EnsureLoaded();
        return _map!
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new LexiconEntry(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Type-ahead matches for the text currently being typed (usually the last token).
    /// Prefers English prefix hits, then contains, then Korean synonym prefix.
    /// </summary>
    public static IReadOnlyList<LexiconEntry> Suggest(string? typed, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(typed) || limit <= 0)
        {
            return [];
        }

        EnsureLoaded();
        string q = typed.Trim();
        var starts = new List<LexiconEntry>();
        var contains = new List<LexiconEntry>();
        var korean = new List<LexiconEntry>();

        foreach (KeyValuePair<string, string[]> kv in _map!)
        {
            if (kv.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                starts.Add(new LexiconEntry(kv.Key, kv.Value));
            }
            else if (kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(new LexiconEntry(kv.Key, kv.Value));
            }
            else if (kv.Value.Any(s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                || s.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                korean.Add(new LexiconEntry(kv.Key, kv.Value));
            }
        }

        return starts
            .OrderBy(e => e.English, StringComparer.OrdinalIgnoreCase)
            .Concat(contains.OrderBy(e => e.English, StringComparer.OrdinalIgnoreCase))
            .Concat(korean.OrderBy(e => e.English, StringComparer.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public static IReadOnlyList<LexiconEntry> FilterEntries(string? filter, int limit = 500)
    {
        EnsureLoaded();
        IEnumerable<LexiconEntry> all = AllEntries();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string q = filter.Trim();
            all = all.Where(e =>
                e.English.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Korean.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        return all.Take(limit).ToList();
    }

    /// <summary>
    /// True when <paramref name="text"/> matches the query after lexicon expansion.
    /// Multi-word queries require every token group to hit (AND of ORs).
    /// </summary>
    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (ContainsTerm(text, query.Trim()))
        {
            return true;
        }

        IReadOnlyList<IReadOnlyList<string>> groups = ExpandGroups(query);
        if (groups.Count == 0)
        {
            return false;
        }

        foreach (IReadOnlyList<string> group in groups)
        {
            bool any = false;
            foreach (string term in group)
            {
                if (term.Length > 0 && ContainsTerm(text, term))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// All distinct search terms for <paramref name="query"/> (original tokens + synonyms).
    /// Useful for OR-style filename matching.
    /// </summary>
    public static IReadOnlyList<string> ExpandTerms(string? query)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyList<string> group in ExpandGroups(query))
        {
            foreach (string term in group)
            {
                if (term.Length > 0)
                {
                    set.Add(term);
                }
            }
        }

        return set.ToList();
    }

    /// <summary>
    /// Per-token synonym groups. Empty when query has no usable tokens.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ExpandGroups(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        EnsureLoaded();
        var groups = new List<IReadOnlyList<string>>();
        foreach (string raw in TokenSplit.Split(query.Trim()))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string token = raw.Trim();
            var group = new List<string> { token };
            if (_map!.TryGetValue(token, out string[]? syns))
            {
                foreach (string s in syns)
                {
                    if (!string.IsNullOrWhiteSpace(s) && !group.Contains(s, StringComparer.OrdinalIgnoreCase))
                    {
                        group.Add(s);
                    }
                }
            }

            // Also try singular/plural-ish strip of trailing 's' for simple English nouns.
            if (token.Length > 3 && token.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                string stem = token[..^1];
                if (!group.Contains(stem, StringComparer.OrdinalIgnoreCase))
                {
                    group.Add(stem);
                }

                if (_map.TryGetValue(stem, out string[]? stemSyns))
                {
                    foreach (string s in stemSyns)
                    {
                        if (!string.IsNullOrWhiteSpace(s) && !group.Contains(s, StringComparer.OrdinalIgnoreCase))
                        {
                            group.Add(s);
                        }
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// First matching term found in <paramref name="text"/> for snippet highlighting.
    /// </summary>
    public static string? FindMatchedTerm(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string trimmed = query.Trim();
        if (ContainsTerm(text, trimmed))
        {
            return trimmed;
        }

        foreach (string term in ExpandTerms(query).OrderByDescending(t => t.Length))
        {
            if (ContainsTerm(text, term))
            {
                return term;
            }
        }

        return null;
    }

    /// <summary>
    /// English query word that bridged to a Korean (or synonym) hit in <paramref name="text"/>.
    /// Used to show "아벨로더  ·  abel" in search results.
    /// </summary>
    public static string? EnglishGloss(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        EnsureLoaded();
        foreach (IReadOnlyList<string> group in ExpandGroups(query))
        {
            string? english = null;
            bool hit = false;
            foreach (string term in group)
            {
                if (!IsHangulHeavy(term))
                {
                    english ??= term;
                }

                if (ContainsTerm(text, term))
                {
                    hit = true;
                }
            }

            if (hit
                && !string.IsNullOrWhiteSpace(english)
                && !ContainsTerm(text, english))
            {
                return english;
            }
        }

        return null;
    }

    /// <summary>
    /// Best-effort English gloss for a Korean (or mixed) display name using the lexicon
    /// in reverse — e.g. 루어스스카웃튜닉 → "Loures Scout Tunic".
    /// Returns null when nothing could be translated.
    /// </summary>
    public static string? TranslateToEnglish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string source = text.Trim();
        // Prefer the Hangul display name when label is "셔츠 → item001_96".
        int arrow = source.IndexOf('→');
        if (arrow < 0)
        {
            arrow = source.IndexOf("->", StringComparison.Ordinal);
        }

        if (arrow > 0)
        {
            source = source[..arrow].Trim();
        }

        int newline = source.IndexOfAny(['\r', '\n']);
        if (newline > 0)
        {
            source = source[..newline].Trim();
        }

        if (!IsHangulHeavy(source))
        {
            return null;
        }

        EnsureLoaded();
        IReadOnlyList<KeyValuePair<string, string>> map = KrToEn();
        if (map.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        int i = 0;
        int matchedChars = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c) || c is '(' or ')' or '[' or ']' or '{' or '}' or '/' or '-' or '·' or '•')
            {
                i++;
                continue;
            }

            string? bestKr = null;
            string? bestEn = null;
            foreach (KeyValuePair<string, string> kv in map)
            {
                if (kv.Key.Length == 0 || i + kv.Key.Length > source.Length)
                {
                    continue;
                }

                if (string.Compare(source, i, kv.Key, 0, kv.Key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    bestKr = kv.Key;
                    bestEn = kv.Value;
                    break; // map is longest-first
                }
            }

            // Element suffix only when it consumes the end of the name (…검풍).
            if (bestKr is null
                && i == source.Length - 1
                && TrailingElements.TryGetValue(source[i].ToString(), out string? element))
            {
                bestKr = source[i].ToString();
                bestEn = element;
            }

            if (bestKr is not null && bestEn is not null)
            {
                parts.Add(TitleCaseEnglish(bestEn));
                i += bestKr.Length;
                matchedChars += bestKr.Length;
                continue;
            }

            // Skip one unmatched Hangul / letter so the rest can still translate.
            i++;
        }

        if (parts.Count == 0 || matchedChars == 0)
        {
            return null;
        }

        // Require covering most of the Hangul so a lone stem (검) does not become the gloss.
        int hangulCount = source.Count(IsHangulSyllable);
        if (hangulCount > 0 && matchedChars * 4 < hangulCount * 3)
        {
            return null;
        }

        string gloss = string.Join(' ', parts);
        if (gloss.Equals(source, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return gloss;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> KrToEn()
    {
        if (_krToEn is not null)
        {
            return _krToEn;
        }

        // Korean → (english, synonymIndex). Lower index = more canonical for that English key.
        var best = new Dictionary<string, (string En, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string[]> kv in _map!)
        {
            for (int i = 0; i < kv.Value.Length; i++)
            {
                string kr = kv.Value[i];
                if (string.IsNullOrWhiteSpace(kr) || !IsHangulHeavy(kr))
                {
                    continue;
                }

                // Prefer the English key that lists this Korean earliest (튜닉 → tunic, not shirt).
                if (!best.TryGetValue(kr, out (string En, int Rank) existing)
                    || i < existing.Rank
                    || (i == existing.Rank
                        && string.Compare(kv.Key, existing.En, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best[kr] = (kv.Key, i);
                }
            }
        }

        _krToEn = best
            .OrderByDescending(kv => kv.Key.Length)
            .ThenBy(kv => kv.Value.Rank)
            .ThenBy(kv => kv.Value.En, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.En))
            .ToList();
        return _krToEn;
    }

    private static string TitleCaseEnglish(string english)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        // Multi-word keys are rare; still title-case each token.
        string[] tokens = english.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t.Length == 0)
            {
                continue;
            }

            tokens[i] = char.ToUpperInvariant(t[0]) + (t.Length > 1 ? t[1..].ToLowerInvariant() : "");
        }

        return string.Join(' ', tokens);
    }

    private static bool ContainsTerm(string text, string term)
    {
        if (term.Length == 0)
        {
            return false;
        }

        bool hangulTerm = IsHangulHeavy(term);
        FalseFriendPrefixes.TryGetValue(term, out string[]? falseFriends);

        if (!hangulTerm && falseFriends is null)
        {
            return text.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool blocked = false;
            if (falseFriends is not null)
            {
                foreach (string friend in falseFriends)
                {
                    if (idx + friend.Length <= text.Length
                        && string.Compare(text, idx, friend, 0, friend.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        blocked = true;
                        break;
                    }
                }
            }

            // Multi-syllable Hangul synonyms (단검, 장검) need a word edge so
            // they don't match inside longer compounds (연단검…). Single-syllable
            // terms (검, 칼) rely on false-friend filtering instead.
            if (!blocked && hangulTerm && term.Length >= 2 && !HasHangulEdge(text, idx, term.Length))
            {
                blocked = true;
            }

            if (!blocked)
            {
                return true;
            }

            start = idx + 1;
        }

        return false;
    }

    private static bool IsHangulHeavy(string term)
    {
        int hangul = 0;
        foreach (char c in term)
        {
            if (IsHangulSyllable(c))
            {
                hangul++;
            }
        }

        return hangul > 0 && hangul * 2 >= term.Length;
    }

    private static bool HasHangulEdge(string text, int idx, int length)
    {
        bool prevOk = idx == 0 || !IsHangulSyllable(text[idx - 1]);
        int after = idx + length;
        bool nextOk = after >= text.Length || !IsHangulSyllable(text[after]);
        return prevOk || nextOk;
    }

    private static bool IsHangulSyllable(char c) => c is >= '\uAC00' and <= '\uD7A3';

    /// <summary>User-editable lexicon path next to the exe (full replace when present).</summary>
    public static string WritablePath =>
        Path.Combine(AppContext.BaseDirectory, "search-lexicon.json");

    public static bool HasUserOverride
    {
        get
        {
            try
            {
                return File.Exists(WritablePath);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _map = null;
            _krToEn = null;
            _overridePath = null;
            _overrideStamp = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Writes the full lexicon to <see cref="WritablePath"/> and reloads.
    /// When that file exists it replaces the embedded dictionary (so removals stick).
    /// </summary>
    public static void SaveEntries(IEnumerable<LexiconEntry> entries)
    {
        var map = new SortedDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (LexiconEntry entry in entries)
        {
            string key = entry.English.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string[] syns = entry.Korean
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (syns.Length == 0)
            {
                continue;
            }

            map[key] = syns;
        }

        string path = WritablePath;
        string json = JsonSerializer.Serialize(
            map,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        File.WriteAllText(path, json);
        Reload();
        EnsureLoaded();
    }

    /// <summary>Delete the user override and restore the embedded lexicon.</summary>
    public static void ResetToEmbedded()
    {
        try
        {
            string path = WritablePath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // still reload
        }

        Reload();
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            string path = WritablePath;
            bool hasOverride = false;
            try
            {
                hasOverride = File.Exists(path);
            }
            catch
            {
                hasOverride = false;
            }

            string? overrideFile = hasOverride ? path : null;
            DateTime stamp = hasOverride
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            if (_map is not null
                && string.Equals(_overridePath, overrideFile, StringComparison.OrdinalIgnoreCase)
                && _overrideStamp == stamp)
            {
                return;
            }

            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (hasOverride)
            {
                try
                {
                    LoadInto(map, File.ReadAllText(path));
                }
                catch
                {
                    LoadInto(map, ReadEmbeddedJson());
                }

                if (map.Count == 0)
                {
                    LoadInto(map, ReadEmbeddedJson());
                }
            }
            else
            {
                LoadInto(map, ReadEmbeddedJson());
            }

            _map = map;
            _krToEn = null;
            _overridePath = overrideFile;
            _overrideStamp = stamp;
        }
    }

    private static string ReadEmbeddedJson()
    {
        Assembly asm = typeof(SearchLexicon).Assembly;
        const string name = "DarkAges.Formats.Catalog.search-lexicon.json";
        using Stream? stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            // Fallback: try any resource ending with the file name.
            string? alt = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("search-lexicon.json", StringComparison.OrdinalIgnoreCase));
            if (alt is null)
            {
                return "{}";
            }

            using Stream? s2 = asm.GetManifestResourceStream(alt);
            if (s2 is null)
            {
                return "{}";
            }

            using var r2 = new StreamReader(s2);
            return r2.ReadToEnd();
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void LoadInto(Dictionary<string, string[]> map, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name) || prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var list = new List<string>();
            foreach (JsonElement el in prop.Value.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    string? s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s.Trim());
                    }
                }
            }

            if (list.Count > 0)
            {
                map[prop.Name.Trim()] = list.ToArray();
            }
        }
    }
}

public readonly record struct LexiconEntry(string English, IReadOnlyList<string> Korean)
{
    public string Label
    {
        get
        {
            if (Korean.Count == 0)
            {
                return English;
            }

            string syn = string.Join(", ", Korean.Take(5));
            if (Korean.Count > 5)
            {
                syn += "…";
            }

            return $"{English}  →  {syn}";
        }
    }
}
