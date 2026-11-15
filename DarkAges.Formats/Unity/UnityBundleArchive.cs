using System.Collections;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using DarkAges.Formats.Data;
using DarkAges.Formats.Imaging;
using DarkAges.Formats.IO;

namespace DarkAges.Formats.Unity;

public sealed partial class UnityBundleArchive : IArchive
{
    public const string MapPreviewName = "__map.preview";

    private readonly AssetsManager _manager = new();
    private readonly Stream _stream;
    private readonly BundleFileInstance _bundle;
    private readonly List<ArchiveEntry> _entries = [];
    private readonly List<AssetsFileInstance> _files = [];
    private readonly Dictionary<string, ArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnityObjectRef> _byEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string File, long PathId), PixelBuffer?> _spriteCache = [];
    private readonly Dictionary<(string File, long PathId), PixelBuffer?> _textureCache = [];
    private readonly HashSet<AssetsFileInstance> _dirtyFiles = [];
    private PixelBuffer? _mapCache;
    private string? _mapCacheNote;
    private MemoryStream? _unpackedForSave;
    private readonly List<Stream> _extraStreams = [];
    private readonly StringBuilder _depLog = new();
    private readonly HashSet<string> _loadedDeps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _previewLock = new();
    private readonly Dictionary<string, List<SpriteSeqFrame>> _sequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _entryGroup = new(StringComparer.OrdinalIgnoreCase);
    private bool _spriteIndexReady;
    private bool _depsLoaded;
    private bool _hasTilemap;

    public UnityBundleArchive(string path)
    {
        SourcePath = path;
        DisplayName = Path.GetFileName(path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _stream = new Xor55Stream(fs);
        bool unpack = fs.Length < 80L * 1024 * 1024;
        _bundle = _manager.LoadBundleFile(_stream, path, unpack);
        BuildEntries();
        _entries.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        if (_hasTilemap)
        {
            var preview = new ArchiveEntry(
                MapPreviewName,
                0,
                () => Stream.Null,
                displayName: "Map / composed tilemap");
            _entries.Insert(0, preview);
            _byName[MapPreviewName] = preview;
        }
    }

    public bool HasEdits => _dirtyFiles.Count > 0;

    public bool CanReplacePixels(ArchiveEntry entry) =>
        _byEntry.TryGetValue(entry.Name, out UnityObjectRef? obj)
        && IsTextureOrSprite(obj.Info);

    public string SourcePath { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;
    public ArchiveEntry? GetEntry(string name)
    {
        _byName.TryGetValue(name, out ArchiveEntry? entry);
        return entry;
    }

    public IEnumerator<ArchiveEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool LooksLike(string path) => Xor55Stream.LooksLikeUnityFs(path);

    public bool SpriteIndexReady => _spriteIndexReady;

    public UnityObjectPreview Preview(ArchiveEntry entry) => Preview(entry, frameIndex: 0, animateSequence: true);

    public UnityObjectPreview Preview(ArchiveEntry entry, int frameIndex, bool animateSequence)
    {
        lock (_previewLock)
        {
            return PreviewCore(entry, frameIndex, animateSequence);
        }
    }

    public int SequenceIndex(ArchiveEntry entry)
    {
        lock (_previewLock)
        {
            EnsureSpriteIndex();
            if (!_entryGroup.TryGetValue(entry.Name, out string? group)
                || !_sequences.TryGetValue(group, out List<SpriteSeqFrame>? frames)
                || frames.Count == 0)
            {
                return 0;
            }

            int idx = frames.FindIndex(f => f.EntryName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? 0 : idx;
        }
    }

    public void EnsureSpriteIndex()
    {
        lock (_previewLock)
        {
            EnsureSpriteIndexUnlocked();
        }
    }

    public IReadOnlyList<UnitySpriteClip> ListSpriteClips()
    {
        lock (_previewLock)
        {
            EnsureSpriteIndexUnlocked();
            var result = new List<UnitySpriteClip>(_sequences.Count);
            foreach (KeyValuePair<string, List<SpriteSeqFrame>> kv in _sequences)
            {
                var frames = new UnitySpriteFrame[kv.Value.Count];
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    SpriteSeqFrame frame = kv.Value[i];
                    frames[i] = new UnitySpriteFrame(frame.EntryName, frame.Frame, frame.PivotX, frame.PivotY);
                }

                result.Add(new UnitySpriteClip(kv.Key, IsEffectSequence(kv.Key), frames));
            }

            result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    public PixelBuffer? DecodeRawSprite(string entryName)
    {
        lock (_previewLock)
        {
            if (!_byEntry.TryGetValue(entryName, out UnityObjectRef? obj))
            {
                return null;
            }

            return DecodeCachedSprite(obj, new StringBuilder());
        }
    }

    private UnityObjectPreview PreviewCore(ArchiveEntry entry, int frameIndex, bool animateSequence)
    {
        if (entry.Name.Equals(MapPreviewName, StringComparison.OrdinalIgnoreCase))
        {
            return PreviewMap(only: null);
        }

        if (!_byEntry.TryGetValue(entry.Name, out UnityObjectRef? obj))
        {
            return new UnityObjectPreview { Error = "Object not found in this bundle." };
        }

        try
        {
            AssetTypeValueField field = _manager.GetBaseField(obj.File, obj.Info);
            string typeName = TypeName(obj.Info);
            string objectName = ReadName(field);
            if (!string.IsNullOrWhiteSpace(objectName)
                && !entry.DisplayName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            {
                entry.DisplayName = objectName;
            }

            var meta = new StringBuilder();
            meta.AppendLine($"{objectName}");
            meta.AppendLine($"{typeName}  pathId {obj.Info.PathId}  {obj.Info.ByteSize} bytes");
            meta.AppendLine($"{DisplayName}  UnityFS XOR 0x55");

            if (typeName.Equals("Tilemap", StringComparison.OrdinalIgnoreCase))
            {
                UnityObjectPreview composed = PreviewMap(obj);
                if (composed.Pixels is not null)
                {
                    return composed;
                }
            }

            if (obj.Info.TypeId == (int)AssetClassID.Texture2D || typeName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                PixelBuffer? pixels = DecodeTexture(obj.File, field, meta, obj.Info.PathId);
                return new UnityObjectPreview { Pixels = pixels, Metadata = meta.ToString(), ObjectName = objectName };
            }

            if (obj.Info.TypeId == (int)AssetClassID.Sprite || typeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
            {
                if (animateSequence)
                {
                    UnityObjectPreview? sequence = PreviewSpriteSequence(entry, objectName, field, meta, frameIndex);
                    if (sequence is not null)
                    {
                        return sequence;
                    }
                }

                PixelBuffer? pixels = DecodeSprite(obj.File, field, meta, out _, out _);
                if (pixels is not null)
                {
                    return new UnityObjectPreview
                    {
                        Pixels = pixels,
                        Metadata = meta.ToString(),
                        ObjectName = objectName
                    };
                }
            }

            if (obj.Info.TypeId == (int)AssetClassID.AudioClip || typeName.Equals("AudioClip", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? audio = ReadAudio(field);
                if (audio is { Length: > 0 })
                {
                    meta.AppendLine($"{audio.Length} bytes audio");
                    return new UnityObjectPreview { Audio = audio, Metadata = meta.ToString() };
                }
            }

            if (obj.Info.TypeId == (int)AssetClassID.TextAsset || typeName.Equals("TextAsset", StringComparison.OrdinalIgnoreCase))
            {
                string text = ReadText(field);
                if (!string.IsNullOrEmpty(text))
                {
                    return new UnityObjectPreview { Text = text, Metadata = meta.ToString() };
                }
            }

            return new UnityObjectPreview
            {
                Text = DumpFields(field, 10, 120),
                Metadata = meta.ToString()
            };
        }
        catch (Exception ex)
        {
            return new UnityObjectPreview { Error = ex.Message, Metadata = entry.DisplayName };
        }
    }

    public void Dispose()
    {
        _manager.UnloadAll();
        _stream.Dispose();
        _unpackedForSave?.Dispose();
        foreach (Stream extra in _extraStreams)
        {
            extra.Dispose();
        }
    }

    private void BuildEntries()
    {
        IReadOnlyList<string> names = _bundle.file.GetAllFileNames();
        for (int i = 0; i < names.Count; i++)
        {
            string cab = names[i];
            if (cab.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || cab.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)
                || cab.EndsWith(".ress", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AssetsFileInstance? fileInst;
            try
            {
                fileInst = _manager.LoadAssetsFileFromBundle(_bundle, i, false);
            }
            catch
            {
                continue;
            }

            if (fileInst is null)
            {
                continue;
            }

            _files.Add(fileInst);
            foreach (AssetFileInfo info in fileInst.file.AssetInfos)
            {
                string typeName = TypeName(info);
                if (typeName.Equals("Tilemap", StringComparison.OrdinalIgnoreCase))
                {
                    _hasTilemap = true;
                }

                if (!LodRAssets.IsListedType(typeName))
                {
                    continue;
                }

                string objectName = info.PathId.ToString();
                string fileName = UniqueName(Sanitize(objectName) + "." + typeName);
                var captured = new UnityObjectRef(fileInst, info);
                int size = info.ByteSize > int.MaxValue ? int.MaxValue : (int)info.ByteSize;
                var entry = new ArchiveEntry(
                    fileName,
                    size,
                    () => OpenObject(captured),
                    displayName: $"{typeName} / {objectName}");
                _entries.Add(entry);
                _byName[fileName] = entry;
                _byEntry[fileName] = captured;
            }
        }
    }

    private string UniqueName(string name)
    {
        if (!_byName.ContainsKey(name))
        {
            return name;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        int n = 2;
        string candidate;
        do
        {
            candidate = $"{stem}_{n}{ext}";
            n++;
        }
        while (_byName.ContainsKey(candidate));

        return candidate;
    }

    private static Stream OpenObject(UnityObjectRef obj)
    {
        int size = obj.Info.ByteSize > int.MaxValue ? int.MaxValue : (int)obj.Info.ByteSize;
        var copy = new byte[size];
        AssetsFileReader reader = obj.File.file.Reader;
        reader.Position = obj.File.file.Header.DataOffset + obj.Info.ByteOffset;
        reader.Read(copy, 0, copy.Length);
        return new MemoryStream(copy, writable: false);
    }

    private PixelBuffer? DecodeTexture(
        AssetsFileInstance fileInst,
        AssetTypeValueField field,
        StringBuilder meta,
        long pathId = 0)
    {
        var cacheKey = (fileInst.name, pathId);
        if (pathId != 0 && _textureCache.TryGetValue(cacheKey, out PixelBuffer? cached))
        {
            if (cached is not null)
            {
                meta.AppendLine($"{cached.Width}×{cached.Height}  cached");
            }

            return cached;
        }

        TextureFile texture = TextureFile.ReadTextureFile(field);
        meta.AppendLine($"{texture.m_Width}×{texture.m_Height}  format {texture.m_TextureFormat}");
        byte[]? data = texture.FillPictureData(fileInst);
        if (data is null || data.Length == 0)
        {
            BundleFileInstance? source = fileInst.parentBundle ?? _bundle;
            texture.SetPictureDataFromBundle(source);
            data = texture.pictureData;
        }

        if (data is null || data.Length == 0)
        {
            meta.AppendLine("No picture data (streamed or empty).");
            return null;
        }

        byte[]? decoded = texture.DecodeTextureRaw(data, false);
        if (decoded is null || decoded.Length == 0 || texture.m_Width <= 0 || texture.m_Height <= 0)
        {
            meta.AppendLine("Texture decode failed.");
            return null;
        }

        PixelBuffer decodedPixels = PixelsFromDecoded(texture, decoded);
        if (pathId != 0)
        {
            _textureCache[cacheKey] = decodedPixels;
        }

        return decodedPixels;
    }

    private static PixelBuffer PixelsFromDecoded(TextureFile texture, byte[] decoded)
    {
        int pixels = texture.m_Width * texture.m_Height;
        PixelBuffer buffer;
        if (texture.m_TextureFormat == (int)TextureFormat.RGB24 && decoded.Length >= pixels * 3 && decoded.Length < pixels * 4)
        {
            var bgra = new byte[pixels * 4];
            for (int i = 0, o = 0; i < pixels; i++, o += 4)
            {
                int s = i * 3;
                bgra[o] = decoded[s + 2];
                bgra[o + 1] = decoded[s + 1];
                bgra[o + 2] = decoded[s];
                bgra[o + 3] = 255;
            }

            buffer = PixelBuffer.FromBgra(texture.m_Width, texture.m_Height, bgra);
        }
        else if (DecoderReturnsRgba(texture.m_TextureFormat))
        {
            buffer = PixelBuffer.FromRgba(texture.m_Width, texture.m_Height, decoded);
        }
        else
        {
            buffer = PixelBuffer.FromBgra(texture.m_Width, texture.m_Height, decoded);
        }

        buffer.FlipVerticalInPlace();
        return buffer;
    }

    private static byte[] DecodedFromPixels(TextureFile texture, PixelBuffer pixels)
    {
        PixelBuffer stored = pixels.FlipVertical();
        if (!DecoderReturnsRgba(texture.m_TextureFormat))
        {
            return stored.Bgra;
        }

        byte[] rgba = new byte[stored.Bgra.Length];
        byte[] bgra = stored.Bgra;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return rgba;
    }

    private static bool DecoderReturnsRgba(int format) =>
        format is (int)TextureFormat.RGBA32 or (int)TextureFormat.RGB24;

    private UnityObjectPreview? PreviewSpriteSequence(
        ArchiveEntry entry,
        string objectName,
        AssetTypeValueField field,
        StringBuilder meta,
        int frameIndex)
    {
        EnsureSpriteIndexUnlocked();
        if (!_entryGroup.TryGetValue(entry.Name, out string? group))
        {
            if (!TryParseSequence(objectName, out group, out _))
            {
                group = objectName;
            }
        }

        if (!_sequences.TryGetValue(group, out List<SpriteSeqFrame>? frames) || frames.Count == 0)
        {
            return null;
        }

        bool effect = IsEffectSequence(group);
        if (frames.Count <= 1 && !effect)
        {
            return null;
        }

        int idx = Math.Clamp(frameIndex, 0, frames.Count - 1);
        List<PixelBuffer> sequence = DecodeGroupFrames(frames, effect);
        if (sequence.Count == 0)
        {
            return null;
        }

        idx = Math.Clamp(idx, 0, sequence.Count - 1);
        PixelBuffer pixels = sequence[idx];
        meta.AppendLine($"{group}  frame {idx + 1}/{sequence.Count}");
        if (effect)
        {
            meta.AppendLine("Dummy player at the sprite pivot. Play runs the whole effect.");
        }
        else
        {
            meta.AppendLine("Play steps the sprite frames without re-decoding.");
        }

        return new UnityObjectPreview
        {
            Pixels = pixels,
            Metadata = meta.ToString(),
            ObjectName = objectName,
            FrameCount = sequence.Count,
            CanAnimate = sequence.Count > 1,
            AutoPlay = effect && sequence.Count > 1,
            Frames = sequence
        };
    }

    private List<PixelBuffer> DecodeGroupFrames(List<SpriteSeqFrame> frames, bool effect)
    {
        var sequence = new List<PixelBuffer>(frames.Count);
        var sink = new StringBuilder();
        foreach (SpriteSeqFrame frame in frames)
        {
            if (!_byEntry.TryGetValue(frame.EntryName, out UnityObjectRef? obj))
            {
                continue;
            }

            PixelBuffer? raw = DecodeCachedSprite(obj, sink);
            if (raw is null)
            {
                continue;
            }

            sequence.Add(effect ? EffectStage.ComposeUnity(raw, frame.PivotX, frame.PivotY) : raw);
        }

        return sequence;
    }

    private PixelBuffer? DecodeCachedSprite(UnityObjectRef obj, StringBuilder meta)
    {
        var key = (obj.File.name, obj.Info.PathId);
        if (_spriteCache.TryGetValue(key, out PixelBuffer? cached))
        {
            return cached;
        }

        try
        {
            AssetTypeValueField field = _manager.GetBaseField(obj.File, obj.Info);
            PixelBuffer? pixels = DecodeSprite(obj.File, field, meta, out _, out _);
            _spriteCache[key] = pixels;
            return pixels;
        }
        catch
        {
            _spriteCache[key] = null;
            return null;
        }
    }

    private void EnsureSpriteIndexUnlocked()
    {
        if (_spriteIndexReady)
        {
            return;
        }

        foreach (KeyValuePair<string, UnityObjectRef> kv in _byEntry)
        {
            UnityObjectRef obj = kv.Value;
            if (obj.Info.TypeId != (int)AssetClassID.Sprite
                && !TypeName(obj.Info).Equals("Sprite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                AssetTypeValueField field = _manager.GetBaseField(obj.File, obj.Info);
                string name = ReadName(field);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = kv.Key;
                }

                if (_byName.TryGetValue(kv.Key, out ArchiveEntry? listed)
                    && !listed.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    listed.DisplayName = name;
                }

                ReadPivot(field, out float pivotX, out float pivotY);
                if (!TryParseSequence(name, out string group, out int frame))
                {
                    group = name;
                    frame = 0;
                }

                if (!_sequences.TryGetValue(group, out List<SpriteSeqFrame>? list))
                {
                    list = [];
                    _sequences[group] = list;
                }

                list.Add(new SpriteSeqFrame(kv.Key, frame, pivotX, pivotY));
                _entryGroup[kv.Key] = group;
            }
            catch
            {
                // skip objects the class database cannot read
            }
        }

        foreach (List<SpriteSeqFrame> list in _sequences.Values)
        {
            list.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        }

        _spriteIndexReady = true;
    }

    private bool IsEffectBundle()
    {
        string file = Path.GetFileName(SourcePath);
        return file.StartsWith("effect", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEffectSequence(string group) =>
        group.StartsWith("effect", StringComparison.OrdinalIgnoreCase)
        || group.StartsWith("efct", StringComparison.OrdinalIgnoreCase)
        || group.StartsWith("spell", StringComparison.OrdinalIgnoreCase)
        || IsEffectBundle();

    private static bool TryParseSequence(string name, out string group, out int frame)
    {
        group = name;
        frame = 0;
        int us = name.LastIndexOf('_');
        if (us <= 0 || us >= name.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(name[(us + 1)..], out frame))
        {
            return false;
        }

        group = name[..us];
        return group.Length > 0;
    }

    private static void ReadPivot(AssetTypeValueField field, out float pivotX, out float pivotY)
    {
        pivotX = 0.5f;
        pivotY = 0f;
        AssetTypeValueField pivot = field["m_Pivot"];
        if (pivot.IsDummy)
        {
            return;
        }

        try
        {
            pivotX = pivot["x"].AsFloat;
            pivotY = pivot["y"].AsFloat;
        }
        catch
        {
            // keep the default bottom-center pivot
        }
    }

    private PixelBuffer? DecodeSprite(AssetsFileInstance fileInst, AssetTypeValueField field, StringBuilder meta) =>
        DecodeSprite(fileInst, field, meta, out _, out _);

    private PixelBuffer? DecodeSprite(
        AssetsFileInstance fileInst,
        AssetTypeValueField field,
        StringBuilder meta,
        out float pivotX,
        out float pivotY)
    {
        ReadPivot(field, out pivotX, out pivotY);
        AssetTypeValueField rd = field["m_RD"];
        if (rd.IsDummy)
        {
            return null;
        }

        AssetExternal ext = _manager.GetExtAsset(fileInst, rd["texture"]);
        if (ext.file is null || ext.info is null)
        {
            return null;
        }

        AssetTypeValueField texField = ext.baseField ?? _manager.GetBaseField(ext.file, ext.info);
        PixelBuffer? full = DecodeTexture(ext.file, texField, meta, ext.info.PathId);
        if (full is null)
        {
            return null;
        }

        AssetTypeValueField rect = rd["textureRect"];
        if (rect.IsDummy)
        {
            return full;
        }

        SpriteRect(rect, full.Width, full.Height, out int x, out int y, out int w, out int h);
        meta.AppendLine($"sprite rect {x},{y} {w}×{h}  pivot {pivotX:0.##},{pivotY:0.##}");
        return Crop(full, x, y, w, h);
    }

    private static void SpriteRect(AssetTypeValueField rect, int texW, int texH, out int x, out int y, out int w, out int h)
    {
        x = (int)rect["x"].AsFloat;
        w = Math.Max(1, (int)rect["width"].AsFloat);
        h = Math.Max(1, (int)rect["height"].AsFloat);
        int unityY = (int)rect["y"].AsFloat;
        y = texH - unityY - h;
    }

    private static PixelBuffer Crop(PixelBuffer source, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        w = Math.Min(w, source.Width - x);
        h = Math.Min(h, source.Height - y);
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

    private static byte[]? ReadAudio(AssetTypeValueField field)
    {
        AssetTypeValueField data = field["m_AudioData"];
        if (!data.IsDummy && data.AsByteArray is { Length: > 0 } bytes)
        {
            return bytes;
        }

        AssetTypeValueField resource = field["m_Resource"];
        if (!resource.IsDummy)
        {
            AssetTypeValueField source = resource["m_Source"];
            if (!source.IsDummy)
            {
                // Streamed clip; listing still works, playback needs the .resource blob.
            }
        }

        return null;
    }

    private static string ReadText(AssetTypeValueField field)
    {
        AssetTypeValueField script = field["m_Script"];
        if (script.IsDummy)
        {
            return "";
        }

        if (script.AsByteArray is { Length: > 0 } bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return script.AsString ?? "";
    }

    private static string DumpFields(AssetTypeValueField field, int maxDepth, int maxLines)
    {
        var sb = new StringBuilder();
        int lines = 0;
        Walk(field, 0);
        return sb.ToString();

        void Walk(AssetTypeValueField node, int depth)
        {
            if (lines >= maxLines || depth > maxDepth)
            {
                return;
            }

            string value = ValueSummary(node);
            sb.Append(' ', depth * 2);
            sb.Append(node.TypeName);
            sb.Append(' ');
            sb.Append(node.FieldName);
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append(" = ");
                sb.Append(value);
            }

            sb.AppendLine();
            lines++;
            if (node.Children is null)
            {
                return;
            }

            foreach (AssetTypeValueField child in node.Children)
            {
                Walk(child, depth + 1);
                if (lines >= maxLines)
                {
                    sb.AppendLine("…");
                    return;
                }
            }
        }
    }

    private static string ValueSummary(AssetTypeValueField node)
    {
        try
        {
            if (node.Children is { Count: > 0 })
            {
                return "";
            }

            string type = node.TypeName ?? "";
            if (type.Contains("string", StringComparison.OrdinalIgnoreCase))
            {
                string s = node.AsString ?? "";
                return s.Length > 80 ? s[..80] + "…" : s;
            }

            if (type is "int" or "SInt32" or "UInt32" or "SInt64" or "UInt64")
            {
                return node.AsLong.ToString();
            }

            if (type is "float" or "double")
            {
                return node.AsFloat.ToString("0.###");
            }

            if (type is "bool")
            {
                return node.AsBool ? "true" : "false";
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }

    private static string ReadName(AssetTypeValueField field)
    {
        AssetTypeValueField name = field["m_Name"];
        return name.IsDummy ? "" : name.AsString ?? "";
    }

    private static string TypeName(AssetFileInfo info)
    {
        int typeId = info.TypeId;
        if (Enum.IsDefined(typeof(AssetClassID), typeId))
        {
            return ((AssetClassID)typeId).ToString();
        }

        return "Type" + typeId;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static bool IsTextureOrSprite(AssetFileInfo info)
    {
        string typeName = TypeName(info);
        return info.TypeId == (int)AssetClassID.Texture2D
            || info.TypeId == (int)AssetClassID.Sprite
            || typeName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)
            || typeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record UnityObjectRef(AssetsFileInstance File, AssetFileInfo Info);

    private sealed record SpriteSeqFrame(string EntryName, int Frame, float PivotX, float PivotY);
}
