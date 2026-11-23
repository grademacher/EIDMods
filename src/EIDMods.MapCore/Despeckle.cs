using System;

namespace EIDMods.MapCore;

/// <summary>
/// Majority filter for map cells. A cell that agrees with at most one of its (at least 4 known)
/// neighbours is a speck (rubble flipping between step-up and floor, a lone post, a pinhole in a
/// wall) and takes the most common neighbouring kind. A stronger rule (same ≤ 2) would erase
/// one-cell-wide walls, which have exactly two same-kind neighbours.
/// </summary>
public static class Despeckle
{
    private const int KindCount = 6; // CellKind values

    /// <param name="neighbours8">The 8 surrounding cells (any order); Unknown ones don't count.</param>
    /// <returns>The kind to draw: <paramref name="self"/>, or the winning neighbour kind for a speck (ties → lowest kind).</returns>
    public static CellKind Filter(CellKind self, ReadOnlySpan<CellKind> neighbours8)
    {
        Span<int> counts = stackalloc int[KindCount];
        int known = 0, same = 0;
        foreach (var k in neighbours8)
        {
            if (k == CellKind.Unknown) continue;
            known++;
            if (k == self) same++;
            counts[(int)k]++;
        }

        if (known < 4 || same > 1) return self;

        int best = 1;
        for (int k = 2; k < KindCount; k++)
            if (counts[k] > counts[best]) best = k;
        return (CellKind)best;
    }
}
