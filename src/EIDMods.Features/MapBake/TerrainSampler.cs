using EIDMods.MapCore;
using UnityEngine;

namespace EIDMods.Features.MapBake;

/// <summary>
/// <see cref="IHeightSampler"/> backed by the live Unity terrains (Terrain.SampleHeight).
/// MAIN THREAD ONLY; classification of display chunks happens on the main thread.
/// </summary>
internal sealed class TerrainSampler : IHeightSampler
{
    private readonly Terrain[] _terrains;

    private TerrainSampler(Terrain[] terrains) => _terrains = terrains;

    /// <summary>A sampler over the currently active terrains, or null if there are none.</summary>
    public static TerrainSampler FromActive()
    {
        var active = Terrain.activeTerrains;
        if (active == null || active.Length == 0) return null;
        var list = new Terrain[active.Length];
        for (int i = 0; i < active.Length; i++) list[i] = active[i];
        return new TerrainSampler(list);
    }

    public bool TrySample(float x, float z, out float height)
    {
        foreach (var t in _terrains)
        {
            if (t == null) continue;
            var p = t.transform.position;
            var size = t.terrainData.size;
            if (x < p.x || z < p.z || x > p.x + size.x || z > p.z + size.z) continue;
            height = p.y + t.SampleHeight(new Vector3(x, 0f, z));
            return true;
        }
        height = 0f;
        return false;
    }
}
