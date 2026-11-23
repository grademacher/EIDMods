namespace EIDMods.MapCore.Markers;

public enum MarkerKind : byte { Teammate, TeammateDowned, Enemy, Container, ContainerOpened, CorpseLoot, FloorItem }

/// <summary>RGBA, 4 bytes in the same layout as UnityEngine.Color32 (the game side reinterprets its buffer).</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>Something to show on the map, at a world position. Colour and size come from <see cref="MarkerPalette"/>.</summary>
public readonly record struct MapMarker(float X, float Y, float Z, MarkerKind Kind);

/// <summary>Where a marker lands on the map texture (row 0 = bottom).</summary>
public readonly record struct ProjectedMarker(int Px, int Py, bool OnRim, float RimAngleDeg, float Alpha);

/// <summary>
/// The agreed look (user, 2026-11-23): every marker is a dot. Teammates are green (orange when downed),
/// enemies are red, and loot/containers are smaller yellow dots.
/// </summary>
public static class MarkerPalette
{
    public static readonly Rgba Teammate = new(80, 200, 90, 255);
    public static readonly Rgba TeammateDowned = new(255, 150, 40, 255);
    public static readonly Rgba Enemy = new(220, 50, 45, 255);
    public static readonly Rgba Loot = new(240, 205, 60, 255);
    public static readonly Rgba LootOpened = new(240, 205, 60, 102); // 40 %
    public static readonly Rgba Outline = new(10, 10, 10, 255);

    public static Rgba For(MarkerKind k) => k switch
    {
        MarkerKind.Teammate => Teammate,
        MarkerKind.TeammateDowned => TeammateDowned,
        MarkerKind.Enemy => Enemy,
        MarkerKind.ContainerOpened => LootOpened,
        _ => Loot,
    };

    public static int Radius(MarkerKind k) => k switch
    {
        MarkerKind.Teammate or MarkerKind.TeammateDowned or MarkerKind.Enemy => 3,
        MarkerKind.FloorItem => 1,
        _ => 2,
    };
}
