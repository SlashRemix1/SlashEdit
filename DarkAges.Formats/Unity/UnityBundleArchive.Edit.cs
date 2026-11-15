using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;

namespace DarkAges.Formats.Unity;

public sealed partial class UnityBundleArchive
{
    private const int MaxMapEdge = 8_192;
    private const int MaxMapTiles = 80_000;

    public UnityObjectPreview ReplacePixels(ArchiveEntry entry, PixelBuffer pixels)
    {
        lock (_previewLock)
        {
            return ReplacePixelsCore(entry, pixels);
        }
    }

    private UnityObjectPreview ReplacePixelsCore(ArchiveEntry entry, PixelBuffer pixels)
    {
        if (!_byEntry.TryGetValue(entry.Name, out UnityObjectRef? obj))
        {
            return new UnityObjectPreview { Error = "Object not found in this bundle." };
        }

        try
        {
            AssetTypeValueField field = _manager.GetBaseField(obj.File, obj.Info);
            string typeName = TypeName(obj.Info);
            if (typeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase)
                || obj.Info.TypeId == (int)AssetClassID.Sprite)
            {
                StampSprite(obj, field, pixels);
            }
            else
            {
                WriteTexture(obj.File, obj.Info, field, pixels);
            }

            _dirtyFiles.Add(obj.File);
            _mapCache = null;
            _spriteCache.Clear();
            _textureCache.Clear();
            _sequencePixelCache.Clear();
            return Preview(entry);
        }
        catch (Exception ex)
        {
            return new UnityObjectPreview { Error = "Could not encode texture: " + ex.Message };
        }
    }

    public string Save()
    {
        if (_dirtyFiles.Count == 0)
        {
            return "No Unity texture edits to save.";
        }

        foreach (AssetsFileInstance file in _dirtyFiles)
        {
            int index = _bundle.file.GetFileIndex(file.name);
            if (index < 0)
            {
                continue;
            }

            _bundle.file.BlockAndDirInfo.DirectoryInfos[index].Replacer =
                new ContentReplacerFromAssets(file);
        }

        if (_bundle.file.DataIsCompressed)
        {
            _unpackedForSave?.Dispose();
            _unpackedForSave = new MemoryStream();
            _bundle.file.Unpack(new AssetsFileWriter(_unpackedForSave));
            _unpackedForSave.Position = 0;
            _bundle.file.Read(new AssetsFileReader(_unpackedForSave));
        }

        using var plain = new MemoryStream();
        _bundle.file.Write(new AssetsFileWriter(plain), 0);

        byte[] xor = Xor55Stream.Apply(plain.ToArray());
        string backup = SourcePath + ".bak";
        if (!File.Exists(backup))
        {
            File.Copy(SourcePath, backup, overwrite: false);
        }

        string temp = SourcePath + ".tmp";
        File.WriteAllBytes(temp, xor);
        try
        {
            File.Copy(temp, SourcePath, overwrite: true);
            File.Delete(temp);
        }
        catch (IOException)
        {
            return $"Wrote XOR'd bundle to {temp}. Close the original file and replace it with that copy. Backup: {backup}";
        }

        _dirtyFiles.Clear();
        return $"Saved {DisplayName} (backup {Path.GetFileName(backup)})";
    }

    private UnityObjectPreview PreviewMap(UnityObjectRef? only)
    {
        if (only is null && _mapCache is not null)
        {
            return new UnityObjectPreview
            {
                Pixels = _mapCache,
                Metadata = _mapCacheNote ?? "Composed LoD R tilemap"
            };
        }

        try
        {
            EnsureDependencies();
            var meta = new StringBuilder();
            if (_depLog.Length > 0)
            {
                meta.Append(_depLog);
            }
            PixelBuffer? pixels = ComposeTilemaps(only, meta);
            if (pixels is null)
            {
                pixels = FirstNamedTexture("MiniMap", meta);
            }

            if (pixels is null)
            {
                pixels = ComposeSpriteRenderers(meta);
            }

            if (only is null && pixels is not null)
            {
                _mapCache = pixels;
                _mapCacheNote = meta.ToString();
            }

            if (pixels is null)
            {
                return new UnityObjectPreview
                {
                    Error = "Could not compose this map (no tile sprites resolved).",
                    Metadata = meta.ToString()
                };
            }

            return new UnityObjectPreview { Pixels = pixels, Metadata = meta.ToString() };
        }
        catch (Exception ex)
        {
            return new UnityObjectPreview { Error = ex.Message, Metadata = "Map compose failed." };
        }
    }

    private PixelBuffer? ComposeTilemaps(UnityObjectRef? only, StringBuilder meta)
    {
        ReadGrid(out float cellW, out float cellH);
        meta.AppendLine($"Grid cell {cellW:0.#}×{cellH:0.#} isometric");

        var placements = new List<TileBlit>(1024);
        int maps = 0;
        int missing = 0;
        int tileCount = 0;
        int spriteSlots = 0;
        foreach (AssetsFileInstance file in _files)
        {
            foreach (AssetFileInfo info in file.file.AssetInfos)
            {
                if (!TypeName(info).Equals("Tilemap", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (only is not null && (only.File != file || only.Info.PathId != info.PathId))
                {
                    continue;
                }

                maps++;
                AssetTypeValueField field;
                try
                {
                    field = _manager.GetBaseField(file, info);
                }
                catch
                {
                    continue;
                }

                if (maps == 1)
                {
                    foreach (AssetsFileExternal ext in file.file.Metadata.Externals)
                    {
                        meta.AppendLine($"dep fileID {file.file.Metadata.Externals.IndexOf(ext) + 1}: {ext.PathName}");
                    }
                }

                List<PixelBuffer?> sprites = LoadTileSprites(file, field["m_TileSpriteArray"], ref missing);
                List<PixelBuffer?> assets = LoadTileSprites(file, field["m_TileAssetArray"], ref missing);
                spriteSlots += sprites.Count + assets.Count;
                foreach (AssetTypeValueField pair in ArrayItems(field["m_Tiles"]))
                {
                    tileCount++;
                    AssetTypeValueField pos = pair["first"];
                    AssetTypeValueField tile = pair["second"];
                    if (pos.IsDummy)
                    {
                        pos = pair.Children is { Count: > 0 } ? pair.Children[0] : pair;
                    }

                    if (tile.IsDummy)
                    {
                        tile = pair.Children is { Count: > 1 } ? pair.Children[1] : pair;
                    }

                    int tileX = FieldInt(pos, "x");
                    int tileY = FieldInt(pos, "y");
                    long spriteIndex = FieldLong(tile, "m_TileSpriteIndex");
                    long tileIndex = FieldLong(tile, "m_TileIndex");
                    PixelBuffer? sprite = IndexOrNull(sprites, spriteIndex) ?? IndexOrNull(assets, tileIndex);
                    if (sprite is null)
                    {
                        continue;
                    }

                    int px = (int)MathF.Round((tileX - tileY) * cellW * 0.5f);
                    int py = (int)MathF.Round((tileX + tileY) * cellH * 0.5f);
                    placements.Add(new TileBlit(px, py, tileX + tileY, sprite));
                    if (placements.Count >= MaxMapTiles)
                    {
                        break;
                    }
                }

                if (placements.Count >= MaxMapTiles)
                {
                    break;
                }
            }
        }

        meta.AppendLine($"{maps} tilemaps, {tileCount} cells, {placements.Count} blits, {spriteSlots} sprite slots, {missing} missing sprites");
        if (placements.Count == 0)
        {
            return null;
        }

        return BlitPlacements(placements, meta);
    }

    private PixelBuffer? ComposeSpriteRenderers(StringBuilder meta)
    {
        var placements = new List<TileBlit>(1024);
        int seen = 0;
        foreach (AssetsFileInstance file in _files)
        {
            foreach (AssetFileInfo info in file.file.AssetInfos)
            {
                if (!TypeName(info).Equals("SpriteRenderer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                seen++;
                if (seen > 400)
                {
                    meta.AppendLine($"SpriteRenderer fallback skipped ({seen}+ objects).");
                    return null;
                }

                AssetTypeValueField field;
                try
                {
                    field = _manager.GetBaseField(file, info);
                }
                catch
                {
                    continue;
                }

                PixelBuffer? sprite = LoadPPtrSprite(file, field["m_Sprite"]);
                if (sprite is null)
                {
                    continue;
                }

                placements.Add(new TileBlit(0, seen * 2, seen, sprite));
            }
        }

        meta.AppendLine($"SpriteRenderer fallback: {placements.Count} of {seen} sprites (unpositioned sheet)");
        if (placements.Count == 0)
        {
            return null;
        }

        // Pack in a contact sheet when world positions are not worth walking yet.
        int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(placements.Count)));
        int cell = 1;
        foreach (TileBlit blit in placements)
        {
            cell = Math.Max(cell, Math.Max(blit.Sprite.Width, blit.Sprite.Height));
        }

        int width = Math.Min(MaxMapEdge, columns * cell);
        int rows = (placements.Count + columns - 1) / columns;
        int height = Math.Min(MaxMapEdge, rows * cell);
        var canvas = new PixelBuffer(width, height);
        for (int i = 0; i < placements.Count; i++)
        {
            int x = (i % columns) * cell;
            int y = (i / columns) * cell;
            if (x >= width || y >= height)
            {
                break;
            }

            canvas.Blit(placements[i].Sprite, x, y);
        }

        return canvas;
    }

    private PixelBuffer? FirstNamedTexture(string name, StringBuilder meta)
    {
        foreach (AssetsFileInstance file in _files)
        {
            foreach (AssetFileInfo info in file.file.AssetInfos)
            {
                if (!TypeName(info).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    AssetTypeValueField field = _manager.GetBaseField(file, info);
                    if (!ReadName(field).Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    PixelBuffer? pixels = DecodeTexture(file, field, meta);
                    if (pixels is not null)
                    {
                        meta.AppendLine($"Fell back to {name} texture.");
                        return pixels;
                    }
                }
                catch
                {
                    // next
                }
            }
        }

        return null;
    }

    private static PixelBuffer? IndexOrNull(List<PixelBuffer?> list, long index)
    {
        if (index < 0 || index >= list.Count)
        {
            return null;
        }

        return list[(int)index];
    }

    private List<PixelBuffer?> LoadTileSprites(AssetsFileInstance file, AssetTypeValueField arrayField, ref int missing)
    {
        var sprites = new List<PixelBuffer?>();
        foreach (AssetTypeValueField item in ArrayItems(arrayField))
        {
            PixelBuffer? sprite = LoadPPtrSprite(file, item);
            if (sprite is null)
            {
                missing++;
            }

            sprites.Add(sprite);
        }

        return sprites;
    }

    private PixelBuffer? LoadPPtrSprite(AssetsFileInstance relativeTo, AssetTypeValueField node)
    {
        AssetTypeValueField pptr = node["m_Data"];
        if (pptr.IsDummy)
        {
            pptr = node["m_Sprite"];
        }

        if (pptr.IsDummy)
        {
            pptr = node;
        }

        if (pptr.IsDummy)
        {
            return null;
        }

        int fileId = FieldInt(pptr, "m_FileID");
        long pathId = FieldLong(pptr, "m_PathID");
        if (pathId == 0 && fileId == 0 && pptr["m_PathID"].IsDummy)
        {
            return null;
        }

        if (fileId != 0)
        {
            EnsureDependencies();
        }

        var key = (relativeTo.name + ":" + fileId, pathId);
        if (_spriteCache.TryGetValue(key, out PixelBuffer? cached))
        {
            return cached;
        }

        PixelBuffer? pixels = null;
        try
        {
            AssetExternal ext = _manager.GetExtAsset(relativeTo, pptr);
            if (ext.file is not null && ext.info is not null)
            {
                AssetTypeValueField field = ext.baseField ?? _manager.GetBaseField(ext.file, ext.info);
                string typeName = TypeName(ext.info);
                var sink = new StringBuilder();
                pixels = typeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase)
                    ? DecodeSprite(ext.file, field, sink)
                    : DecodeTexture(ext.file, field, sink);
            }
        }
        catch
        {
            pixels = null;
        }

        _spriteCache[key] = pixels;
        return pixels;
    }

    private void ReadGrid(out float cellW, out float cellH)
    {
        cellW = 56;
        cellH = 28;
        foreach (AssetsFileInstance file in _files)
        {
            foreach (AssetFileInfo info in file.file.AssetInfos)
            {
                if (!TypeName(info).Equals("Grid", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    AssetTypeValueField field = _manager.GetBaseField(file, info);
                    AssetTypeValueField size = field["m_CellSize"];
                    if (size.IsDummy)
                    {
                        return;
                    }

                    float x = FieldFloat(size, "x");
                    float y = FieldFloat(size, "y");
                    if (x > 1 && y > 1)
                    {
                        cellW = x;
                        cellH = y;
                    }

                    return;
                }
                catch
                {
                    return;
                }
            }
        }
    }

    private static PixelBuffer BlitPlacements(List<TileBlit> placements, StringBuilder meta)
    {
        placements.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.X.CompareTo(b.X));
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (TileBlit blit in placements)
        {
            int left = blit.X - blit.Sprite.Width / 2;
            int top = blit.Y - blit.Sprite.Height / 2;
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + blit.Sprite.Width);
            maxY = Math.Max(maxY, top + blit.Sprite.Height);
        }

        int width = Math.Clamp(maxX - minX, 1, MaxMapEdge);
        int height = Math.Clamp(maxY - minY, 1, MaxMapEdge);
        meta.AppendLine($"canvas {width}×{height} origin {minX},{minY}");
        var canvas = new PixelBuffer(width, height);
        foreach (TileBlit blit in placements)
        {
            int x = blit.X - blit.Sprite.Width / 2 - minX;
            int y = blit.Y - blit.Sprite.Height / 2 - minY;
            if (x >= width || y >= height || x + blit.Sprite.Width < 0 || y + blit.Sprite.Height < 0)
            {
                continue;
            }

            canvas.Blit(blit.Sprite, x, y);
        }

        return canvas;
    }

    private void StampSprite(UnityObjectRef spriteObj, AssetTypeValueField spriteField, PixelBuffer pixels)
    {
        AssetTypeValueField rd = spriteField["m_RD"];
        if (rd.IsDummy)
        {
            throw new InvalidDataException("Sprite has no renderer data.");
        }

        AssetExternal ext = _manager.GetExtAsset(spriteObj.File, rd["texture"]);
        if (ext.file is null || ext.info is null)
        {
            throw new InvalidDataException("Sprite texture is missing.");
        }

        AssetTypeValueField texField = ext.baseField ?? _manager.GetBaseField(ext.file, ext.info);
        var sink = new StringBuilder();
        PixelBuffer atlas = DecodeTexture(ext.file, texField, sink)
            ?? throw new InvalidDataException("Could not decode sprite atlas.");
        AssetTypeValueField rect = rd["textureRect"];
        int x = 0;
        int y = 0;
        int w = atlas.Width;
        int h = atlas.Height;
        if (!rect.IsDummy)
        {
            SpriteRect(rect, atlas.Width, atlas.Height, out x, out y, out w, out h);
        }

        PixelBuffer fitted = pixels.Width == w && pixels.Height == h ? pixels : pixels.Resize(w, h);
        StampOverwrite(atlas, fitted, x, y);
        WriteTexture(ext.file, ext.info, texField, atlas);
        _dirtyFiles.Add(ext.file);
    }

    private void WriteTexture(
        AssetsFileInstance file,
        AssetFileInfo info,
        AssetTypeValueField field,
        PixelBuffer pixels)
    {
        TextureFile texture = TextureFile.ReadTextureFile(field);
        texture.m_StreamData.path = "";
        texture.m_StreamData.offset = 0;
        texture.m_StreamData.size = 0;
        texture.EncodeTextureRaw(DecodedFromPixels(texture, pixels), pixels.Width, pixels.Height, 50, false);
        texture.m_Width = pixels.Width;
        texture.m_Height = pixels.Height;
        texture.WriteTo(field);
        info.SetNewData(field);
        _dirtyFiles.Add(file);
    }

    private void EnsureDependencies()
    {
        if (_depsLoaded)
        {
            return;
        }

        _depsLoaded = true;
        string? dir = Path.GetDirectoryName(SourcePath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        foreach (AssetsFileInstance file in _files.ToArray())
        {
            try
            {
                _manager.LoadBundleDependencies(file, _bundle, dir);
            }
            catch
            {
                // Shared Asset/map may be absent for item/monster bundles.
            }

            foreach (AssetsFileExternal ext in file.file.Metadata.Externals)
            {
                string name = Path.GetFileName(ext.PathName.Replace("archive:/", "").Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(name) || name.Contains('.'))
                {
                    continue;
                }

                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate) && !candidate.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    LoadDependencyBundle(candidate);
                }
            }
        }

        string shared = Path.Combine(dir, "map");
        if (File.Exists(shared) && !shared.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            LoadDependencyBundle(shared);
        }

        AliasCabLookups();
    }

    private void AliasCabLookups()
    {
        foreach (AssetsFileInstance file in _manager.Files)
        {
            if (string.IsNullOrEmpty(file.name))
            {
                continue;
            }

            _manager.FileLookup[AssetsManager.GetFileLookupKey(file.name)] = file;
            string fileName = Path.GetFileName(file.name);
            if (!string.IsNullOrEmpty(fileName))
            {
                _manager.FileLookup[AssetsManager.GetFileLookupKey(fileName)] = file;
            }
        }

        _depLog.AppendLine("cabs: " + string.Join(", ", _manager.Files.Select(f => f.name).Take(12)));
    }

    private void LoadDependencyBundle(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            full = path;
        }

        if (!_loadedDeps.Add(full))
        {
            return;
        }

        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var xor = new Xor55Stream(fs);
            _extraStreams.Add(xor);
            bool unpack = fs.Length < 80L * 1024 * 1024;
            BundleFileInstance bun = _manager.LoadBundleFile(xor, path, unpack);
            IReadOnlyList<string> names = bun.file.GetAllFileNames();
            int loaded = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string cab = names[i];
                if (cab.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                    || cab.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                    || cab.EndsWith(".ress", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _manager.LoadAssetsFileFromBundle(bun, i, false);
                loaded++;
            }

            _depLog.AppendLine($"loaded {Path.GetFileName(path)} ({loaded} CABs, {names.Count} files)");
        }
        catch (Exception ex)
        {
            _depLog.AppendLine($"failed {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void StampOverwrite(PixelBuffer dest, PixelBuffer source, int destX, int destY)
    {
        int srcX0 = destX < 0 ? -destX : 0;
        int srcY0 = destY < 0 ? -destY : 0;
        int srcX1 = Math.Min(source.Width, dest.Width - destX);
        int srcY1 = Math.Min(source.Height, dest.Height - destY);
        if (srcX1 <= srcX0 || srcY1 <= srcY0)
        {
            return;
        }

        for (int sy = srcY0; sy < srcY1; sy++)
        {
            Buffer.BlockCopy(
                source.Bgra,
                (sy * source.Width + srcX0) * 4,
                dest.Bgra,
                ((destY + sy) * dest.Width + destX + srcX0) * 4,
                (srcX1 - srcX0) * 4);
        }
    }

    private static IEnumerable<AssetTypeValueField> ArrayItems(AssetTypeValueField field)
    {
        if (field.IsDummy)
        {
            yield break;
        }

        AssetTypeValueField array = field["Array"];
        IList<AssetTypeValueField>? children = array.IsDummy ? field.Children : array.Children;
        if (children is null)
        {
            yield break;
        }

        foreach (AssetTypeValueField child in children)
        {
            if (child.FieldName == "Array" && child.Children is { Count: > 0 })
            {
                foreach (AssetTypeValueField inner in child.Children)
                {
                    yield return inner;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static int FieldInt(AssetTypeValueField field, string name)
    {
        AssetTypeValueField child = field[name];
        if (child.IsDummy)
        {
            return 0;
        }

        try
        {
            return (int)child.AsLong;
        }
        catch
        {
            return (int)child.AsFloat;
        }
    }

    private static long FieldLong(AssetTypeValueField field, string name)
    {
        AssetTypeValueField child = field[name];
        if (child.IsDummy)
        {
            return 0;
        }

        try
        {
            return child.AsLong;
        }
        catch
        {
            return (long)child.AsFloat;
        }
    }

    private static float FieldFloat(AssetTypeValueField field, string name)
    {
        AssetTypeValueField child = field[name];
        if (child.IsDummy)
        {
            return 0;
        }

        try
        {
            return child.AsFloat;
        }
        catch
        {
            return child.AsLong;
        }
    }

    private readonly record struct TileBlit(int X, int Y, int Depth, PixelBuffer Sprite);
}
