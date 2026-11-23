using System;
using System.Text;
using EIDMods.Features.Minimap;
using EIDMods.MapCore;
using UnityEngine;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// Debug: classifies random cells around the player both live (physics, MapGrid) and from the
/// bake (ColumnClassifier), and logs how often they agree plus a few disagreements with the spans
/// involved. Read-only.
/// </summary>
internal static class BakeParity
{
    public static void Run(Vector3 feet, float refY, Heightfield hf, IHeightSampler terrain, float cellSize, int samples = 600, float radius = 40f)
    {
        var physics = new MapGrid(cellSize);
        var baked = new ColumnClassifier(hf, terrain);
        var rng = new System.Random(12345);
        var confusion = new int[6, 6];
        int agree = 0, examples = 0;
        var log = Mod.Log;

        for (int n = 0; n < samples; n++)
        {
            double ang = rng.NextDouble() * Math.PI * 2, dist = Math.Sqrt(rng.NextDouble()) * radius;
            int cx = (int)MathF.Floor((feet.x + (float)(Math.Cos(ang) * dist)) / cellSize);
            int cz = (int)MathF.Floor((feet.z + (float)(Math.Sin(ang) * dist)) / cellSize);
            float x = (cx + 0.5f) * cellSize, z = (cz + 0.5f) * cellSize;

            var live = physics.ClassifyKind(x, z, refY, out float dyLive);
            var bake = baked.Classify(cx, cz, refY, out float dyBake);
            confusion[(int)live, (int)bake]++;
            if (live == bake) { agree++; continue; }

            if (examples++ < 12)
            {
                var sb = new StringBuilder();
                foreach (var s in hf.GetSpans(cx, cz)) sb.Append($"[{s.Bottom - refY:F2}..{s.Top - refY:F2} {s.Flags}] ");
                string t = terrain != null && terrain.TrySample(x, z, out float th) ? $"{th - refY:F2}" : "none";
                log.LogInfo($"[Parity]   ({cx},{cz}) live={live}({dyLive:F2}) baked={bake}({dyBake:F2}) terrain={t} spans(rel)={sb}");
            }
        }

        log.LogInfo($"[Parity] {agree}/{samples} agree ({100.0 * agree / samples:F0}%) within {radius} m, refY={refY:F2}. rows=live, cols=baked (Unknown,Floor,StepUp,Obstacle,Wall,Void):");
        for (int r = 1; r < 6; r++)
            log.LogInfo(string.Format("[Parity]   {0,-8} {1,5} {2,5} {3,5} {4,5} {5,5}",
                (CellKind)r, confusion[r, 1], confusion[r, 2], confusion[r, 3], confusion[r, 4], confusion[r, 5]));
    }
}
