using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using EIDMods.MapCore;
using Xunit;
using Xunit.Abstractions;

namespace EIDMods.MapCore.Tests;

public class TriangleRasterizerTests
{
    private const float Tol = 0.06f;
    private readonly ITestOutputHelper _out;

    public TriangleRasterizerTests(ITestOutputHelper output) => _out = output;

    private static HeightfieldBuilder NewBuilder() => new(cellSize: 0.5f, originY: -100f);

    /// <summary>Axis-aligned cuboid as 8 verts + 12 outward triangles, appended to the lists (test-local helper).</summary>
    private static void AddCuboid(List<Vector3> v, List<int> idx, Vector3 min, Vector3 max)
    {
        int o = v.Count;
        v.Add(new(min.X, min.Y, min.Z)); v.Add(new(max.X, min.Y, min.Z));
        v.Add(new(max.X, min.Y, max.Z)); v.Add(new(min.X, min.Y, max.Z));
        v.Add(new(min.X, max.Y, min.Z)); v.Add(new(max.X, max.Y, min.Z));
        v.Add(new(max.X, max.Y, max.Z)); v.Add(new(min.X, max.Y, max.Z));
        int[] t =
        {
            0, 1, 2, 0, 2, 3, // bottom
            4, 6, 5, 4, 7, 6, // top
            0, 4, 5, 0, 5, 1, // -z
            3, 2, 6, 3, 6, 7, // +z
            0, 3, 7, 0, 7, 4, // -x
            1, 5, 6, 1, 6, 2, // +x
        };
        foreach (var i in t) idx.Add(o + i);
    }

    [Fact]
    public void Shell_FlatQuad_OneSpanPerCoveredCell()
    {
        var b = NewBuilder();
        var v = new[] { new Vector3(0, 1, 0), new Vector3(2, 1, 0), new Vector3(2, 1, 2), new Vector3(0, 1, 2) };
        TriangleRasterizer.RasterizeShell(b, v, new[] { 0, 2, 1, 0, 3, 2 }, SpanFlags.None);
        var hf = b.Build();

        Assert.Equal(16, hf.ColumnCount);
        for (int z = 0; z < 4; z++)
        for (int x = 0; x < 4; x++)
        {
            var spans = hf.GetSpans(x, z).ToArray();
            Assert.Single(spans);
            Assert.InRange(spans[0].Bottom, 1 - Tol, 1 + Tol);
            Assert.InRange(spans[0].Top, 1 - Tol, 1 + Tol);
            Assert.Equal(SpanFlags.WalkableTop, spans[0].Flags);
        }
        Assert.Equal(0, hf.GetSpans(4, 0).Length); // the edge at x = 2.0 must not spill into the next cell
    }

    [Fact]
    public void Shell_ThinVerticalWall_CoversEveryCellAlongIt()
    {
        var b = NewBuilder();
        var v = new[] { new Vector3(1.05f, 0, 0), new Vector3(1.05f, 0, 3), new Vector3(1.05f, 4, 3), new Vector3(1.05f, 4, 0) };
        TriangleRasterizer.RasterizeShell(b, v, new[] { 0, 1, 2, 0, 2, 3 }, SpanFlags.None);
        var hf = b.Build();

        for (int z = 0; z <= 5; z++)
        {
            var spans = hf.GetSpans(2, z).ToArray();
            Assert.Single(spans);
            Assert.InRange(spans[0].Bottom, -Tol, Tol);
            Assert.InRange(spans[0].Top, 4 - Tol, 4 + Tol);
            Assert.Equal(SpanFlags.None, spans[0].Flags);
        }
        Assert.Equal(6, hf.ColumnCount);
    }

    [Fact]
    public void Shell_SlopedTriangle_SpanCoversHeightRangeWithinCell()
    {
        // Ramp with y = x over the triangle x + z <= 2.
        var b = NewBuilder();
        var v = new[] { new Vector3(0, 0, 0), new Vector3(2, 2, 0), new Vector3(0, 0, 2) };
        TriangleRasterizer.RasterizeShell(b, v, new[] { 0, 2, 1 }, SpanFlags.None);
        var spans = b.Build().GetSpans(1, 0).ToArray(); // x 0.5..1.0, z 0..0.5

        Assert.Single(spans);
        Assert.InRange(spans[0].Bottom, 0.5f - Tol, 0.5f + Tol);
        Assert.InRange(spans[0].Top, 1.0f - Tol, 1.0f + Tol);
        Assert.Equal(SpanFlags.None, spans[0].Flags); // 45 degrees: normal.y ~ 0.71, not walkable
    }

    [Fact]
    public void Shell_BaseFlags_ArePassedThrough()
    {
        var b = NewBuilder();
        var v = new[] { new Vector3(0, 0, 0), new Vector3(0.4f, 0, 0), new Vector3(0, 0, 0.4f) };
        TriangleRasterizer.RasterizeShell(b, v, new[] { 0, 2, 1 }, SpanFlags.FloorOnly);
        var s = b.Build().GetSpans(0, 0)[0];

        Assert.Equal(SpanFlags.FloorOnly | SpanFlags.WalkableTop, s.Flags);
    }

    [Fact]
    public void Solid_Box_FillsInteriorColumns()
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        AddCuboid(v, idx, new Vector3(0, -1, 0), new Vector3(2, 1, 2));

        var solid = NewBuilder();
        TriangleRasterizer.RasterizeSolid(solid, v.ToArray(), idx.ToArray(), SpanFlags.None);
        var s = solid.Build().GetSpans(1, 1).ToArray();
        Assert.Single(s);
        Assert.InRange(s[0].Bottom, -1 - Tol, -1 + Tol);
        Assert.InRange(s[0].Top, 1 - Tol, 1 + Tol);
        Assert.Equal(SpanFlags.WalkableTop, s[0].Flags);

        var shell = NewBuilder();
        TriangleRasterizer.RasterizeShell(shell, v.ToArray(), idx.ToArray(), SpanFlags.None);
        Assert.DoesNotContain(shell.Build().GetSpans(1, 1).ToArray(), sp => sp.Bottom <= 0f && sp.Top >= 0f);
    }

    [Fact]
    public void Shell_Arch_LeavesDoorwayOpen()
    {
        // Review Focus 1: a non-convex arch (two pillars + lintel in ONE mesh) rasterized as a shell.
        var v = new List<Vector3>();
        var idx = new List<int>();
        AddCuboid(v, idx, new Vector3(0, 0, 0), new Vector3(0.5f, 3, 0.5f));      // left pillar
        AddCuboid(v, idx, new Vector3(2, 0, 0), new Vector3(2.5f, 3, 0.5f));      // right pillar
        AddCuboid(v, idx, new Vector3(0, 2.5f, 0), new Vector3(2.5f, 3, 0.5f));   // lintel

        var b = NewBuilder();
        TriangleRasterizer.RasterizeShell(b, v.ToArray(), idx.ToArray(), SpanFlags.None);
        var hf = b.Build();

        foreach (int x in new[] { 1, 2, 3 }) // x 0.5..2.0: the doorway
            Assert.DoesNotContain(hf.GetSpans(x, 0).ToArray(), s => s.Bottom < 1.6f && s.Top > 0.6f);
        Assert.Contains(hf.GetSpans(0, 0).ToArray(), s => s.Bottom < 1.6f && s.Top > 0.6f); // pillar side walls
    }

    [Fact]
    public void Solid_EmptyInput_AddsNothing()
    {
        var b = NewBuilder();
        TriangleRasterizer.RasterizeSolid(b, Array.Empty<Vector3>(), Array.Empty<int>(), SpanFlags.None);
        Assert.Equal(0, b.Build().ColumnCount);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Rasterize_1MillionTriangles_UnderTwoSeconds()
    {
        // Opt-in (set EIDMODS_PERF=1): too slow for every run, used to record timings.
        if (Environment.GetEnvironmentVariable("EIDMODS_PERF") != "1") return;

        var rng = new Random(1);
        const int n = 1_000_000;
        var v = new Vector3[n * 3];
        var idx = new int[n * 3];
        for (int i = 0; i < n; i++)
        {
            var o = new Vector3((float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 50f, (float)rng.NextDouble() * 1000f);
            v[i * 3] = o;
            v[i * 3 + 1] = o + new Vector3(0.8f, (float)rng.NextDouble(), 0.1f);
            v[i * 3 + 2] = o + new Vector3(0.1f, (float)rng.NextDouble(), 0.8f);
            idx[i * 3] = i * 3; idx[i * 3 + 1] = i * 3 + 1; idx[i * 3 + 2] = i * 3 + 2;
        }
        var b = NewBuilder();
        var sw = Stopwatch.StartNew();
        TriangleRasterizer.RasterizeShell(b, v, idx, SpanFlags.None);
        long rasterMs = sw.ElapsedMilliseconds;
        var hf = b.Build();
        sw.Stop();
        _out.WriteLine($"1M triangles: {sw.ElapsedMilliseconds} ms total ({rasterMs} ms rasterize, {sw.ElapsedMilliseconds - rasterMs} ms build), {hf.ColumnCount} columns");
        Assert.True(sw.Elapsed.TotalSeconds < 2.0, $"took {sw.Elapsed.TotalSeconds:F2}s");
    }
}
