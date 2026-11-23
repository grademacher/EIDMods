using System;
using System.Collections.Generic;
using System.Numerics;

namespace EIDMods.MapCore;

/// <summary>Box collider → 8 vertices + 12 triangles, wound so cross(b-a, c-a) points outward.</summary>
public static class BoxTriangles
{
    // Corner order: bottom 0..3, top 4..7 (x/z: min-min, max-min, max-max, min-max).
    private static readonly int[] Tris =
    {
        0, 1, 2, 0, 2, 3, // bottom (-y)
        4, 6, 5, 4, 7, 6, // top (+y)
        0, 4, 5, 0, 5, 1, // -z
        3, 2, 6, 3, 6, 7, // +z
        0, 3, 7, 0, 7, 4, // -x
        1, 5, 6, 1, 6, 2, // +x
    };

    /// <summary>
    /// Appends a box given in local space (center, size) transformed by <paramref name="localToWorld"/>.
    /// System.Numerics row-vector convention (v * M): a Unity Matrix4x4 must be transposed first.
    /// A mirroring transform (negative determinant) keeps the outward winding.
    /// </summary>
    public static void Append(List<Vector3> verts, List<int> indices, Matrix4x4 localToWorld, Vector3 center, Vector3 size)
    {
        var h = size * 0.5f;
        var min = center - h;
        var max = center + h;
        int o = verts.Count;
        verts.Add(Vector3.Transform(new Vector3(min.X, min.Y, min.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(max.X, min.Y, min.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(max.X, min.Y, max.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(min.X, min.Y, max.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(min.X, max.Y, min.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(max.X, max.Y, min.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(max.X, max.Y, max.Z), localToWorld));
        verts.Add(Vector3.Transform(new Vector3(min.X, max.Y, max.Z), localToWorld));

        bool mirrored = localToWorld.GetDeterminant() < 0f;
        for (int t = 0; t < Tris.Length; t += 3)
        {
            indices.Add(o + Tris[t]);
            indices.Add(o + Tris[mirrored ? t + 2 : t + 1]);
            indices.Add(o + Tris[mirrored ? t + 1 : t + 2]);
        }
    }
}

/// <summary>Ground height lookup (terrain), sampled at classification time instead of rasterized.</summary>
public interface IHeightSampler
{
    bool TrySample(float x, float z, out float height);
}

/// <summary>
/// Bilinear sampler over a Unity terrain heightmap: <c>heights01[zIndex, xIndex]</c> in 0..1,
/// covering <c>[origin, origin + size]</c> in x/z; height = origin.Y + h01 * size.Y.
/// </summary>
public sealed class HeightmapSampler : IHeightSampler
{
    private readonly Vector3 _origin;
    private readonly Vector3 _size;
    private readonly int _res;
    private readonly float[,] _h;

    public HeightmapSampler(Vector3 origin, Vector3 size, int resolution, float[,] heights01)
    {
        if (resolution < 2) throw new ArgumentOutOfRangeException(nameof(resolution));
        _origin = origin;
        _size = size;
        _res = resolution;
        _h = heights01;
    }

    public bool TrySample(float x, float z, out float height)
    {
        height = 0f;
        float u = (x - _origin.X) / _size.X, v = (z - _origin.Z) / _size.Z;
        if (!(u >= 0f && u <= 1f && v >= 0f && v <= 1f)) return false; // also rejects NaN

        float fx = u * (_res - 1), fz = v * (_res - 1);
        int x0 = Math.Min((int)fx, _res - 2), z0 = Math.Min((int)fz, _res - 2);
        float tx = fx - x0, tz = fz - z0;

        float a = _h[z0, x0] + (_h[z0, x0 + 1] - _h[z0, x0]) * tx;
        float b = _h[z0 + 1, x0] + (_h[z0 + 1, x0 + 1] - _h[z0 + 1, x0]) * tx;
        height = _origin.Y + (a + (b - a) * tz) * _size.Y;
        return true;
    }
}
