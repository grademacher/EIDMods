using System;
using System.Collections.Generic;
using System.Diagnostics;
using EIDMods.Features.Minimap;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// One-shot probe answering "how should a whole-level map be baked?":
///  1. Does the game ship map art (AG.Levels.MapData.map / Preview / Backdrop)?
///  2. Are collider meshes CPU-readable (geometry slicing possible)?
///  3. Generator modules + level extent.
///  4. Cost of the physics classifier per cell (full physics bake estimate).
/// Read-only.
/// </summary>
internal static class BakeProbe
{
    public static void Run(Transform player)
    {
        var log = Mod.Log;
        log.LogInfo($"[Bake] ===== bake probe, scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', player at {player.position} =====");
        Try("mapdata", MapDataInfo);
        Try("modules", Modules);
        Try("colliders", () => Colliders(out _));
        Try("terrain", TerrainInfo);
        Try("benchmark", () => Benchmark(player.position));
        log.LogInfo("[Bake] ===== end bake probe =====");
    }

    private static void MapDataInfo()
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AG.Levels.MapData>());
        Mod.Log.LogInfo($"[Bake] MapData assets loaded: {all.Length}");
        for (int i = 0; i < all.Length && i < 12; i++)
        {
            var md = all[i].Cast<AG.Levels.MapData>();
            Mod.Log.LogInfo($"[Bake]   '{md.name}' scenePath='{md.ScenePath}' subScenes={Describe(md.SubScenes)}");
            Mod.Log.LogInfo($"[Bake]     map={Describe(md.map)}  preview={Describe(md.Preview)}  backdrop={Describe(md.Backdrop)}");
        }
    }

    private static void Modules()
    {
        var mods = Object.FindObjectsByType(Il2CppType.Of<AG.Generation.Module>(), FindObjectsSortMode.None);
        Mod.Log.LogInfo($"[Bake] generation modules in scene: {mods.Length}");
        var byName = new Dictionary<string, int>();
        for (int i = 0; i < mods.Length; i++)
        {
            var m = mods[i].Cast<AG.Generation.Module>();
            var n = m.name.Replace("(Clone)", "").Trim();
            byName[n] = byName.TryGetValue(n, out var c) ? c + 1 : 1;
            if (i < 8) Mod.Log.LogInfo($"[Bake]   module '{m.name}' type={m.Type} pos={m.transform.position} rot={m.transform.eulerAngles.y:F0} bounds={m.Bounds}");
        }
        if (mods.Length > 0) Mod.Log.LogInfo($"[Bake]   distinct module prefabs: {byName.Count}");
    }

    private static void Colliders(out Bounds levelBounds)
    {
        var all = Object.FindObjectsByType(Il2CppType.Of<Collider>(), FindObjectsSortMode.None);
        int env = 0, mesh = 0, convex = 0, readable = 0, noMesh = 0, box = 0, other = 0, terrain = 0;
        long readableTris = 0, unreadableVerts = 0;
        string firstReadable = null, firstUnreadable = null, readTest = null;
        levelBounds = default;
        bool haveBounds = false;
        var perLayer = new SortedDictionary<int, int>();

        for (int i = 0; i < all.Length; i++)
        {
            var col = all[i].Cast<Collider>();
            int layer = col.gameObject.layer;
            if (((1 << layer) & MapGrid.FloorMask) == 0 || col.isTrigger || col.attachedRigidbody != null) continue;
            env++;
            perLayer[layer] = perLayer.TryGetValue(layer, out var n) ? n + 1 : 1;

            var b = col.bounds;
            if (!haveBounds) { levelBounds = b; haveBounds = true; } else levelBounds.Encapsulate(b);

            var mc = col.TryCast<MeshCollider>();
            if (mc != null)
            {
                mesh++;
                if (mc.convex) convex++;
                var m = mc.sharedMesh;
                if (m == null) { noMesh++; continue; }
                if (m.isReadable)
                {
                    readable++;
                    readableTris += (long)m.GetIndexCount(0) / 3;
                    firstReadable ??= $"'{m.name}' on '{col.name}' ({m.vertexCount} verts)";
                    if (readTest == null)
                    {
                        // Actually read it, to be sure "readable" means usable.
                        var v = m.vertices;
                        var t = m.triangles;
                        readTest = $"read '{m.name}': {v.Length} vertices, {t.Length / 3} triangles";
                    }
                }
                else
                {
                    unreadableVerts += m.vertexCount;
                    firstUnreadable ??= $"'{m.name}' on '{col.name}' ({m.vertexCount} verts)";
                }
            }
            else if (col.TryCast<TerrainCollider>() != null) terrain++;
            else if (col.TryCast<BoxCollider>() != null) box++;
            else other++;
        }

        var log = Mod.Log;
        log.LogInfo($"[Bake] static level colliders: {env} (of {all.Length} total)  mesh={mesh} (convex {convex}, no mesh {noMesh})  box={box}  terrain={terrain}  other={other}");
        log.LogInfo($"[Bake]   per layer: {string.Join(", ", Fmt(perLayer))}");
        log.LogInfo($"[Bake]   mesh colliders readable: {readable}/{mesh - noMesh}  ({readableTris} tris readable, {unreadableVerts} verts unreadable)");
        if (firstReadable != null) log.LogInfo($"[Bake]   e.g. readable: {firstReadable}");
        if (firstUnreadable != null) log.LogInfo($"[Bake]   e.g. NOT readable: {firstUnreadable}");
        if (readTest != null) log.LogInfo($"[Bake]   {readTest}");
        if (haveBounds)
        {
            var s = levelBounds.size;
            long cells = (long)(s.x / 0.5f) * (long)(s.z / 0.5f);
            log.LogInfo($"[Bake] level bounds: center {levelBounds.center} size {s}  -> {cells:N0} cells at 0.5m");
        }
    }

    private static IEnumerable<string> Fmt(SortedDictionary<int, int> d)
    {
        foreach (var kv in d) yield return $"{kv.Key}:{LayerMask.LayerToName(kv.Key)}={kv.Value}";
    }

    private static void TerrainInfo()
    {
        var ts = Terrain.activeTerrains;
        Mod.Log.LogInfo($"[Bake] terrains: {ts.Length}");
        for (int i = 0; i < ts.Length; i++)
        {
            var td = ts[i].terrainData;
            var h = td.GetHeights(0, 0, 2, 2);
            Mod.Log.LogInfo($"[Bake]   '{ts[i].name}' pos={ts[i].transform.position} size={td.size} heightmap={td.heightmapResolution} readHeights={(h != null ? "ok" : "null")}");
        }
    }

    private static void Benchmark(Vector3 feet)
    {
        // Time the real classifier on a block of cells around the player.
        var grid = new MapGrid(0.5f);
        const int n = 60; // 60x60 = 3600 cells, 30m x 30m
        var counts = new int[6];
        var sw = Stopwatch.StartNew();
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            var k = grid.ClassifyKind(feet.x + (x - n / 2) * 0.5f, feet.z + (z - n / 2) * 0.5f, feet.y, out _);
            counts[(int)k]++;
        }
        sw.Stop();
        double usPerCell = sw.Elapsed.TotalMilliseconds * 1000.0 / (n * n);
        Mod.Log.LogInfo($"[Bake] classifier: {n * n} cells in {sw.Elapsed.TotalMilliseconds:F1} ms = {usPerCell:F2} us/cell " +
                        $"(floor {counts[1]}, stepUp {counts[2]}, obstacle {counts[3]}, wall {counts[4]}, void {counts[5]})");
        Mod.Log.LogInfo($"[Bake]   -> 1,000,000 cells would take ~{usPerCell:F1} s of main-thread time (one floor layer)");
    }

    private static string Describe(object o)
    {
        if (o == null) return "null";
        if (o is Il2CppObjectBase ib)
        {
            if (ib.Pointer == IntPtr.Zero) return "null";
            var tex = ib.TryCast<Texture2D>();
            if (tex != null) return $"Texture2D '{tex.name}' {tex.width}x{tex.height} readable={tex.isReadable}";
            var spr = ib.TryCast<Sprite>();
            if (spr != null) return $"Sprite '{spr.name}' rect={spr.rect} tex='{(spr.texture != null ? spr.texture.name : "null")}'";
            var uo = ib.TryCast<Object>();
            if (uo != null) return $"{ib.GetType().Name} '{uo.name}'";
            return $"{ib.GetType().Name}: {ib}";
        }
        return $"{o.GetType().Name}: {o}";
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Mod.Log.LogWarning($"[Bake] {what} failed: {e.GetType().Name}: {e.Message}"); }
    }
}
