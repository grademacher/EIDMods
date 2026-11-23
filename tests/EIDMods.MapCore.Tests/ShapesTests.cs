using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class ShapesTests
{
    private const float Tol = 1e-4f;

    private static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> v) =>
        (new Vector3(v.Min(p => p.X), v.Min(p => p.Y), v.Min(p => p.Z)),
         new Vector3(v.Max(p => p.X), v.Max(p => p.Y), v.Max(p => p.Z)));

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(actual.X, expected.X - Tol, expected.X + Tol);
        Assert.InRange(actual.Y, expected.Y - Tol, expected.Y + Tol);
        Assert.InRange(actual.Z, expected.Z - Tol, expected.Z + Tol);
    }

    [Fact]
    public void Box_Identity_Has8Verts12Tris_BoundsMatch()
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        BoxTriangles.Append(v, idx, Matrix4x4.Identity, new Vector3(1, 2, 3), new Vector3(2, 4, 6));

        Assert.Equal(8, v.Count);
        Assert.Equal(36, idx.Count);
        var (min, max) = Bounds(v);
        AssertVec(new Vector3(0, 0, 0), min);
        AssertVec(new Vector3(2, 4, 6), max);
    }

    [Fact]
    public void Box_AppendsAfterExistingVertices()
    {
        var v = new List<Vector3> { Vector3.Zero, Vector3.One };
        var idx = new List<int> { 0, 1, 0 };
        BoxTriangles.Append(v, idx, Matrix4x4.Identity, Vector3.Zero, Vector3.One);

        Assert.Equal(10, v.Count);
        Assert.All(idx.Skip(3), i => Assert.InRange(i, 2, 9));
    }

    [Fact]
    public void Box_RotatedAndTranslated_BoundsMatchExpected()
    {
        var m = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 5);
        var v = new List<Vector3>();
        BoxTriangles.Append(v, new List<int>(), m, Vector3.Zero, new Vector3(2, 2, 4));

        var (min, max) = Bounds(v);
        AssertVec(new Vector3(8, -1, 4), min);
        AssertVec(new Vector3(12, 1, 6), max);
    }

    [Fact]
    public void Box_TrianglesWoundOutward()
    {
        // The rasterizer's boundary tie-break relies on cross(b-a, c-a) pointing out of the solid.
        var v = new List<Vector3>();
        var idx = new List<int>();
        var center = new Vector3(3, 1, -2);
        BoxTriangles.Append(v, idx, Matrix4x4.Identity, center, new Vector3(1, 2, 3));

        for (int t = 0; t < idx.Count; t += 3)
        {
            Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
            var n = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3f - center;
            Assert.True(Vector3.Dot(n, outward) > 0f, $"triangle {t / 3} is wound inward");
        }
    }

    [Fact]
    public void Box_MirroredTransform_StillWoundOutward()
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        var m = Matrix4x4.CreateScale(-1, 1, 1) * Matrix4x4.CreateTranslation(5, 0, 0);
        BoxTriangles.Append(v, idx, m, Vector3.Zero, new Vector3(1, 2, 3));
        var center = new Vector3(5, 0, 0);

        for (int t = 0; t < idx.Count; t += 3)
        {
            Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - center) > 0f,
                $"triangle {t / 3} is wound inward");
        }
    }

    [Fact]
    public void Heightmap_Corners_And_Center_Bilinear()
    {
        var heights = new float[,] { { 0f, 1f }, { 0f, 1f } }; // [z, x]: rises along x
        var s = new HeightmapSampler(Vector3.Zero, new Vector3(10, 100, 10), 2, heights);

        Assert.True(s.TrySample(0, 0, out var h00)); Assert.InRange(h00, -Tol, Tol);
        Assert.True(s.TrySample(10, 0, out var h10)); Assert.InRange(h10, 100 - Tol, 100 + Tol);
        Assert.True(s.TrySample(5, 5, out var h55)); Assert.InRange(h55, 50 - Tol, 50 + Tol);
    }

    [Fact]
    public void Heightmap_UsesOriginOffset()
    {
        var heights = new float[,] { { 0.5f, 0.5f }, { 0.5f, 0.5f } };
        var s = new HeightmapSampler(new Vector3(-700, -10, -500), new Vector3(1000, 600, 1000), 2, heights);

        Assert.True(s.TrySample(-200, 0, out var h));
        Assert.InRange(h, 290 - Tol, 290 + Tol);
    }

    [Fact]
    public void Heightmap_Outside_ReturnsFalse()
    {
        var s = new HeightmapSampler(Vector3.Zero, new Vector3(10, 100, 10), 2, new float[2, 2]);

        Assert.False(s.TrySample(-0.01f, 5, out _));
        Assert.False(s.TrySample(5, 10.01f, out _));
        Assert.False(s.TrySample(float.NaN, 5, out _));
    }
}
