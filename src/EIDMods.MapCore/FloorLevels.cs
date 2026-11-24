using System;
using System.Collections.Generic;

namespace EIDMods.MapCore;

/// <summary>
/// The storeys of a baked level, for the big map's elevation control: heights where many cells have a
/// walkable surface you can stand on (at least <see cref="Headroom"/> free above it). Standable tops are counted
/// in 0.5 m bins; the busiest bins become floors, busiest first, skipping any within
/// <see cref="MinSeparation"/> of a floor already taken and any holding less than
/// <see cref="MinShare"/> of all standable cells (ramps, stairs, rooftops, clutter).
/// Pure C# (no Unity), so it can run on a worker thread.
/// </summary>
public static class FloorLevels
{
    public const float BinSize = 0.5f, Headroom = 1.8f, MinSeparation = 2.5f, MinShare = 0.02f;

    /// <returns>Floor heights in metres, lowest first.</returns>
    public static List<float> Find(Heightfield hf) => Find(hf, 0f, 0f, float.PositiveInfinity);

    /// <summary>Same, counting only columns within radius metres (XZ) of the centre: the floors around you.</summary>
    public static List<float> Find(Heightfield hf, float centerX, float centerZ, float radius)
    {
        float cs = hf.CellSize, r2 = radius * radius;
        var count = new Dictionary<int, int>();
        var sum = new Dictionary<int, double>();
        long total = 0;
        foreach (var (cx, cz) in hf.Columns())
        {
            if (!float.IsPositiveInfinity(radius))
            {
                float dx = (cx + 0.5f) * cs - centerX, dz = (cz + 0.5f) * cs - centerZ;
                if (dx * dx + dz * dz > r2) continue;
            }
            var spans = hf.GetSpans(cx, cz);
            for (int i = 0; i < spans.Length; i++)
            {
                if ((spans[i].Flags & SpanFlags.WalkableTop) == 0) continue;
                float top = spans[i].Top;
                if (i + 1 < spans.Length && spans[i + 1].Bottom - top < Headroom) continue;
                int bin = (int)MathF.Floor(top / BinSize);
                count[bin] = count.TryGetValue(bin, out var c) ? c + 1 : 1;
                sum[bin] = (sum.TryGetValue(bin, out var s) ? s : 0) + top;
                total++;
            }
        }

        var bins = new List<int>(count.Keys);
        bins.Sort((a, b) => count[b].CompareTo(count[a]));
        var floors = new List<float>();
        foreach (int bin in bins)
        {
            if (count[bin] < MinShare * total) break;
            float h = (float)(sum[bin] / count[bin]);
            bool tooClose = false;
            foreach (float f in floors) if (MathF.Abs(f - h) < MinSeparation) { tooClose = true; break; }
            if (!tooClose) floors.Add(h);
        }
        floors.Sort();
        return floors;
    }

    /// <summary>
    /// The floor above (dir &gt; 0) or below (dir &lt; 0) the given height, at least 0.5 m away; the top or
    /// bottom floor itself when there is none further. Returns the height unchanged if there are no floors.
    /// </summary>
    public static float Step(IReadOnlyList<float> floors, float height, int dir)
    {
        if (floors.Count == 0) return height;
        const float Same = 0.5f;
        if (dir > 0)
        {
            for (int i = 0; i < floors.Count; i++) if (floors[i] > height + Same) return floors[i];
            return floors[^1];
        }
        for (int i = floors.Count - 1; i >= 0; i--) if (floors[i] < height - Same) return floors[i];
        return floors[0];
    }
}
