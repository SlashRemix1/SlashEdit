namespace DarkAges.Formats.Catalog;

public enum ArchiveRole
{
    Unknown,
    Mobs,
    Walls,
    Floors,
    Ui,
    Items,
    Effects,
    PlayerSprites,
        Maps,
        Metadata,
        Cinematics,
        Misc
    }

public static class ArchiveRoleNames
{
    public static string Label(ArchiveRole role) => role switch
    {
        ArchiveRole.Mobs => "Mobs",
        ArchiveRole.Walls => "Walls",
        ArchiveRole.Floors => "Floors",
        ArchiveRole.Ui => "UI",
        ArchiveRole.Items => "Items",
        ArchiveRole.Effects => "Effects",
        ArchiveRole.PlayerSprites => "Players / gear",
        ArchiveRole.Maps => "Maps",
        ArchiveRole.Metadata => "Metadata",
        ArchiveRole.Cinematics => "Cinematics",
        ArchiveRole.Misc => "Other",
        _ => "Unknown"
    };
}
