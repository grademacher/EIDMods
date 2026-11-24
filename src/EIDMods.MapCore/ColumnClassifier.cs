using System;

namespace EIDMods.MapCore;

/// <summary>Heights (metres, relative to the reference height) used by the cell classification.</summary>
public static class ClassifierBands
{
    /// <summary>
    /// Body box: something solid here blocks a standing player. Starts at 0.45 (not knee height 0.6)
    /// so a 1 m railing still blocks it when the reference height lags 0.5 m above the feet.
    /// </summary>
    public const float BodyLow = 0.45f, BodyHigh = 1.6f;
    /// <summary>Head box: solid here too makes it a wall rather than an obstacle.</summary>
    public const float HeadLow = 1.7f, HeadHigh = 2.3f;
    /// <summary>The step-up ray starts here and looks down for a walkable top above StepMinTop.</summary>
    public const float StepRayFrom = 2.6f, StepMinTop = 0.45f;
    /// <summary>The floor ray starts here and looks down this far.</summary>
    public const float FloorRayFrom = 0.6f, FloorRayLength = 10f;
    /// <summary>Floor within ±FlatFloor is drawn flat; below Drop it reads as a lower floor.</summary>
    public const float FlatFloor = 0.5f, Drop = -1.5f;
    /// <summary>Terrain acts as a solid span this deep below its surface.</summary>
    public const float TerrainDepth = 50f;
    /// <summary>
    /// Terrain is ignored in a column with a walkable floor between refY − 2 and refY + 1 that lies more
    /// than 2 m under the terrain surface (a tunnel under the ground).
    /// </summary>
    public const float UnderTerrainFloorBelow = 2f, UnderTerrainFloorAbove = 1f, UnderTerrainCover = 2f;
    /// <summary>
    /// Metres trimmed off each cell edge when deciding whether geometry occupies a cell (bake and
    /// live overlay alike), so door frames grazing a doorway cell don't close it.
    /// </summary>
    public const float CellInset = 0.1f;
}

/// <summary>
/// Classifies a cell from a baked <see cref="Heightfield"/> (+ optional terrain), with the same rules
/// the physics scanner (MapGrid.ClassifyKind) applies with CheckBox/Raycast:
/// 1. Body band blocked by a solid (non-FloorOnly) span?
///    - If the step-up ray's origin is inside a span, it would start inside a wall: not a step up.
///    - Else the highest top below the origin, if walkable and above StepMinTop, is a StepUp.
///    - Else Wall if the head band is blocked too, otherwise Obstacle.
/// 2. Otherwise the highest top at or below the floor ray's origin (within its length) is the Floor;
///    nothing there is Void. (A span containing the floor ray's origin is missed, like physics does.)
/// </summary>
public sealed class ColumnClassifier
{
    private readonly Heightfield _hf;
    private readonly IHeightSampler _terrain;

    public ColumnClassifier(Heightfield hf, IHeightSampler terrain)
    {
        _hf = hf ?? throw new ArgumentNullException(nameof(hf));
        _terrain = terrain;
    }

    public CellKind Classify(int cx, int cz, float refY, out float dy)
    {
        dy = 0f;
        var spans = _hf.GetSpans(cx, cz);

        bool hasTerrain = false;
        HeightSpan terrain = default;
        if (_terrain != null)
        {
            float cs = _hf.CellSize;
            if (_terrain.TrySample((cx + 0.5f) * cs, (cz + 0.5f) * cs, out float t) && !UnderTerrainFloor(spans, t, refY))
            {
                hasTerrain = true;
                terrain = new HeightSpan(t - ClassifierBands.TerrainDepth, t, SpanFlags.WalkableTop);
            }
        }

        float bodyLo = refY + ClassifierBands.BodyLow, bodyHi = refY + ClassifierBands.BodyHigh;
        bool blocked = AnySolidOverlap(spans, hasTerrain, terrain, bodyLo, bodyHi);

        if (blocked)
        {
            float rayFrom = refY + ClassifierBands.StepRayFrom;
            bool originInside = false, haveTop = false;
            HeightSpan best = default;

            for (int i = -1; i < spans.Length; i++)
            {
                HeightSpan s;
                if (i < 0) { if (!hasTerrain) continue; s = terrain; }
                else s = spans[i];

                if (s.Bottom < rayFrom && rayFrom < s.Top) { originInside = true; break; }
                if (s.Top <= rayFrom && (!haveTop || s.Top > best.Top)) { best = s; haveTop = true; }
            }

            if (!originInside && haveTop && best.Top > refY + ClassifierBands.StepMinTop
                && (best.Flags & SpanFlags.WalkableTop) != 0)
            {
                dy = best.Top - refY;
                return CellKind.StepUp;
            }

            bool tall = AnySolidOverlap(spans, hasTerrain, terrain, refY + ClassifierBands.HeadLow, refY + ClassifierBands.HeadHigh);
            return tall ? CellKind.Wall : CellKind.Obstacle;
        }

        float floorFrom = refY + ClassifierBands.FloorRayFrom;
        float floorTo = floorFrom - ClassifierBands.FloorRayLength;
        bool found = false;
        float top = 0f;
        for (int i = -1; i < spans.Length; i++)
        {
            HeightSpan s;
            if (i < 0) { if (!hasTerrain) continue; s = terrain; }
            else s = spans[i];

            if (s.Top <= floorFrom && s.Top >= floorTo && (!found || s.Top > top))
            {
                top = s.Top;
                found = true;
            }
        }

        if (!found) return CellKind.Void;
        dy = top - refY;
        return CellKind.Floor;
    }

    /// <summary>
    /// A built walkable floor near your height, well below the ground surface: a tunnel or cellar (the
    /// sewers under the Moor, 2026-11-23). The terrain is a ceiling there, not rock, so it's ignored for
    /// this column. Rock between tunnels has no such floor and stays solid.
    /// </summary>
    private static bool UnderTerrainFloor(ReadOnlySpan<HeightSpan> spans, float terrainTop, float refY)
    {
        for (int i = 0; i < spans.Length; i++)
        {
            var s = spans[i];
            if ((s.Flags & SpanFlags.WalkableTop) == 0) continue;
            if (s.Top >= refY - ClassifierBands.UnderTerrainFloorBelow && s.Top <= refY + ClassifierBands.UnderTerrainFloorAbove
                && s.Top < terrainTop - ClassifierBands.UnderTerrainCover)
                return true;
        }
        return false;
    }

    private static bool AnySolidOverlap(ReadOnlySpan<HeightSpan> spans, bool hasTerrain, HeightSpan terrain, float lo, float hi)
    {
        if (hasTerrain && terrain.Bottom < hi && terrain.Top > lo) return true;
        foreach (var s in spans)
            if ((s.Flags & SpanFlags.FloorOnly) == 0 && s.Bottom < hi && s.Top > lo) return true;
        return false;
    }
}
