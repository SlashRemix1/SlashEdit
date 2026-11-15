using DarkAges.Formats.Catalog;

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

    private static bool IsBodyPrefix(string name)
    {
        if (name.Length < 2)
        {
            return false;
        }

        char a = name[0];
        char b = name[1];
        bool gender = a is 'm' or 'w';
        bool part = b is 'h' or 'b' or 'a' or 'c' or 'u' or 'g' or 'l' or 'p' or 'e' or 'o' or 'm' or 'n' or 's' or 'f' or 'w';
        return gender && part;
    }
}
