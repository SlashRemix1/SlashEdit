using System.IO;
using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.Unity;
using DarkAges.Viewer.Preview;

namespace DarkAges.Viewer.Services;

public sealed class ImageMatchHit
{
    public required CatalogEntry Catalog { get; init; }
    public required ArchiveEntry Entry { get; init; }
    public float Score { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public PixelBuffer? Thumb { get; init; }

    /// <summary>Optional display title (e.g. composite paper-doll label).</summary>
    public string? Label { get; init; }

    public bool IsComposite { get; init; }

    public string Title =>
        !string.IsNullOrWhiteSpace(Label) ? Label! : Entry.DisplayName;
}

public sealed class ReverseImageSearchOptions
{
    public bool Items { get; init; } = true;
    public bool Players { get; init; }

    /// <summary>
    /// Match against assembled paper-dolls (body + each idle gear piece).
    /// Best for full character / costume screenshots.
    /// </summary>
    public bool Composites { get; init; } = true;

    public bool Ui { get; init; } = true;
    public bool Mobs { get; init; }
    public bool Effects { get; init; }
    public float MinScore { get; init; } = 0.40f;
    public int MaxHits { get; init; } = 40;

    /// <summary>
    /// Screenshot search step in pixels (1 = dense, 8/10 = block stride). Larger = faster, slightly less precise.
    /// </summary>
    public int BlockSize { get; init; } = 8;
}

public static class ReverseImageSearch
{
    public static IReadOnlyList<ImageMatchHit> Search(
        PixelBuffer query,
        IReadOnlyList<CatalogEntry> catalogs,
        ArchiveCache cache,
        ReverseImageSearchOptions options,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        int blockSize = Math.Clamp(options.BlockSize, 1, 32);
        ImageMatcher.QueryImage prepared = ImageMatcher.Prepare(query, blockSize: blockSize);
        bool cluttered = prepared.IsCluttered;
        bool characterQuery = LooksLikeCharacterQuery(prepared) && !cluttered;
        bool scanItems = options.Items;
        bool scanPlayers = options.Players;
        bool scanComposites = options.Composites;
        if (cluttered)
        {
            // Bag / inventory / multi-icon shots: identify discrete tiles, not paper-dolls.
            if (options.Items)
            {
                scanItems = true;
            }

            if (scanComposites && options.Items)
            {
                scanComposites = false;
                progress?.Report(
                    $"Cluttered scene ({prepared.Objects.Length} objects) — matching Items/UI inside the image (Composites skipped).");
            }
            else
            {
                progress?.Report($"Cluttered scene ({prepared.Objects.Length} objects) — matching each region…");
            }
        }
        else if (characterQuery)
        {
            // Full character / costume shots should not run the inventory-icon pipeline —
            // body/gear shapes in that pass poison scores and yield empty item results.
            if (options.Items)
            {
                scanItems = false;
                progress?.Report("Query looks like a character — skipping Items (use Composites for costumes).");
            }

            // Raw gear layers duplicate Unity/khan composites and let tiny motion tips
            // (8×11 axe heads, etc.) outrank the real coat/head/weapon.
            if (options.Players && options.Composites)
            {
                scanPlayers = false;
                progress?.Report("Query looks like a character — using Composites only (Gear layers skipped).");
            }
        }

        var layerCandidates = new List<(CatalogEntry Catalog, ArchiveEntry Entry, IArchive Archive)>();
        foreach (CatalogEntry catalog in catalogs)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!WantsRole(catalog.Role, options, scanItems, scanPlayers))
            {
                continue;
            }

            try
            {
                IArchive archive = cache.Get(catalog);
                if (archive is UnityBundleArchive unity && !unity.SpriteIndexReady)
                {
                    progress?.Report($"Indexing {catalog.DisplayName}…");
                    unity.EnsureSpriteIndex();
                }

                int before = layerCandidates.Count;
                foreach (ArchiveEntry entry in EnumerateCandidates(catalog, archive))
                {
                    layerCandidates.Add((catalog, entry, archive));
                }

                int added = layerCandidates.Count - before;
                if (added >= 500)
                {
                    progress?.Report($"{catalog.DisplayName}: {added} candidates");
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"{catalog.DisplayName}: {ex.Message}");
            }
        }

        var compositeCandidates = new List<(CatalogEntry Catalog, ArchiveEntry Entry, IArchive Archive, PixelBuffer Pixels, string Label)>();
        if (scanComposites)
        {
            progress?.Report("Building character composites (paper-dolls, all facings)…");
            foreach (CatalogEntry catalog in catalogs)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (catalog.Role != ArchiveRole.PlayerSprites)
                {
                    continue;
                }

                try
                {
                    IArchive archive = cache.Get(catalog);
                    if (archive is UnityBundleArchive unity)
                    {
                        // LoD R has no classic paper-doll EPFs — match character sprites directly.
                        unity.EnsureSpriteIndex();
                        int n = 0;
                        foreach (ArchiveEntry entry in unity.RepresentativeImageEntries())
                        {
                            if (!IsCharacterModelAsset(entry))
                            {
                                continue;
                            }

                            PixelBuffer? pixels = Decode(archive, entry);
                            if (pixels is null || !pixels.HasVisiblePixels())
                            {
                                continue;
                            }

                            compositeCandidates.Add(
                                (catalog, entry, archive, pixels, $"Unity · {entry.DisplayName}"));
                            n++;
                        }

                        if (n > 0)
                        {
                            progress?.Report($"{catalog.DisplayName}: {n:N0} character sprites");
                        }

                        continue;
                    }

                    if (!KhanPieces.IsKhanArchive(archive))
                    {
                        continue;
                    }

                    int built = 0;
                    foreach (CompositeCandidate doll in CharacterAssembler.IdleComposites(archive, allFacings: true))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        compositeCandidates.Add((catalog, doll.FocusEntry, archive, doll.Pixels, doll.Label));
                        built++;
                        if (built % 400 == 0)
                        {
                            progress?.Report($"Composites {built:N0} from {catalog.DisplayName}…");
                        }
                    }

                    if (built > 0)
                    {
                        progress?.Report($"{catalog.DisplayName}: {built:N0} composites");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"{catalog.DisplayName}: {ex.Message}");
                }
            }
        }

        int totalWork = layerCandidates.Count + compositeCandidates.Count;
        progress?.Report($"Scanning {totalWork:N0} candidates ({compositeCandidates.Count:N0} composites, {layerCandidates.Count:N0} layers)…");
        if (cluttered)
        {
            progress?.Report(
                $"Multi-item mode ({prepared.Pixels.Width}×{prepared.Pixels.Height}, {prepared.Objects.Length} object crops)…");
        }
        else if (prepared.IsScreenshot)
        {
            progress?.Report(
                $"Screenshot mode ({prepared.Pixels.Width}×{prepared.Pixels.Height}, block {blockSize}px, {prepared.Segments.Length} regions)…");
        }
        else
        {
            progress?.Report($"Icon mode ({prepared.Pixels.Width}×{prepared.Pixels.Height}) — comparing cropped sprites…");
        }

        var hits = new List<ImageMatchHit>();
        var softHits = new List<ImageMatchHit>(); // below MinScore but still useful
        var gate = new object();
        int done = 0;
        float bestSeen = 0;
        // Soft hits below this are noise — do not surface them as "matches".
        const float SoftFloor = 0.36f;

        void Consider(
            CatalogEntry catalog,
            ArchiveEntry entry,
            IArchive archive,
            PixelBuffer? readyPixels,
            string? label,
            bool composite)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            PixelBuffer? pixels = readyPixels ?? Decode(archive, entry);
            if (pixels is null || !pixels.HasVisiblePixels())
            {
                return;
            }

            PixelBuffer preparedSprite = ImageMatcher.PrepareSprite(pixels);
            if (!preparedSprite.HasVisiblePixels())
            {
                return;
            }

            if (!LooksPlausible(prepared, preparedSprite))
            {
                return;
            }

            ImageMatcher.MatchResult match = ImageMatcher.BestMatch(prepared, preparedSprite);
            if (match.Score <= 0.01f)
            {
                return;
            }

            PixelBuffer thumb = preparedSprite.Contain(64, 64);
            var hit = new ImageMatchHit
            {
                Catalog = catalog,
                Entry = entry,
                Score = match.Score,
                X = match.X,
                Y = match.Y,
                Thumb = thumb,
                Label = label,
                IsComposite = composite
            };

            lock (gate)
            {
                if (match.Score > bestSeen)
                {
                    bestSeen = match.Score;
                }

                if (match.Score >= options.MinScore)
                {
                    hits.Add(hit);
                }
                else if (match.Score >= SoftFloor)
                {
                    softHits.Add(hit);
                }
            }
        }

        void ReportProgress()
        {
            int hitCount;
            float best;
            lock (gate)
            {
                hitCount = hits.Count;
                best = bestSeen;
            }

            progress?.Report(
                hitCount > 0
                    ? $"Checked {done:N0}/{totalWork:N0} · {hitCount} hit(s)…"
                    : $"Checked {done:N0}/{totalWork:N0} · best {best:0%}…");
        }

        // Composites first — usually what a full-character query wants.
        foreach (var item in compositeCandidates)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            Consider(item.Catalog, item.Entry, item.Archive, item.Pixels, item.Label, composite: true);
            done++;
            if (done % 25 == 0)
            {
                ReportProgress();
            }
        }

        // Layer / icon candidates (Unity not thread-safe).
        foreach (IGrouping<IArchive, (CatalogEntry Catalog, ArchiveEntry Entry, IArchive Archive)> group
                 in layerCandidates.GroupBy(c => c.Archive))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            bool parallel = group.Key is not UnityBundleArchive;
            var list = group.ToList();
            if (parallel)
            {
                Parallel.ForEach(list, new ParallelOptions { CancellationToken = ct }, item =>
                {
                    Consider(item.Catalog, item.Entry, item.Archive, null, null, composite: false);
                    Interlocked.Increment(ref done);
                    if (done % 100 == 0)
                    {
                        ReportProgress();
                    }
                });
            }
            else
            {
                foreach (var item in list)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    Consider(item.Catalog, item.Entry, item.Archive, null, null, composite: false);
                    done++;
                    if (done % 50 == 0)
                    {
                        ReportProgress();
                    }
                }
            }
        }

        // Keep best facing per focus entry so 8 dir variants don't flood the list.
        List<ImageMatchHit> pool = hits.Count > 0 ? hits : softHits;
        List<ImageMatchHit> ranked = pool
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.IsComposite)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .GroupBy(h => $"{h.Catalog.SourcePath}\0{h.Entry.Name}\0{h.Entry.FrameIndex}\0{h.IsComposite}")
            .Select(g => g.First())
            .ToList();

        if (ranked.Count == 0 || ranked[0].Score < SoftFloor)
        {
            progress?.Report(
                bestSeen > 0
                    ? $"No confident matches (best {bestSeen:0%}). For costumes use Composites; for inventory icons crop the item only and enable Items."
                    : "No usable scores — load a classic client (khan) for Composites, or an item DAT for Items.");
            return [];
        }

        if (hits.Count == 0)
        {
            progress?.Report($"Weak matches only — showing top candidates (best {ranked[0].Score:0%}). Lower Min score if needed.");
        }

        // Full-character composites: keep the best coat / head / weapon / … separately.
        // Otherwise one strong weapon floods the list and the costume pieces disappear.
        // Skip that when an inventory icon clearly wins (zoomed item007_204 @ 95%, etc.).
        float bestItemScore = ranked
            .Where(h => !h.IsComposite && h.Catalog.Role == ArchiveRole.Items)
            .Select(h => h.Score)
            .DefaultIfEmpty(0f)
            .Max();
        float bestAny = ranked[0].Score;
        bool strongItemHit = bestItemScore >= 0.75f && bestItemScore + 0.05f >= bestAny;
        bool compositeHeavy = ranked.Count(h => h.IsComposite) >= 8;
        if (!strongItemHit && (compositeHeavy || characterQuery))
        {
            List<ImageMatchHit> diverse = DiversifyCompositeSlots(ranked, Math.Max(options.MaxHits, 40));
            if (diverse.Count > 0)
            {
                progress?.Report(
                    $"Composite slots diversified — {diverse.Count} hit(s) across gear types (best {diverse[0].Score:0%}).");
                return diverse;
            }
        }

        float bestScore = ranked[0].Score;
        // Cluttered scenes often contain several real items at different strengths —
        // keep a wider band so secondary icons aren't dropped by one strong hit.
        float band = cluttered ? 0.22f : 0.10f;
        float relativeFloor = Math.Max(SoftFloor, bestScore - band);
        if (hits.Count > 0)
        {
            relativeFloor = Math.Max(
                cluttered ? Math.Min(options.MinScore, SoftFloor) : options.MinScore * 0.9f,
                bestScore - band);
        }

        int maxHits = Math.Max(1, cluttered ? Math.Max(options.MaxHits, 60) : options.MaxHits);
        return ranked
            .Where(h => h.Score >= relativeFloor)
            .Take(maxHits)
            .ToList();
    }

    /// <summary>
    /// Round-robin the strongest hit from each paper-doll slot so a costume screenshot
    /// surfaces coat + hat + weapon together instead of 40 near-identical weapons.
    /// </summary>
    private static List<ImageMatchHit> DiversifyCompositeSlots(List<ImageMatchHit> ranked, int maxHits)
    {
        var bySlot = new Dictionary<string, List<ImageMatchHit>>(StringComparer.OrdinalIgnoreCase);
        var other = new List<ImageMatchHit>();
        foreach (ImageMatchHit hit in ranked)
        {
            string slot = CompositeSlotKey(hit);
            if (slot.Length == 0)
            {
                other.Add(hit);
                continue;
            }

            if (!bySlot.TryGetValue(slot, out List<ImageMatchHit>? list))
            {
                bySlot[slot] = list = [];
            }

            list.Add(hit);
        }

        if (bySlot.Count < 2)
        {
            return [];
        }

        // Prefer slots that actually matter for costume ID.
        string[] priority =
        [
            "weapon", "coat", "armor", "head", "boots", "accessory", "shield", "body", "face", "other"
        ];
        var queues = new List<Queue<ImageMatchHit>>();
        foreach (string key in priority)
        {
            if (!bySlot.TryGetValue(key, out List<ImageMatchHit>? list) || list.Count == 0)
            {
                continue;
            }

            queues.Add(new Queue<ImageMatchHit>(list));
        }

        if (other.Count > 0)
        {
            queues.Add(new Queue<ImageMatchHit>(other));
        }

        var result = new List<ImageMatchHit>();
        bool progressed = true;
        while (result.Count < maxHits && progressed)
        {
            progressed = false;
            foreach (Queue<ImageMatchHit> q in queues)
            {
                if (q.Count == 0 || result.Count >= maxHits)
                {
                    continue;
                }

                result.Add(q.Dequeue());
                progressed = true;
            }
        }

        // Keep round-robin order (weapon, coat, head, …) — re-sorting by score
        // would put 40 weapons back on top and hide the costume pieces again.
        return result;
    }

    private static string CompositeSlotKey(ImageMatchHit hit)
    {
        if (!hit.IsComposite)
        {
            // Unity player sprites: mu/mh/mw… prefixes map to khan slots.
            string stem = Path.GetFileNameWithoutExtension(hit.Entry.DisplayName);
            if (stem.Length >= 2 && (stem[0] is 'm' or 'w'))
            {
                return stem[1] switch
                {
                    'h' => "head",
                    'u' or 'a' => "coat",
                    'i' or 'j' => "armor",
                    'w' or 'p' => "weapon",
                    'l' => "boots",
                    's' => "shield",
                    'c' or 'g' => "accessory",
                    'b' or 'm' or 'n' => "body",
                    'o' => "face",
                    _ => "other"
                };
            }

            return "";
        }

        string t = hit.Title.ToLowerInvariant();
        if (t.Contains("weapon"))
        {
            return "weapon";
        }

        if (t.Contains("coat") || t.Contains("over"))
        {
            return "coat";
        }

        if (t.Contains("armor"))
        {
            return "armor";
        }

        if (t.Contains("head") || t.Contains("helm"))
        {
            return "head";
        }

        if (t.Contains("boot"))
        {
            return "boots";
        }

        if (t.Contains("shield"))
        {
            return "shield";
        }

        if (t.Contains("accessor"))
        {
            return "accessory";
        }

        if (t.Contains("body"))
        {
            return "body";
        }

        if (t.Contains("face"))
        {
            return "face";
        }

        return "other";
    }

    private static bool WantsRole(
        ArchiveRole role,
        ReverseImageSearchOptions options,
        bool scanItems,
        bool scanPlayers) =>
        role switch
        {
            ArchiveRole.Items => scanItems,
            ArchiveRole.PlayerSprites => scanPlayers,
            ArchiveRole.Ui => options.Ui,
            ArchiveRole.Mobs => options.Mobs,
            ArchiveRole.Effects => options.Effects,
            _ => false
        };

    /// <summary>
    /// Role-aware candidate list. Player/gear and Unity animation packs collapse to one
    /// frame per sequence — otherwise walk/attack cels explode into 100k+ near-duplicates.
    /// Items never include character body / gear models.
    /// </summary>
    private static IEnumerable<ArchiveEntry> EnumerateCandidates(CatalogEntry catalog, IArchive archive)
    {
        if (catalog.Role == ArchiveRole.Items)
        {
            return EnumerateItemCandidates(archive);
        }

        if (archive is UnityBundleArchive unity)
        {
            return unity.RepresentativeImageEntries()
                .Where(e => catalog.Role != ArchiveRole.PlayerSprites || IsCharacterModelAsset(e));
        }

        if (catalog.Role == ArchiveRole.PlayerSprites)
        {
            if (KhanPieces.IsKhanArchive(archive))
            {
                return KhanPieces.CollapseForGallery(archive.Entries);
            }

            return archive.Entries.Where(e => IsSearchable(e, archive));
        }

        // UI / mobs / effects — keep sheet expansion, but strip any khan body pieces that leaked in.
        return IconSheets.Expand(archive.Entries)
            .Where(e => IsSearchable(e, archive) && !IsCharacterModelAsset(e));
    }

    private static IEnumerable<ArchiveEntry> EnumerateItemCandidates(IArchive archive)
    {
        if (archive is UnityBundleArchive unity)
        {
            foreach (ArchiveEntry entry in unity.RepresentativeImageEntries())
            {
                if (IsCharacterModelAsset(entry))
                {
                    continue;
                }

                if (IsInventoryIcon(entry))
                {
                    yield return entry;
                }
            }

            yield break;
        }

        // Item sheets only (not skill/spell, not passthrough body EPFs).
        foreach (ArchiveEntry entry in LegendItems.Expand(archive.Entries))
        {
            if (IsCharacterModelAsset(entry))
            {
                continue;
            }

            if (entry.FrameIndex is not null
                || LegendItems.IsItemGraphic(entry)
                || LegendItems.IsRgbaItem(entry))
            {
                yield return entry;
            }
        }
    }

    private static bool IsInventoryIcon(ArchiveEntry entry)
    {
        if (LegendItems.IsItemGraphic(entry) || LegendItems.IsRgbaItem(entry))
        {
            return true;
        }

        return LodRAssets.IsIconSpriteName(entry.DisplayName)
            || LodRAssets.IsIconSpriteName(entry.Name);
    }

    /// <summary>Khan / LoD R body &amp; gear, aisling dolls — not inventory icons.</summary>
    private static bool IsCharacterModelAsset(ArchiveEntry entry)
    {
        if (KhanPieces.TryParse(entry.Name, out _)
            || KhanPieces.TryParse(entry.DisplayName, out _))
        {
            return true;
        }

        if (LodRAssets.IsBodyPrefix(entry.DisplayName)
            || LodRAssets.IsBodyPrefix(entry.Name))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(entry.DisplayName);
        if (stem.StartsWith("aisling", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stem = Path.GetFileNameWithoutExtension(entry.Name);
        return stem.StartsWith("aisling", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tall/opaque crops (paper-dolls, costume shots) vs small inventory icons.
    /// </summary>
    private static bool LooksLikeCharacterQuery(ImageMatcher.QueryImage query)
    {
        PixelBuffer px = query.Pixels;
        if (px.Width < 40 || px.Height < 40)
        {
            return false;
        }

        // Zoomed viewer icons / single punched islands (gloves, boots, swords) are tall
        // but must keep Items enabled — otherwise Find-from-image never sees item###_N.
        if (query.Objects.Length <= 1)
        {
            int maxEdge = Math.Max(px.Width, px.Height);
            if (query.Objects.Length == 1)
            {
                PixelBuffer island = query.Objects[0].Pixels;
                maxEdge = Math.Max(island.Width, island.Height);
            }

            // Paper-doll / costume crops are usually larger than a lone inventory tile.
            if (maxEdge < 180)
            {
                return false;
            }
        }

        // Inventory icons are usually square-ish; characters / costumes are taller.
        // After background punch, a 100×85 chicken shot often becomes ~49×61.
        float aspect = px.Height / (float)Math.Max(1, px.Width);
        if (query.IsScreenshot && aspect >= 1.10f)
        {
            return true;
        }

        if (aspect >= 1.15f && px.Height >= 56)
        {
            return true;
        }

        if (aspect >= 1.05f && px.Height >= 48 && px.Width * px.Height >= 40 * 48)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fast perceptual gate for icon↔icon compares. Screenshots / cluttered scenes skip
    /// the whole-image hash (a bag won't match one tile); object crops gate inside BestMatch.
    /// </summary>
    private static bool LooksPlausible(ImageMatcher.QueryImage query, PixelBuffer sprite)
    {
        if (query.IsScreenshot || query.IsCluttered)
        {
            return true;
        }

        // Tiny motion tips / debris (e.g. 11×8 axe head) get inflated NCC scores against
        // a full costume crop and crowd out the real coat / head / weapon.
        int sw = sprite.Width;
        int sh = sprite.Height;
        int area = sw * sh;
        int qArea = Math.Max(1, query.Pixels.Width * query.Pixels.Height);
        if (area < 100 || Math.Max(sw, sh) < 14)
        {
            return false;
        }

        // Do NOT reject normal inventory tiles (≈30×30) against a zoomed viewer crop
        // (≈200×200). The old area×20 gate hid item007_204 at 95% NCC. Only drop
        // sprites that are truly tiny relative to a large costume / scene query.
        float qAspect = query.Pixels.Height / (float)Math.Max(1, query.Pixels.Width);
        bool likelyCostume = qAspect >= 1.15f && query.Pixels.Height >= 80;
        if (likelyCostume && area * 40 < qArea)
        {
            return false;
        }

        if (!likelyCostume && area * 100 < qArea)
        {
            return false;
        }

        ulong avg = ImageMatcher.AverageHash(sprite);
        ulong diff = ImageMatcher.DifferenceHash(sprite);
        int hamm = Math.Min(
            ImageMatcher.Hamming(query.AverageHash, avg),
            ImageMatcher.Hamming(query.DifferenceHash, diff));
        return hamm <= 34;
    }

    private static bool IsSearchable(ArchiveEntry entry, IArchive archive)
    {
        if (entry.Name.Equals(UnityBundleArchive.MapPreviewName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string ext = entry.Extension.ToLowerInvariant();
        if (archive is UnityBundleArchive)
        {
            return ext is ".sprite" or ".texture2d";
        }

        return ext is ".epf" or ".mpf" or ".hpf" or ".spf" or ".efa" or ".rgba" or ".png" or ".bmp";
    }

    private static PixelBuffer? Decode(IArchive archive, ArchiveEntry entry)
    {
        try
        {
            if (archive is UnityBundleArchive unity)
            {
                UnityObjectPreview preview = unity.Preview(entry, 0, animateSequence: false);
                return preview.Pixels;
            }

            PreviewResult result = PreviewBuilder.Build(
                archive,
                entry,
                frameIndex: entry.FrameIndex ?? 0,
                paletteOverride: null,
                animationGroup: "All",
                additive: false,
                overlayEntries: null,
                contactSheet: false);
            return result.Pixels;
        }
        catch
        {
            return null;
        }
    }
}
