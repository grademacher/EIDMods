using System;

namespace EIDMods.MapCore;

/// <summary>
/// What a map cell is, relative to the player's reference height.
/// Wall: blocks body AND head height (a real wall/pillar).
/// Obstacle: blocks body height only, with no walkable top (debris, crates, railings).
/// StepUp: blocks body height but has a walkable top (stairs, ledges, rising ground).
/// </summary>
public enum CellKind : byte { Unknown = 0, Floor, StepUp, Obstacle, Wall, Void }

[Flags]
public enum SpanFlags : byte
{
    None = 0,
    /// <summary>The span's top surface is walkable (|normal.y| &gt; 0.75).</summary>
    WalkableTop = 1,
    /// <summary>Counts for floor height only, never as a wall/obstacle (the game's "Small" layer).</summary>
    FloorOnly = 2,
}

/// <summary>A solid vertical interval in one cell column, in world-space metres.</summary>
public readonly record struct HeightSpan(float Bottom, float Top, SpanFlags Flags);
