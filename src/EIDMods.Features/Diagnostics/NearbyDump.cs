using System.Text;
using EIDMods.Features.Minimap;
using EIDMods.MapCore;
using UnityEngine;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// Debug (F5): every collider within a few metres of the player (layer, type, trigger, rigidbody,
/// bounds), plus side-by-side ASCII maps of baked vs live classification around the player.
/// For "why is X missing from the map" questions. Read-only.
/// </summary>
internal static class NearbyDump
{
    public static void Run(Vector3 feet, float refY, Heightfield hf, IHeightSampler terrain, float cellSize, float radius = 4f)
    {
        var log = Mod.Log;
        log.LogInfo($"[Nearby] ===== player {feet}, refY {refY:F2} =====");

        var cols = Physics.OverlapSphere(feet + Vector3.up, radius, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            var go = c.gameObject;
            var b = c.bounds;
            string mesh = "";
            var mc = c.TryCast<MeshCollider>();
            if (mc != null) mesh = $" mesh='{(mc.sharedMesh != null ? mc.sharedMesh.name : "null")}' convex={mc.convex} readable={(mc.sharedMesh != null && mc.sharedMesh.isReadable)}";
            bool inMask = ((1 << go.layer) & MapGrid.FloorMask) != 0;
            log.LogInfo($"[Nearby]   '{go.name}' layer={go.layer}:{LayerMask.LayerToName(go.layer)}{(inMask ? "" : " (IGNORED LAYER)")} " +
                        $"{c.GetIl2CppType().Name} trigger={c.isTrigger} rb={(c.attachedRigidbody != null)} enabled={c.enabled} " +
                        $"y {b.min.y - refY:F2}..{b.max.y - refY:F2} size=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}){mesh}");
        }

        // ASCII maps, north up, player in the middle. # wall, o obstacle, ^ step-up, . floor, - lower, v drop, _ void
        if (hf == null) { log.LogInfo("[Nearby] (no bake yet)"); return; }
        var baked = new ColumnClassifier(hf, terrain);
        var live = new MapGrid(cellSize);
        int r = Mathf.CeilToInt(radius / cellSize);
        int pcx = (int)System.MathF.Floor(feet.x / cellSize), pcz = (int)System.MathF.Floor(feet.z / cellSize);
        // Baked kinds for the whole window, with the same step-up reachability pass as the map view.
        int w = 2 * r + 1;
        var kinds = new CellKind[w * w];
        var dys = new float[w * w];
        for (int z = 0; z < w; z++)
        for (int x = 0; x < w; x++)
            kinds[z * w + x] = baked.Classify(pcx - r + x, pcz - r + z, refY, out dys[z * w + x]);
        StepReachability.Resolve(kinds, dys, w, w, 0.6f);

        log.LogInfo("[Nearby] baked (as drawn) | live   (north up, @ = player)");
        for (int dz = r; dz >= -r; dz--)
        {
            var sb = new StringBuilder();
            var sl = new StringBuilder();
            for (int dx = -r; dx <= r; dx++)
            {
                int cx = pcx + dx, cz = pcz + dz;
                if (dx == 0 && dz == 0) { sb.Append('@'); sl.Append('@'); continue; }
                int wi = (dz + r) * w + dx + r;
                sb.Append(Char(kinds[wi], dys[wi]));
                var k = live.ClassifyKind((cx + 0.5f) * cellSize, (cz + 0.5f) * cellSize, refY, out float dyL);
                sl.Append(Char(k, dyL));
            }
            log.LogInfo($"[Nearby] {sb} | {sl}");
        }
    }

    private static char Char(CellKind k, float dy) => k switch
    {
        CellKind.Wall => '#',
        CellKind.Obstacle => 'o',
        CellKind.StepUp => '^',
        CellKind.Void => '_',
        _ => dy < -1.5f ? 'v' : dy < -0.5f ? '-' : '.',
    };
}
