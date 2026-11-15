using DarkAges.Formats.Catalog;
using DarkAges.Formats.Data;

namespace DarkAges.Formats.Unity;

public static class LodRAssets
{
    public static ArchiveRole Classify(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith("item"))
        {
            return ArchiveRole.Items;
        }

        if (name.StartsWith("monster"))
        {
            return ArchiveRole.Mobs;
        }

        if (name.StartsWith("map") || name is "light")
        {
            return ArchiveRole.Maps;
        }

        if (name.StartsWith("effect") || name.Contains("effect"))
        {
            return ArchiveRole.Effects;
        }

        if (name.StartsWith("bgm") || name.StartsWith("sfx") || name.StartsWith("video"))
        {
            return ArchiveRole.Cinematics;
        }

        if (IsBodyPrefix(name)
            || name.Contains("cash"))
        {
            return ArchiveRole.PlayerSprites;
        }

        if (name is "ui" or "atlas" or "paper" or "tables"
            || name.StartsWith("skill")
            || name.StartsWith("spell")
            || name.StartsWith("embl")
            || name.StartsWith("emot")
            || name.StartsWith("rank")
            || name.StartsWith("rest"))
        {
            return ArchiveRole.Ui;
        }

        return ArchiveRole.Misc;
    }

    public static bool IsListedType(string typeName) =>
        typeName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("AudioClip", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("TextAsset", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Tilemap", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Cubemap", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("RenderTexture", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("VideoClip", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Font", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Material", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
        || typeName.Equals("Shader", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageEntry(string name)
    {
        string ext = Path.GetExtension(name);
        return ext.Equals(".texture2d", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sprite", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cubemap", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rendertexture", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAudioEntry(string name)
    {
        string ext = Path.GetExtension(name);
        return ext.Equals(".audioclip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bundles whose sprites are inventory/UI icons (not walk/attack / emote animations).
    /// Monster and emo/emot packs are animated sequences, not icon sheets.
    /// </summary>
    public static bool IsIconBundle(string path)
    {
        string name = Path.GetFileName(path);
        if (IsAnimatedSequenceBundle(name))
        {
            return false;
        }

        if (name.Contains("cash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Classify(path) is ArchiveRole.Items or ArchiveRole.Ui;
    }

    /// <summary>
    /// Inventory / skill / spell / UI icons use _N as separate assets, not animation frames.
    /// Emotes (emo*/emot*) and monsters are sequences — see <see cref="IsAnimatedSequenceName"/>.
    /// </summary>
    public static bool IsIconSpriteName(string name)
    {
        string stem = SequenceStem(name);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        if (IsAnimatedSequenceName(stem))
        {
            return false;
        }

        return stem.StartsWith("item", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("skill", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("spell", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("embl", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("rank", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("rest", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("icon", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("ui", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("paper", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("cash", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("atlas", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsIconAsset(string bundlePath, string spriteName) =>
        !IsAnimatedSequenceName(spriteName)
        && !IsAnimatedSequenceBundle(Path.GetFileName(bundlePath))
        && (IsIconBundle(bundlePath) || IsIconSpriteName(spriteName));

    /// <summary>Monster / emote / effect packs whose _N suffixes are animation frames.</summary>
    public static bool IsAnimatedSequenceBundle(string fileName) =>
        fileName.StartsWith("monster", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("emot", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("emo", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("effect", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("effect", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sprite group names that should collapse to one list row with Playable frames.</summary>
    public static bool IsAnimatedSequenceName(string name)
    {
        string stem = SequenceStem(name);
        return stem.StartsWith("monster", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("emot", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("emo", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("effect", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("efct", StringComparison.OrdinalIgnoreCase);
    }

    private static string SequenceStem(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "";
        }

        int us = stem.LastIndexOf('_');
        if (us > 0 && int.TryParse(stem[(us + 1)..], out _))
        {
            stem = stem[..us];
        }

        return stem;
    }

    public static IEnumerable<ArchiveEntry> PreferSprites(IEnumerable<ArchiveEntry> entries)
    {
        var list = entries as IList<ArchiveEntry> ?? entries.ToList();
        if (!list.Any(e => e.Extension.Equals(".sprite", StringComparison.OrdinalIgnoreCase)))
        {
            return list;
        }

        return list.Where(e =>
            e.Name.Equals(UnityBundleArchive.MapPreviewName, StringComparison.OrdinalIgnoreCase)
            || !e.Extension.Equals(".texture2d", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>LoD R / khan-style body &amp; gear sprite names (mh001, wu123, …).</summary>
    public static bool IsBodyPrefix(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length < 2)
        {
            return false;
        }

        // Strip trailing _frame for Unity sequences.
        int us = stem.LastIndexOf('_');
        if (us > 1 && int.TryParse(stem[(us + 1)..], out _))
        {
            stem = stem[..us];
        }

        char a = stem[0];
        char b = stem[1];
        bool gender = a is 'm' or 'w';
        bool part = b is 'h' or 'b' or 'a' or 'c' or 'u' or 'g' or 'l' or 'p' or 'e' or 'o' or 'm' or 'n' or 's' or 'f' or 'w';
        return gender && part;
    }
}
