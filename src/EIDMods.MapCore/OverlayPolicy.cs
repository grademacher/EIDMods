namespace EIDMods.MapCore;

/// <summary>
/// When the live physics overlay may override the baked map.
///
/// The overlay exists to catch geometry that changed since the bake (doors, destruction, moved
/// props). Those always flip whether a cell blocks a standing player. Physics also has artifacts of
/// its own: a step-up ray that starts inside a solid wall falls through and reports "StepUp" on a
/// real wall. That made catacomb walls near the player vanish in sections as the reference height
/// moved on slopes. So the overlay only wins when blocked-vs-open differs, never on which kind of
/// blocked (or which floor shade) it is, and a blocked cell only opens when physics finds a floor there.
/// </summary>
public static class OverlayPolicy
{
    /// <param name="liveConservativelyOpen">
    /// The live cell is clear even with a full-cell (non-inset) check. The live classification uses
    /// the same inset box as the bake, which can miss a thin wall the bake has; a cell only counts
    /// as opened (door opened, wall destroyed) if nothing is there at all.
    /// </param>
    public static bool ShouldOverride(CellKind baked, CellKind live, bool liveConservativelyOpen)
    {
        if (baked == CellKind.Unknown || live == CellKind.Unknown) return false;
        bool bakedBlocked = IsBlocked(baked), liveBlocked = IsBlocked(live);
        if (bakedBlocked == liveBlocked) return false;
        if (liveBlocked) return true;
        // Opening a blocked cell needs a floor: physics sees terrain as a thin surface, so earth behind
        // a sewer wall reads as clear with no floor (Void) where the bake knows it's rock (2026-11-23).
        return live != CellKind.Void && liveConservativelyOpen;
    }

    public static bool IsBlocked(CellKind k) => k is CellKind.Wall or CellKind.Obstacle or CellKind.StepUp;
}
