using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// One-shot dump of what level data exists around the player, so we can decide what the
/// minimap should be built from (NavMesh, generator modules, or raw physics colliders).
/// Read-only: only queries, never changes anything.
/// </summary>
internal static class LevelProbe
{
    public static void Run(Transform player)
    {
        var log = Mod.Log;
        var p = player.position;
        log.LogInfo($"[Probe] ===== level probe, player '{player.name}' at {p} =====");

        Try("scene", () => log.LogInfo($"[Probe] active scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}"));

        Try("layers", () =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                var n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) sb.Append($"{i}={n}, ");
            }
            log.LogInfo($"[Probe] layers: {sb}");
        });

        Try("navmesh", () =>
        {
            bool onMesh = NavMesh.SamplePosition(p, out var navHit, 3f, NavMesh.AllAreas);
            log.LogInfo($"[Probe] navmesh under player: {(onMesh ? $"yes, at {navHit.position}" : "no (within 3m)")}");

            // CalculateTriangulation was stripped from the build; Triangulate is the native call behind it.
            NavMesh.Triangulate(out var v, out var idx);
            int n = v?.Length ?? 0;
            if (n == 0) { log.LogInfo("[Probe] navmesh triangulation: EMPTY"); return; }
            var b = new Bounds(v[0], Vector3.zero);
            for (int i = 1; i < n; i++) b.Encapsulate(v[i]);
            log.LogInfo($"[Probe] navmesh triangulation: {n} verts, {idx.Length / 3} tris, bounds center {b.center} size {b.size}");
        });

        Try("modules", () =>
        {
            var objs = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<AG.Generation.Module>(), FindObjectsSortMode.None);
            log.LogInfo($"[Probe] generation modules: {objs.Length}");
            for (int i = 0; i < objs.Length && i < 30; i++)
            {
                var m = objs[i].Cast<AG.Generation.Module>();
                log.LogInfo($"[Probe]   module '{m.name}' type={m.Type} pos={m.transform.position} bounds={m.Bounds} conns={m.ConnectionCount}");
            }
        });

        Try("terrain", () => log.LogInfo($"[Probe] active terrains: {Terrain.activeTerrains.Length}"));

        Try("colliders", () =>
        {
            var cols = Physics.OverlapSphere(p, 20f, ~0, QueryTriggerInteraction.Collide);
            var byLayer = new SortedDictionary<string, int[]>(); // [total, trigger, withRigidbody, meshCollider]
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                var key = $"{c.gameObject.layer}:{LayerMask.LayerToName(c.gameObject.layer)}";
                if (!byLayer.TryGetValue(key, out var s)) byLayer[key] = s = new int[4];
                s[0]++;
                if (c.isTrigger) s[1]++;
                if (c.attachedRigidbody != null) s[2]++;
                if (c.TryCast<MeshCollider>() != null) s[3]++;
            }
            log.LogInfo($"[Probe] colliders within 20m: {cols.Length} (per layer: total/trigger/rigidbody/mesh)");
            foreach (var kv in byLayer)
                log.LogInfo("[Probe]   " + kv.Key.PadRight(24) + $" {kv.Value[0]}/{kv.Value[1]}/{kv.Value[2]}/{kv.Value[3]}");
        });

        Try("raycasts", () =>
        {
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var down, 5f, ~0, QueryTriggerInteraction.Ignore))
                log.LogInfo($"[Probe] floor: {Describe(down)}");
            else
                log.LogInfo("[Probe] floor: no hit within 5m");

            var origin = p + Vector3.up * 1.0f;
            for (int a = 0; a < 360; a += 45)
            {
                var dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                log.LogInfo(Physics.Raycast(origin, dir, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore)
                    ? $"[Probe] ray {a,3}deg: {Describe(hit)}"
                    : $"[Probe] ray {a,3}deg: no hit within 30m");
            }
        });

        log.LogInfo("[Probe] ===== end probe =====");
    }

    private static string Describe(RaycastHit h)
    {
        var go = h.collider.gameObject;
        return $"{h.distance:F1}m layer={go.layer}:{LayerMask.LayerToName(go.layer)} obj='{go.name}' " +
               $"collider={h.collider.GetIl2CppType().Name} normal={h.normal} static={(h.collider.attachedRigidbody == null)}";
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Mod.Log.LogWarning($"[Probe] {what} failed: {e.GetType().Name}: {e.Message}"); }
    }
}
