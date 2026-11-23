using EIDMods.MapCore;
using UnityEngine;

namespace EIDMods.Features.MapBake;

/// <summary>
/// <see cref="IHeightSampler"/> backed by the live Unity terrains (Terrain.SampleHeight).
/// MAIN THREAD ONLY; classification of display chunks happens on the main thread.
/// </summary>
internal sealed class TerrainSampler : IHeightSampler
{
    // Terrains don't move: position/size are cached so a sample is one interop call, not three.
    private readonly (Terrain terrain, Vector3 pos, Vector3 size)[] _terrains;

    private TerrainSampler((Terrain, Vector3, Vector3)[] terrains) => _terrains = terrains;

    /// <summary>A sampler over the currently active terrains, or null if there are none.</summary>
    public static TerrainSampler FromActive()
    {
        var active = Terrain.activeTerrains;
        if (active == null || active.Length == 0) return null;
        var list = new (Terrain, Vector3, Vector3)[active.Length];
        for (int i = 0; i < active.Length; i++)
            list[i] = (active[i], active[i].transform.position, active[i].terrainData.size);
        return new TerrainSampler(list);
    }

    public bool TrySample(float x, float z, out float height)
    {
        foreach (var (t, p, size) in _terrains)
        {
            if (x < p.x || z < p.z || x > p.x + size.x || z > p.z + size.z) continue;
            if (t == null) continue;
            height = p.y + t.SampleHeight(new Vector3(x, 0f, z));
            return true;
        }
        height = 0f;
        return false;
    }
}
