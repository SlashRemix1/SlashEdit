namespace DarkAges.Formats.Maps;

public readonly record struct WarpIssue(
    string Kind,
    int MapId,
    int DestMapId,
    int X,
    int Y,
    int DestX,
    int DestY,
    string Detail)
{
    public bool IsError => Kind is
        "Unknown dest" or "Missing dest" or "Missing source"
        or "Dest out of bounds" or "Source out of bounds" or "Overlap";

    public string Severity => IsError ? "error" : "warning";

    public bool Matches(MapWarp warp) =>
        Kind != "Unused map"
        && warp.MapId == MapId
        && warp.Covers(X, Y);
}

public static class WarpCheck
{
    public static IReadOnlyList<WarpIssue> Run(
        IEnumerable<MapWarp> warps,
        IReadOnlyDictionary<int, (int Width, int Height)> sizes,
        IReadOnlyCollection<int> knownMaps,
        bool unusedMaps = false)
    {
        var list = warps.Where(w => w.MapId > 0).ToList();
        var known = knownMaps.Count == 0 ? new HashSet<int>() : knownMaps.ToHashSet();
        foreach (int id in sizes.Keys)
        {
            known.Add(id);
        }

        var issues = new List<WarpIssue>();
        var returns = new HashSet<(int From, int To)>();
        var used = new HashSet<int>();
        foreach (MapWarp warp in list)
        {
            used.Add(warp.MapId);
            if (warp.DestMapId > 0)
            {
                used.Add(warp.DestMapId);
                returns.Add((warp.MapId, warp.DestMapId));
            }
        }

        foreach (MapWarp warp in list)
        {
            AddWarpIssues(issues, warp, list, sizes, known, returns);
        }

        var grouped = list.GroupBy(w => w.MapId);
        foreach (IGrouping<int, MapWarp> group in grouped)
        {
            List<MapWarp> items = group.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (!RectsOverlap(items[i], items[j]))
                    {
                        continue;
                    }

                    bool sameDest = items[i].DestMapId == items[j].DestMapId
                        && items[i].DestX == items[j].DestX
                        && items[i].DestY == items[j].DestY;
                    string kind = sameDest ? "Duplicate" : "Overlap";
                    string detail = sameDest
                        ? "Another warp covers these tiles with the same destination."
                        : $"Overlaps another warp to map {items[j].DestMapId}.";
                    issues.Add(Issue(kind, items[i], detail));
                    issues.Add(Issue(
                        kind,
                        items[j],
                        sameDest
                            ? "Another warp covers these tiles with the same destination."
                            : $"Overlaps another warp to map {items[i].DestMapId}."));
                }
            }
        }

        if (unusedMaps)
        {
            foreach (int id in known.OrderBy(n => n))
            {
                if (!used.Contains(id))
                {
                    issues.Add(new WarpIssue("Unused map", id, 0, 0, 0, -1, -1, $"Map {id} has no warps in or out."));
                }
            }
        }

        return issues;
    }

    public static IReadOnlyList<WarpIssue> For(
        MapWarp warp,
        IEnumerable<MapWarp> all,
        IReadOnlyDictionary<int, (int Width, int Height)> sizes,
        IReadOnlyCollection<int> knownMaps)
    {
        List<MapWarp> list = all.Where(w => !WarpTable.SameRect(w, warp) || w.MapId != warp.MapId).ToList();
        list.Add(warp);
        return Run(list, sizes, knownMaps).Where(i => i.Matches(warp)).ToList();
    }

    public static string Summary(IEnumerable<WarpIssue> issues)
    {
        var list = issues.ToList();
        if (list.Count == 0)
        {
            return "";
        }

        int errors = list.Count(i => i.IsError);
        int warnings = list.Count - errors;
        var parts = new List<string>();
        if (errors > 0)
        {
            parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        }

        if (warnings > 0)
        {
            parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        }

        return string.Join(", ", parts);
    }

    public static string Line(IEnumerable<WarpIssue> issues)
    {
        var list = issues.ToList();
        if (list.Count == 0)
        {
            return "";
        }

        return string.Join("; ", list.Select(i => i.Kind).Distinct());
    }

    private static void AddWarpIssues(
        List<WarpIssue> issues,
        MapWarp warp,
        IReadOnlyList<MapWarp> all,
        IReadOnlyDictionary<int, (int Width, int Height)> sizes,
        HashSet<int> known,
        HashSet<(int From, int To)> returns)
    {
        int dest = warp.DestMapId;
        if (known.Count > 0 && !known.Contains(warp.MapId))
        {
            issues.Add(Issue("Missing source", warp, $"Map {warp.MapId} is not in this client."));
        }

        if (sizes.TryGetValue(warp.MapId, out (int Width, int Height) sourceSize))
        {
            foreach ((int tx, int ty) in warp.Tiles())
            {
                if ((uint)tx >= (uint)sourceSize.Width || (uint)ty >= (uint)sourceSize.Height)
                {
                    issues.Add(Issue(
                        "Source out of bounds",
                        warp,
                        $"Exit ({tx},{ty}) is outside map {warp.MapId} ({sourceSize.Width}×{sourceSize.Height})."));
                    break;
                }
            }
        }

        if (warp.IsWorldMap)
        {
            return;
        }

        if (dest <= 0)
        {
            issues.Add(Issue("Unknown dest", warp, "Destination map id is missing."));
        }
        else if (known.Count > 0 && !known.Contains(dest))
        {
            issues.Add(Issue("Missing dest", warp, $"Map {dest} is not in this client."));
        }

        if (!warp.HasLanding)
        {
            issues.Add(Issue("No landing", warp, "Dest X/Y is unset (-1). Reverse landing may still work."));
        }
        else if (dest > 0 && sizes.TryGetValue(dest, out (int Width, int Height) destSize)
                 && ((uint)warp.DestX >= (uint)destSize.Width || (uint)warp.DestY >= (uint)destSize.Height))
        {
            issues.Add(Issue(
                "Dest out of bounds",
                warp,
                $"Landing ({warp.DestX},{warp.DestY}) is outside map {dest} ({destSize.Width}×{destSize.Height})."));
        }

        if (dest > 0 && !returns.Contains((dest, warp.MapId)))
        {
            issues.Add(Issue("One-way", warp, $"No return warp from map {dest} back to {warp.MapId}."));
        }

        if (dest == warp.MapId && warp.HasLanding && warp.Covers(warp.DestX, warp.DestY))
        {
            issues.Add(Issue("Self warp", warp, "Landing tile is on this same exit. Walking it loops in place."));
        }

        if (dest > 0 && warp.HasLanding)
        {
            List<MapWarp> reverse = all.Where(w => w.MapId == dest && w.DestMapId == warp.MapId).ToList();
            if (reverse.Count > 0 && reverse.All(w => !NearReturn(w, warp.DestX, warp.DestY)))
            {
                issues.Add(Issue(
                    "Landing mismatch",
                    warp,
                    $"Landing ({warp.DestX},{warp.DestY}) is not on or next to a return warp (±1 tile) from map {dest}."));
            }
        }
    }

    /// <summary>
    /// Return warps sit one tile off the landing so the player does not bounce back immediately.
    /// </summary>
    public static bool NearReturn(MapWarp reverse, int x, int y)
    {
        int x0 = Math.Min(reverse.X1, reverse.X2) - 1;
        int x1 = Math.Max(reverse.X1, reverse.X2) + 1;
        int y0 = Math.Min(reverse.Y1, reverse.Y2) - 1;
        int y1 = Math.Max(reverse.Y1, reverse.Y2) + 1;
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    private static bool RectsOverlap(MapWarp a, MapWarp b)
    {
        int ax0 = Math.Min(a.X1, a.X2);
        int ax1 = Math.Max(a.X1, a.X2);
        int ay0 = Math.Min(a.Y1, a.Y2);
        int ay1 = Math.Max(a.Y1, a.Y2);
        int bx0 = Math.Min(b.X1, b.X2);
        int bx1 = Math.Max(b.X1, b.X2);
        int by0 = Math.Min(b.Y1, b.Y2);
        int by1 = Math.Max(b.Y1, b.Y2);
        return ax0 <= bx1 && ax1 >= bx0 && ay0 <= by1 && ay1 >= by0;
    }

    private static WarpIssue Issue(string kind, MapWarp warp, string detail) =>
        new(kind, warp.MapId, warp.DestMapId, Math.Min(warp.X1, warp.X2), Math.Min(warp.Y1, warp.Y2), warp.DestX, warp.DestY, detail);
}
