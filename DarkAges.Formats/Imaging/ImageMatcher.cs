namespace DarkAges.Formats.Imaging;

/// <summary>
/// Reverse-image helpers for DA / LoD R sprites: perceptual prefilter +
/// masked luminance NCC blended with color agreement.
/// </summary>
public static class ImageMatcher
{
    /// <summary>Scales used when locating a small sprite inside a larger screenshot.</summary>
    public static readonly float[] PatchScales = [0.7f, 0.85f, 1f, 1.15f, 1.35f, 1.6f];

    /// <summary>Scales used when comparing two similarly sized icons.</summary>
    public static readonly float[] IconScales = [0.85f, 1f, 1.15f];

    public static PixelBuffer PrepareSprite(PixelBuffer source)
    {
        PixelBuffer cropped = source.CropVisible(0, out _, out _);
        if (!cropped.HasVisiblePixels())
        {
            return cropped;
        }

        // Cap template size so sliding-window search stays interactive.
        return cropped.Width > 96 || cropped.Height > 96
            ? cropped.Contain(96, 96)
            : cropped;
    }

    public static PixelBuffer PrepareQuery(PixelBuffer source, int maxEdge = 640)
    {
        // PNG/JPG exports often sit on a solid desk/UI color — punch that so CropVisible works.
        // Edge flood first (inventory chrome), then corner-uniform punch as a fallback.
        PixelBuffer punched = PunchEdgeBackground(source);
        if (ReferenceEquals(punched, source) || OpaqueFraction(punched) > 0.92f)
        {
            punched = PunchUniformBackground(source);
        }

        PixelBuffer cropped = punched.CropVisible(1, out _, out _);
        if (!cropped.HasVisiblePixels())
        {
            cropped = source.CropVisible(0, out _, out _);
        }

        return cropped.Fit(maxEdge, maxEdge);
    }

    /// <summary>
    /// Preprocess the query once per search. Reuse across every candidate sprite.
    /// Screenshots are cut into overlapping blocks so later matching can skip empty regions.
    /// Cluttered inventory / bag shots also get discrete object crops for multi-item ID.
    /// </summary>
    public static QueryImage Prepare(PixelBuffer source, int maxEdge = 640, int blockSize = 8)
    {
        PixelBuffer hay = PrepareQuery(source, maxEdge);
        float opaque = OpaqueFraction(hay);
        // True screenshots stay large after bg punch; tight icon/costume crops do not.
        bool screenshot = hay.Width >= 220
            && hay.Height >= 220
            && opaque > 0.45f;
        int stride = Math.Clamp(blockSize, 1, 32);
        SegmentCell[] segments = screenshot
            ? BuildSegments(hay, cellSize: Math.Clamp(stride * 8, 48, 96))
            : [];

        ObjectRegion[] objects = ExtractObjectRegions(hay, maxRegions: 48);
        // Multiple discrete icons / tiles → match each island instead of the whole collage.
        bool cluttered = objects.Length >= 2
            && (screenshot || (hay.Width >= 120 && hay.Height >= 120 && objects.Length >= 3));
        if (!screenshot && cluttered)
        {
            screenshot = true;
        }

        return new QueryImage(
            hay,
            AverageHash(hay),
            DifferenceHash(hay),
            screenshot,
            stride,
            segments,
            objects,
            cluttered);
    }

    /// <summary>
    /// Flood from image edges through near-uniform UI/desk chrome so inventory icons become
    /// separate opaque islands (needed for multi-item identification in cluttered shots).
    /// </summary>
    public static PixelBuffer PunchEdgeBackground(PixelBuffer source, int tolerance = 28)
    {
        if (source.Width < 16 || source.Height < 16)
        {
            return source;
        }

        int w = source.Width;
        int h = source.Height;
        byte[] bgra = source.Bgra;
        int pixels = w * h;
        var visited = new bool[pixels];
        var queue = new Queue<int>(Math.Min(pixels / 4, 8192));

        void EnqueueEdge(int x, int y)
        {
            int i = y * w + x;
            if (visited[i])
            {
                return;
            }

            visited[i] = true;
            queue.Enqueue(i);
        }

        for (int x = 0; x < w; x++)
        {
            EnqueueEdge(x, 0);
            EnqueueEdge(x, h - 1);
        }

        for (int y = 1; y < h - 1; y++)
        {
            EnqueueEdge(0, y);
            EnqueueEdge(w - 1, y);
        }

        // Seed color = median-ish average of edge samples already queued.
        long sb = 0, sg = 0, sr = 0;
        int samples = 0;
        foreach (int i in queue)
        {
            int p = i * 4;
            sb += bgra[p];
            sg += bgra[p + 1];
            sr += bgra[p + 2];
            samples++;
        }

        if (samples < 8)
        {
            return source;
        }

        int tb = (int)(sb / samples);
        int tg = (int)(sg / samples);
        int tr = (int)(sr / samples);

        var mask = new bool[pixels];
        int cleared = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int p = i * 4;
            if (Math.Abs(bgra[p] - tb) > tolerance
                || Math.Abs(bgra[p + 1] - tg) > tolerance
                || Math.Abs(bgra[p + 2] - tr) > tolerance)
            {
                continue;
            }

            mask[i] = true;
            cleared++;
            int x = i % w;
            int y = i / w;
            void Try(int nx, int ny)
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                {
                    return;
                }

                int ni = ny * w + nx;
                if (visited[ni])
                {
                    return;
                }

                visited[ni] = true;
                queue.Enqueue(ni);
            }

            Try(x - 1, y);
            Try(x + 1, y);
            Try(x, y - 1);
            Try(x, y + 1);
        }

        // Need a meaningful punch without wiping the whole image.
        if (cleared < pixels * 0.08 || cleared > pixels * 0.92)
        {
            return source;
        }

        var copy = source.Clone();
        byte[] dest = copy.Bgra;
        for (int i = 0; i < pixels; i++)
        {
            if (!mask[i])
            {
                continue;
            }

            int p = i * 4;
            dest[p] = 0;
            dest[p + 1] = 0;
            dest[p + 2] = 0;
            dest[p + 3] = 0;
        }

        return copy;
    }

    /// <summary>
    /// Clear near-uniform corner background to alpha 0 so character/item crops tighten.
    /// </summary>
    public static PixelBuffer PunchUniformBackground(PixelBuffer source, int tolerance = 30)
    {
        if (source.Width < 8 || source.Height < 8)
        {
            return source;
        }

        byte[] bgra = source.Bgra;
        void Sample(int x, int y, out int b, out int g, out int r)
        {
            int i = (y * source.Width + x) * 4;
            b = bgra[i];
            g = bgra[i + 1];
            r = bgra[i + 2];
        }

        Sample(0, 0, out int b0, out int g0, out int r0);
        Sample(source.Width - 1, 0, out int b1, out int g1, out int r1);
        Sample(0, source.Height - 1, out int b2, out int g2, out int r2);
        Sample(source.Width - 1, source.Height - 1, out int b3, out int g3, out int r3);
        int sb = (b0 + b1 + b2 + b3) / 4;
        int sg = (g0 + g1 + g2 + g3) / 4;
        int sr = (r0 + r1 + r2 + r3) / 4;

        // Corners disagree → probably not a flat backdrop.
        if (Math.Abs(b0 - sb) > tolerance || Math.Abs(g0 - sg) > tolerance || Math.Abs(r0 - sr) > tolerance
            || Math.Abs(b1 - sb) > tolerance || Math.Abs(g1 - sg) > tolerance || Math.Abs(r1 - sr) > tolerance
            || Math.Abs(b2 - sb) > tolerance || Math.Abs(g2 - sg) > tolerance || Math.Abs(r2 - sr) > tolerance
            || Math.Abs(b3 - sb) > tolerance || Math.Abs(g3 - sg) > tolerance || Math.Abs(r3 - sr) > tolerance)
        {
            return source;
        }

        var copy = source.Clone();
        byte[] dest = copy.Bgra;
        int cleared = 0;
        for (int i = 0; i < dest.Length; i += 4)
        {
            if (Math.Abs(dest[i] - sb) <= tolerance
                && Math.Abs(dest[i + 1] - sg) <= tolerance
                && Math.Abs(dest[i + 2] - sr) <= tolerance)
            {
                dest[i] = 0;
                dest[i + 1] = 0;
                dest[i + 2] = 0;
                dest[i + 3] = 0;
                cleared++;
            }
        }

        // If we wiped almost everything, bail.
        if (cleared > source.Width * source.Height * 0.97)
        {
            return source;
        }

        return copy;
    }

    public static ulong AverageHash(PixelBuffer source, int size = 8)
    {
        size = Math.Clamp(size, 4, 16);
        PixelBuffer cropped = source.CropVisible(0, out _, out _);
        if (!cropped.HasVisiblePixels())
        {
            return 0;
        }

        PixelBuffer small = cropped.Resize(size, size);
        long sum = 0;
        var lum = new byte[size * size];
        for (int i = 0, p = 0; i < lum.Length; i++, p += 4)
        {
            byte b = small.Bgra[p];
            byte g = small.Bgra[p + 1];
            byte r = small.Bgra[p + 2];
            byte a = small.Bgra[p + 3];
            byte v = a < 16 ? (byte)0 : Luma(r, g, b);
            lum[i] = v;
            sum += v;
        }

        byte avg = (byte)(sum / lum.Length);
        ulong hash = 0;
        for (int i = 0; i < lum.Length; i++)
        {
            if (lum[i] >= avg)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    /// <summary>Horizontal gradient hash — better at structure than average-hash alone.</summary>
    public static ulong DifferenceHash(PixelBuffer source, int size = 8)
    {
        size = Math.Clamp(size, 4, 16);
        PixelBuffer cropped = source.CropVisible(0, out _, out _);
        if (!cropped.HasVisiblePixels())
        {
            return 0;
        }

        PixelBuffer small = cropped.Resize(size + 1, size);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i0 = (y * (size + 1) + x) * 4;
                int i1 = i0 + 4;
                byte a0 = small.Bgra[i0 + 3];
                byte a1 = small.Bgra[i1 + 3];
                byte l0 = a0 < 16 ? (byte)0 : Luma(small.Bgra[i0 + 2], small.Bgra[i0 + 1], small.Bgra[i0]);
                byte l1 = a1 < 16 ? (byte)0 : Luma(small.Bgra[i1 + 2], small.Bgra[i1 + 1], small.Bgra[i1]);
                if (l0 > l1)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int Hamming(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    public static MatchResult BestMatch(
        PixelBuffer query,
        PixelBuffer sprite,
        float[]? scales = null) =>
        BestMatch(Prepare(query), sprite, scales);

    public static MatchResult BestMatch(
        QueryImage query,
        PixelBuffer sprite,
        float[]? scales = null)
    {
        PixelBuffer hay = query.Pixels;
        PixelBuffer needleBase = PrepareSprite(sprite);
        if (!hay.HasVisiblePixels() || !needleBase.HasVisiblePixels())
        {
            return MatchResult.None;
        }

        MatchResult primary = MatchResult.None;

        // Cluttered bag / inventory shots: score each extracted object crop as its own icon.
        if (query.IsCluttered && query.Objects.Length > 0)
        {
            primary = BestObjectRegionMatch(query, needleBase, scales ?? IconScales);
            // Strong region hit — skip sliding the whole collage (slow + noisy).
            if (primary.Score >= 0.42f)
            {
                return primary;
            }
        }

        // Prefer whole-image compare when the sprite is a large fraction of the query
        // (composites / icons). Only slide a window for small sprites in big screenshots.
        bool needleMuchSmaller = needleBase.Width * 2 <= hay.Width && needleBase.Height * 2 <= hay.Height;
        bool patchSearch = query.IsScreenshot && needleMuchSmaller;

        MatchResult scene;
        if (!patchSearch)
        {
            scene = BestIconMatch(query, needleBase, scales ?? IconScales);
        }
        else
        {
            float[] useScales = scales ?? (query.Stride >= 8
                ? [0.85f, 1f, 1.25f]
                : PatchScales);
            scene = BestPatchMatch(query, needleBase, useScales);
        }

        if (scene.Score > primary.Score)
        {
            primary = scene;
        }

        // Never score the full cluttered hay as one icon — that washes out individual items.
        if (query.IsCluttered)
        {
            return primary;
        }

        if (query.IsScreenshot && primary.Score > 0.01f && needleMuchSmaller)
        {
            int nw = Math.Clamp(needleBase.Width, 4, hay.Width);
            int nh = Math.Clamp(needleBase.Height, 4, hay.Height);
            int x = Math.Clamp(primary.X, 0, Math.Max(0, hay.Width - nw));
            int y = Math.Clamp(primary.Y, 0, Math.Max(0, hay.Height - nh));
            PixelBuffer patch = CropRect(hay, x, y, nw, nh);
            float icon = NormalizedIconScore(patch, needleBase);
            if (icon > primary.Score)
            {
                return new MatchResult(icon, x, y);
            }

            return primary;
        }

        float whole = NormalizedIconScore(hay, needleBase);
        if (whole > primary.Score)
        {
            return new MatchResult(whole, primary.X, primary.Y);
        }

        return primary;
    }

    private static MatchResult BestObjectRegionMatch(
        QueryImage query,
        PixelBuffer needleBase,
        float[] scales)
    {
        ulong needleAvg = AverageHash(needleBase);
        ulong needleDiff = DifferenceHash(needleBase);
        float best = 0;
        int bestX = 0;
        int bestY = 0;

        foreach (ObjectRegion obj in query.Objects)
        {
            // Skip regions that look nothing like this sprite (cheap gate).
            int hamm = Math.Min(
                Hamming(obj.AverageHash, needleAvg),
                Hamming(obj.DifferenceHash, needleDiff));
            if (hamm > 30)
            {
                continue;
            }

            if (!AspectCompatible(obj.Pixels.Width, obj.Pixels.Height, needleBase.Width, needleBase.Height, maxRatio: 3.5f))
            {
                continue;
            }

            var regionQuery = new QueryImage(
                obj.Pixels,
                obj.AverageHash,
                obj.DifferenceHash,
                IsScreenshot: false,
                Stride: 1,
                Segments: [],
                Objects: [],
                IsCluttered: false);

            MatchResult local = BestIconMatch(regionQuery, needleBase, scales);
            float icon = NormalizedIconScore(obj.Pixels, needleBase);
            float score = Math.Max(local.Score, icon);
            if (score > best)
            {
                best = score;
                bestX = obj.X + local.X;
                bestY = obj.Y + local.Y;
            }
        }

        return new MatchResult(best, bestX, bestY);
    }

    /// <summary>
    /// Resize both images into the same box and score hash + opaque pixel distance.
    /// Strong for inventory icons; weaker alone for full characters (use with NCC).
    /// </summary>
    public static float NormalizedIconScore(PixelBuffer a, PixelBuffer b, int box = 48)
    {
        PixelBuffer aa = PrepareSprite(PunchUniformBackground(a)).Contain(box, box);
        PixelBuffer bb = PrepareSprite(b).Contain(box, box);
        aa = CenterPad(aa, box, box);
        bb = CenterPad(bb, box, box);

        int hammAvg = Hamming(AverageHash(aa), AverageHash(bb));
        int hammDiff = Hamming(DifferenceHash(aa), DifferenceHash(bb));
        int hamm = Math.Min(hammAvg, hammDiff);
        // Require some perceptual agreement or the pixel score is noise.
        if (hamm > 26)
        {
            return Math.Clamp(1f - hamm / 40f, 0, 0.35f);
        }

        long sad = 0;
        long count = 0;
        byte[] pa = aa.Bgra;
        byte[] pb = bb.Bgra;
        for (int i = 0; i < pa.Length; i += 4)
        {
            bool oa = pa[i + 3] >= 24;
            bool ob = pb[i + 3] >= 24;
            if (!oa && !ob)
            {
                continue;
            }

            if (oa != ob)
            {
                sad += 180;
                count++;
                continue;
            }

            sad += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
            count++;
        }

        if (count < 16)
        {
            return 0;
        }

        float hash = Math.Clamp(1f - hamm / 28f, 0, 1);
        float pix = Math.Clamp(1f - (float)(sad / (3.0 * count)) / 160f, 0, 1);
        return 0.40f * hash + 0.60f * pix;
    }

    private static PixelBuffer CenterPad(PixelBuffer source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return source;
        }

        var dest = new PixelBuffer(width, height);
        int ox = Math.Max(0, (width - source.Width) / 2);
        int oy = Math.Max(0, (height - source.Height) / 2);
        dest.Blit(source, ox, oy);
        return dest;
    }

    private static MatchResult BestIconMatch(QueryImage query, PixelBuffer needleBase, float[] scales)
    {
        PixelBuffer hay = query.Pixels;

        // Soft perceptual reject — JPEG / palette shifts inflate Hamming a lot.
        ulong needleAvg = AverageHash(needleBase);
        ulong needleDiff = DifferenceHash(needleBase);
        int hammAvg = Hamming(query.AverageHash, needleAvg);
        int hammDiff = Hamming(query.DifferenceHash, needleDiff);
        if (hammAvg > 30 && hammDiff > 30)
        {
            return MatchResult.None;
        }

        if (!AspectCompatible(hay.Width, hay.Height, needleBase.Width, needleBase.Height, maxRatio: 3.5f))
        {
            return MatchResult.None;
        }

        float best = 0;
        int bestX = 0;
        int bestY = 0;
        float hashBoost = HashAgreement(hammAvg, hammDiff);

        foreach (float scale in scales)
        {
            int nw = Math.Max(4, (int)Math.Round(needleBase.Width * scale));
            int nh = Math.Max(4, (int)Math.Round(needleBase.Height * scale));
            if (nw > hay.Width + 8 || nh > hay.Height + 8)
            {
                continue;
            }

            // Fit both into the same box for a fair whole-image compare.
            int boxW = Math.Max(nw, Math.Min(hay.Width, nw + 4));
            int boxH = Math.Max(nh, Math.Min(hay.Height, nh + 4));
            boxW = Math.Min(boxW, hay.Width);
            boxH = Math.Min(boxH, hay.Height);
            if (nw > boxW || nh > boxH)
            {
                float fit = Math.Min(boxW / (float)nw, boxH / (float)nh);
                nw = Math.Max(4, (int)Math.Round(nw * fit));
                nh = Math.Max(4, (int)Math.Round(nh * fit));
            }

            PixelBuffer needle = nw == needleBase.Width && nh == needleBase.Height
                ? needleBase
                : needleBase.Resize(nw, nh);

            // Center the smaller image inside the larger when sizes differ slightly.
            int ox = Math.Max(0, (hay.Width - nw) / 2);
            int oy = Math.Max(0, (hay.Height - nh) / 2);
            if (nw > hay.Width || nh > hay.Height)
            {
                continue;
            }

            float score = Combine(ScoreAt(hay, needle, ox, oy), hashBoost);
            if (score > best)
            {
                best = score;
                bestX = ox;
                bestY = oy;
            }

            // Also try origin alignment (common for identical crops).
            if (ox != 0 || oy != 0)
            {
                float origin = Combine(ScoreAt(hay, needle, 0, 0), hashBoost);
                if (origin > best)
                {
                    best = origin;
                    bestX = 0;
                    bestY = 0;
                }
            }
        }

        return new MatchResult(best, bestX, bestY);
    }

    private static MatchResult BestPatchMatch(QueryImage query, PixelBuffer needleBase, float[] scales)
    {
        PixelBuffer hay = query.Pixels;
        int stride = Math.Max(1, query.Stride);
        ulong needleAvg = AverageHash(needleBase);
        ulong needleDiff = DifferenceHash(needleBase);

        // Restrict sliding windows to screenshot blocks that look like the sprite.
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> regions = PromisingRegions(
            query,
            needleAvg,
            needleDiff,
            pad: Math.Max(needleBase.Width, needleBase.Height));

        float best = 0;
        int bestX = 0;
        int bestY = 0;

        foreach (float scale in scales)
        {
            int nw = Math.Max(4, (int)Math.Round(needleBase.Width * scale));
            int nh = Math.Max(4, (int)Math.Round(needleBase.Height * scale));
            if (nw > hay.Width || nh > hay.Height)
            {
                continue;
            }

            PixelBuffer needle = nw == needleBase.Width && nh == needleBase.Height
                ? needleBase
                : needleBase.Resize(nw, nh);

            // Coarse grid: user block size, but never coarser than ~1/4 of the template.
            int stepX = Math.Max(stride, Math.Max(1, nw / 8));
            int stepY = Math.Max(stride, Math.Max(1, nh / 8));
            int sample = stride >= 8 ? 2 : 1;
            float scaleBest = 0;
            int scaleX = 0;
            int scaleY = 0;

            foreach ((int x0, int y0, int x1, int y1) in regions)
            {
                int maxX = Math.Min(x1, hay.Width - nw);
                int maxY = Math.Min(y1, hay.Height - nh);
                int minX = Math.Clamp(x0, 0, Math.Max(0, maxX));
                int minY = Math.Clamp(y0, 0, Math.Max(0, maxY));
                for (int y = minY; y <= maxY; y += stepY)
                {
                    for (int x = minX; x <= maxX; x += stepX)
                    {
                        float score = ScoreAt(hay, needle, x, y, sample);
                        if (score > scaleBest)
                        {
                            scaleBest = score;
                            scaleX = x;
                            scaleY = y;
                        }
                    }
                }
            }

            // Refine around the coarse peak (full pixel density).
            if (scaleBest > 0.40f)
            {
                int rx0 = Math.Max(0, scaleX - stepX);
                int ry0 = Math.Max(0, scaleY - stepY);
                int rx1 = Math.Min(hay.Width - nw, scaleX + stepX);
                int ry1 = Math.Min(hay.Height - nh, scaleY + stepY);
                for (int y = ry0; y <= ry1; y++)
                {
                    for (int x = rx0; x <= rx1; x++)
                    {
                        float score = ScoreAt(hay, needle, x, y, sampleStep: 1);
                        if (score > scaleBest)
                        {
                            scaleBest = score;
                            scaleX = x;
                            scaleY = y;
                        }
                    }
                }
            }

            // Local perceptual check on the winning patch vs the needle.
            if (scaleBest > 0.35f)
            {
                PixelBuffer patch = CropRect(hay, scaleX, scaleY, nw, nh);
                int hamm = Hamming(AverageHash(patch), needleAvg);
                int dhamm = Hamming(DifferenceHash(patch), needleDiff);
                scaleBest = Combine(scaleBest, HashAgreement(hamm, dhamm));
            }

            if (scaleBest > best)
            {
                best = scaleBest;
                bestX = scaleX;
                bestY = scaleY;
            }
        }

        return new MatchResult(best, bestX, bestY);
    }

    /// <summary>
    /// Connected opaque islands — inventory tiles, dropped items, UI icons in a collage.
    /// </summary>
    public static ObjectRegion[] ExtractObjectRegions(PixelBuffer hay, int maxRegions = 48)
    {
        int w = hay.Width;
        int h = hay.Height;
        if (w < 24 || h < 24)
        {
            return [];
        }

        byte[] bgra = hay.Bgra;
        int pixels = w * h;
        var seen = new bool[pixels];
        var regions = new List<ObjectRegion>();
        int minArea = 48;
        int maxArea = Math.Max(minArea + 1, (w * h) / 2);
        int maxDim = Math.Min(192, Math.Max(w, h) * 2 / 3);

        for (int start = 0; start < pixels; start++)
        {
            if (seen[start] || bgra[start * 4 + 3] < 24)
            {
                continue;
            }

            int minX = start % w;
            int maxX = minX;
            int minY = start / w;
            int maxY = minY;
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            int area = 0;

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                area++;
                int x = i % w;
                int y = i / w;
                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }

                void Try(int nx, int ny)
                {
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                    {
                        return;
                    }

                    int ni = ny * w + nx;
                    if (seen[ni] || bgra[ni * 4 + 3] < 24)
                    {
                        return;
                    }

                    seen[ni] = true;
                    stack.Push(ni);
                }

                Try(x - 1, y);
                Try(x + 1, y);
                Try(x, y - 1);
                Try(x, y + 1);
            }

            int bw = maxX - minX + 1;
            int bh = maxY - minY + 1;
            if (area < minArea || area > maxArea || bw < 8 || bh < 8 || bw > maxDim || bh > maxDim)
            {
                continue;
            }

            float aspect = bw > bh ? bw / (float)bh : bh / (float)bw;
            if (aspect > 4f)
            {
                continue;
            }

            // Slight pad so we don't clip anti-aliased edges.
            int pad = 1;
            int x0 = Math.Max(0, minX - pad);
            int y0 = Math.Max(0, minY - pad);
            int x1 = Math.Min(w - 1, maxX + pad);
            int y1 = Math.Min(h - 1, maxY + pad);
            PixelBuffer crop = CropRect(hay, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            if (!crop.HasVisiblePixels())
            {
                continue;
            }

            regions.Add(new ObjectRegion(
                x0,
                y0,
                crop,
                AverageHash(crop),
                DifferenceHash(crop)));
        }

        if (regions.Count == 0)
        {
            return [];
        }

        // Prefer medium icon-sized islands; drop huge leftovers that swallow the scene.
        regions.Sort((a, b) =>
        {
            int aa = a.Pixels.Width * a.Pixels.Height;
            int bb = b.Pixels.Width * b.Pixels.Height;
            return aa.CompareTo(bb);
        });

        // Drop regions that heavily contain a smaller one (keep the tiles, not the bag frame).
        var kept = new List<ObjectRegion>(Math.Min(regions.Count, maxRegions));
        foreach (ObjectRegion candidate in regions)
        {
            bool swallows = false;
            foreach (ObjectRegion small in kept)
            {
                if (ContainsRoughly(candidate, small, cover: 0.85f))
                {
                    swallows = true;
                    break;
                }
            }

            if (swallows)
            {
                continue;
            }

            kept.Add(candidate);
            if (kept.Count >= maxRegions)
            {
                break;
            }
        }

        return kept.ToArray();
    }

    private static bool ContainsRoughly(ObjectRegion outer, ObjectRegion inner, float cover)
    {
        int ox1 = outer.X + outer.Pixels.Width;
        int oy1 = outer.Y + outer.Pixels.Height;
        int ix1 = inner.X + inner.Pixels.Width;
        int iy1 = inner.Y + inner.Pixels.Height;
        int x0 = Math.Max(outer.X, inner.X);
        int y0 = Math.Max(outer.Y, inner.Y);
        int x1 = Math.Min(ox1, ix1);
        int y1 = Math.Min(oy1, iy1);
        if (x1 <= x0 || y1 <= y0)
        {
            return false;
        }

        float inter = (x1 - x0) * (y1 - y0);
        float innerArea = Math.Max(1, inner.Pixels.Width * inner.Pixels.Height);
        return inter / innerArea >= cover
            && outer.Pixels.Width * outer.Pixels.Height > innerArea * 1.35f;
    }

    private static SegmentCell[] BuildSegments(PixelBuffer hay, int cellSize)
    {
        cellSize = Math.Clamp(cellSize, 32, 128);
        int step = Math.Max(cellSize / 2, 16);
        var cells = new List<SegmentCell>();
        for (int y = 0; y < hay.Height; y += step)
        {
            int h = Math.Min(cellSize, hay.Height - y);
            if (h < 16)
            {
                break;
            }

            for (int x = 0; x < hay.Width; x += step)
            {
                int w = Math.Min(cellSize, hay.Width - x);
                if (w < 16)
                {
                    break;
                }

                PixelBuffer cell = CropRect(hay, x, y, w, h);
                if (!cell.HasVisiblePixels())
                {
                    continue;
                }

                cells.Add(new SegmentCell(x, y, w, h, AverageHash(cell), DifferenceHash(cell)));
            }
        }

        return cells.ToArray();
    }

    private static IReadOnlyList<(int X0, int Y0, int X1, int Y1)> PromisingRegions(
        QueryImage query,
        ulong needleAvg,
        ulong needleDiff,
        int pad)
    {
        PixelBuffer hay = query.Pixels;
        if (query.Segments.Length == 0)
        {
            return [(0, 0, hay.Width, hay.Height)];
        }

        var regions = new List<(int, int, int, int)>();
        foreach (SegmentCell cell in query.Segments)
        {
            int hamm = Math.Min(
                Hamming(cell.AverageHash, needleAvg),
                Hamming(cell.DifferenceHash, needleDiff));
            // Loose gate — blocks only need to look vaguely related.
            if (hamm > 26)
            {
                continue;
            }

            int x0 = Math.Max(0, cell.X - pad);
            int y0 = Math.Max(0, cell.Y - pad);
            int x1 = Math.Min(hay.Width, cell.X + cell.Width + pad);
            int y1 = Math.Min(hay.Height, cell.Y + cell.Height + pad);
            regions.Add((x0, y0, x1, y1));
        }

        if (regions.Count == 0)
        {
            // Nothing looked similar — still do one coarse full-image pass.
            return [(0, 0, hay.Width, hay.Height)];
        }

        return MergeRegions(regions);
    }

    private static List<(int X0, int Y0, int X1, int Y1)> MergeRegions(
        List<(int X0, int Y0, int X1, int Y1)> regions)
    {
        regions.Sort((a, b) => a.Y0 != b.Y0 ? a.Y0.CompareTo(b.Y0) : a.X0.CompareTo(b.X0));
        var merged = new List<(int X0, int Y0, int X1, int Y1)>();
        foreach ((int x0, int y0, int x1, int y1) in regions)
        {
            if (merged.Count == 0)
            {
                merged.Add((x0, y0, x1, y1));
                continue;
            }

            (int mx0, int my0, int mx1, int my1) = merged[^1];
            bool overlaps = x0 <= mx1 + 8 && y0 <= my1 + 8 && x1 >= mx0 - 8 && y1 >= my0 - 8;
            if (overlaps)
            {
                merged[^1] = (
                    Math.Min(mx0, x0),
                    Math.Min(my0, y0),
                    Math.Max(mx1, x1),
                    Math.Max(my1, y1));
            }
            else
            {
                merged.Add((x0, y0, x1, y1));
            }
        }

        return merged;
    }

    /// <summary>
    /// Shape (masked luminance NCC) blended with color agreement on opaque pixels.
    /// <paramref name="sampleStep"/> &gt; 1 skips pixels during the coarse pass.
    /// </summary>
    private static float ScoreAt(PixelBuffer hay, PixelBuffer needle, int ox, int oy, int sampleStep = 1)
    {
        long count = 0;
        double sumH = 0;
        double sumN = 0;
        double sumHH = 0;
        double sumNN = 0;
        double sumHN = 0;
        double absR = 0;
        double absG = 0;
        double absB = 0;
        byte[] hb = hay.Bgra;
        byte[] nb = needle.Bgra;
        int hW = hay.Width;
        int nW = needle.Width;
        int step = Math.Max(1, sampleStep);

        if (ox < 0 || oy < 0 || ox + nW > hay.Width || oy + needle.Height > hay.Height)
        {
            return 0;
        }

        for (int y = 0; y < needle.Height; y += step)
        {
            int hy = (oy + y) * hW;
            int ny = y * nW;
            for (int x = 0; x < nW; x += step)
            {
                int ni = (ny + x) * 4;
                if (nb[ni + 3] < 24)
                {
                    continue;
                }

                int hi = (hy + ox + x) * 4;
                byte nr = nb[ni + 2];
                byte ng = nb[ni + 1];
                byte nbb = nb[ni];
                byte hr = hb[hi + 2];
                byte hg = hb[hi + 1];
                byte hbb = hb[hi];
                float hn = Luma(nr, ng, nbb);
                float hh = Luma(hr, hg, hbb);
                sumH += hh;
                sumN += hn;
                sumHH += hh * hh;
                sumNN += hn * hn;
                sumHN += hh * hn;
                absR += Math.Abs(hr - nr);
                absG += Math.Abs(hg - ng);
                absB += Math.Abs(hbb - nbb);
                count++;
            }
        }

        int minCount = step > 1 ? 12 : 24;
        if (count < minCount)
        {
            return 0;
        }

        double meanH = sumH / count;
        double meanN = sumN / count;
        double varH = sumHH - sumH * meanH;
        double varN = sumNN - sumN * meanN;
        double cov = sumHN - sumH * meanN;
        if (varH <= 1e-3 || varN <= 1e-3)
        {
            return 0;
        }

        double ncc = cov / Math.Sqrt(varH * varN);
        if (ncc <= 0)
        {
            return 0;
        }

        // Mean absolute RGB distance on opaque needle pixels (0 = identical color).
        double meanAbs = (absR + absG + absB) / (3.0 * count);
        // Tolerant of JPEG / palette shifts (DA sprites often remapped).
        float colorSim = (float)Math.Clamp(1.0 - meanAbs / 140.0, 0, 1);

        // Shape carries most of the score; color is a gentle nudge, not a hard gate.
        float shape = (float)Math.Clamp(ncc, 0, 1);
        return shape * (0.62f + 0.38f * colorSim);
    }

    private static float Combine(float shapeColorScore, float hashAgreement)
    {
        if (shapeColorScore <= 0)
        {
            return 0;
        }

        return Math.Clamp(shapeColorScore * (0.75f + 0.25f * hashAgreement), 0, 1);
    }

    private static float HashAgreement(int hammAvg, int hammDiff)
    {
        // 0 bits → 1.0; 32 bits → ~0
        float a = Math.Clamp(1f - hammAvg / 28f, 0, 1);
        float d = Math.Clamp(1f - hammDiff / 28f, 0, 1);
        return Math.Max(a, d);
    }

    private static bool AspectCompatible(int w0, int h0, int w1, int h1, float maxRatio)
    {
        if (w0 <= 0 || h0 <= 0 || w1 <= 0 || h1 <= 0)
        {
            return false;
        }

        float a0 = w0 / (float)h0;
        float a1 = w1 / (float)h1;
        float ratio = a0 > a1 ? a0 / a1 : a1 / a0;
        return ratio <= maxRatio;
    }

    private static float OpaqueFraction(PixelBuffer buf)
    {
        if (buf.Width <= 0 || buf.Height <= 0)
        {
            return 0;
        }

        int opaque = 0;
        byte[] bgra = buf.Bgra;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] >= 24)
            {
                opaque++;
            }
        }

        return opaque / (float)(buf.Width * buf.Height);
    }

    private static PixelBuffer CropRect(PixelBuffer source, int x, int y, int w, int h)
    {
        var dest = new PixelBuffer(w, h);
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(
                source.Bgra,
                ((y + row) * source.Width + x) * 4,
                dest.Bgra,
                row * w * 4,
                w * 4);
        }

        return dest;
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)((r * 3 + g * 6 + b) / 10);

    public readonly record struct SegmentCell(
        int X,
        int Y,
        int Width,
        int Height,
        ulong AverageHash,
        ulong DifferenceHash);

    public readonly record struct ObjectRegion(
        int X,
        int Y,
        PixelBuffer Pixels,
        ulong AverageHash,
        ulong DifferenceHash);

    public readonly record struct QueryImage(
        PixelBuffer Pixels,
        ulong AverageHash,
        ulong DifferenceHash,
        bool IsScreenshot,
        int Stride,
        SegmentCell[] Segments,
        ObjectRegion[] Objects,
        bool IsCluttered);

    public readonly record struct MatchResult(float Score, int X, int Y)
    {
        public static MatchResult None { get; } = new(0, 0, 0);
    }
}
