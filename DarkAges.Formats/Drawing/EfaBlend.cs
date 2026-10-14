namespace DarkAges.Formats.Drawing;

/// <summary>
/// First int32 of an EFA. Additive/self-alpha blit as Plus; separate/per-channel as SrcOver.
/// Palette ids ≥ 1000 in effpal.tbl are a separate luminance-to-alpha path on palettized EPF effects.
/// </summary>
public enum EfaBlend
{
    Additive = 0,
    SelfAlpha = 1,
    SeparateAlpha = 2,
    PerChannelAlpha = 3
}

public static class EfaBlendRules
{
    public static EfaBlend FromRaw(int value) =>
        value is >= 0 and <= 3 ? (EfaBlend)value : EfaBlend.Additive;

    public static bool AdditiveBlit(EfaBlend blend) =>
        blend is EfaBlend.Additive or EfaBlend.SelfAlpha;

    public static bool LuminanceAlpha(EfaBlend blend) =>
        blend is not EfaBlend.PerChannelAlpha;
}
