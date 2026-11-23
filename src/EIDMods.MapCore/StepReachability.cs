using System;
using System.Collections.Generic;

namespace EIDMods.MapCore;

/// <summary>
/// "Step up" means a raised surface you can walk onto: stairs, ramps, rising ground. The column
/// classifier can't tell those from a raised top you can't reach. A railing's 1 m top rail, a
/// table or a crate all have a flat walkable top within reach of the step-up ray. This pass keeps
/// StepUp only where it connects to a Floor cell through neighbour steps of at most maxClimb
/// (8-neighbourhood); every other StepUp is a barrier and becomes a Wall.
/// </summary>
public static class StepReachability
{
    private const float Epsilon = 1e-4f;

    /// <param name="kinds">Row-major grid (index = z * width + x); modified in place.</param>
    /// <param name="dy">Floor/step height per cell (relative to the reference height).</param>
    public static void Resolve(CellKind[] kinds, float[] dy, int width, int height, float maxClimb)
    {
        if (kinds.Length != width * height || dy.Length != width * height)
            throw new ArgumentException("kinds/dy must both be width * height long");

        var reached = new bool[kinds.Length];
        var queue = new Queue<int>();
        for (int i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] != CellKind.Floor) continue;
            reached[i] = true;
            queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, z = i / width;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;
                int n = nz * width + nx;
                if (reached[n] || kinds[n] != CellKind.StepUp) continue;
                if (Math.Abs(dy[n] - dy[i]) > maxClimb + Epsilon) continue;
                reached[n] = true;
                queue.Enqueue(n);
            }
        }

        // A raised top you can't walk up to is a barrier: drawn as a wall.
        for (int i = 0; i < kinds.Length; i++)
            if (kinds[i] == CellKind.StepUp && !reached[i]) kinds[i] = CellKind.Wall;
    }
}
