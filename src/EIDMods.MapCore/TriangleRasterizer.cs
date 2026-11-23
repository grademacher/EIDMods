using System;
using System.Collections.Generic;
using System.Numerics;

namespace EIDMods.MapCore;

/// <summary>
/// Recast-style triangle rasterization into a <see cref="HeightfieldBuilder"/>: each triangle is
/// clipped to every cell column it overlaps (first to the z row slab, then to the x cell slab), and
/// the clipped polygon's min/max y becomes that triangle's span in the cell. Conservative, so thin
/// walls still mark every cell they pass through.
/// </summary>
public static class TriangleRasterizer
{
    private const float WalkableNormalY = 0.75f;
    private const float WalkTopTolerance = 0.1f;
    private const int MaxPoly = 12;

    /// <summary>Surfaces only: one span per triangle per cell (non-convex meshes, so arches stay open).</summary>
    public static void RasterizeShell(HeightfieldBuilder hf, ReadOnlySpan<Vector3> verts, ReadOnlySpan<int> indices, SpanFlags baseFlags)
    {
        var emit = new ShellEmitter { Hf = hf };
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = verts[indices[t]]; var b = verts[indices[t + 1]]; var c = verts[indices[t + 2]];
            emit.Flags = baseFlags | (IsWalkable(a, b, c) ? SpanFlags.WalkableTop : SpanFlags.None);
            ForEachCell(hf.CellSize, a, b, c, ref emit);
        }
    }

    private struct ShellEmitter : ICellEmitter
    {
        public HeightfieldBuilder Hf;
        public SpanFlags Flags;
        public void Emit(int cx, int cz, float minY, float maxY) => Hf.AddSpan(cx, cz, minY, maxY, Flags);
    }

    private struct SolidEmitter : ICellEmitter
    {
        public Dictionary<long, (float min, float max, bool walk)> Cells;
        public bool Walk;

        public void Emit(int cx, int cz, float minY, float maxY)
        {
            long key = ((long)cx << 32) | (uint)cz;
            if (!Cells.TryGetValue(key, out var cur))
            {
                Cells[key] = (minY, maxY, Walk);
                return;
            }
            bool w = maxY > cur.max + WalkTopTolerance ? Walk
                   : cur.max > maxY + WalkTopTolerance ? cur.walk
                   : cur.walk || Walk;
            Cells[key] = (Math.Min(cur.min, minY), Math.Max(cur.max, maxY), w);
        }
    }

    /// <summary>
    /// A closed convex volume: each cell column is filled from the lowest to the highest point of
    /// all triangles of this call. WalkableTop comes from the triangle(s) that set the top.
    /// </summary>
    public static void RasterizeSolid(HeightfieldBuilder hf, ReadOnlySpan<Vector3> verts, ReadOnlySpan<int> indices, SpanFlags baseFlags)
    {
        var emit = new SolidEmitter { Cells = new Dictionary<long, (float min, float max, bool walk)>() };
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = verts[indices[t]]; var b = verts[indices[t + 1]]; var c = verts[indices[t + 2]];
            emit.Walk = IsWalkable(a, b, c);
            ForEachCell(hf.CellSize, a, b, c, ref emit);
        }

        foreach (var kv in emit.Cells)
        {
            int cx = (int)(kv.Key >> 32), cz = (int)(uint)kv.Key;
            var (min, max, walk) = kv.Value;
            hf.AddSpan(cx, cz, min, max, baseFlags | (walk ? SpanFlags.WalkableTop : SpanFlags.None));
        }
    }

    private static bool IsWalkable(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        return len > 1e-12f && MathF.Abs(n.Y / len) > WalkableNormalY;
    }

    private interface ICellEmitter
    {
        void Emit(int cx, int cz, float minY, float maxY);
    }

    /// <summary>
    /// Calls <paramref name="emit"/> once per cell the triangle overlaps, with its y range there.
    /// Generic over a struct emitter so the hot loop has no delegate/closure allocation.
    /// </summary>
    private static void ForEachCell<TEmit>(float cs, Vector3 a, Vector3 b, Vector3 c, ref TEmit emit) where TEmit : struct, ICellEmitter
    {
        Span<Vector3> tri = stackalloc Vector3[3] { a, b, c };
        Span<Vector3> row = stackalloc Vector3[MaxPoly];
        Span<Vector3> cell = stackalloc Vector3[MaxPoly];
        Span<Vector3> tmp = stackalloc Vector3[MaxPoly];

        // Outward normal (Unity collision meshes: cross(b-a, c-a) points out of the solid). Used to
        // put a face lying exactly on a cell boundary into the cell on its solid's side.
        var normal = Vector3.Cross(b - a, c - a);

        float minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        CellRange(minZ, maxZ, cs, normal.Z, out int z0, out int z1);

        for (int cz = z0; cz <= z1; cz++)
        {
            int rowCount = ClipSlab(tri, row, tmp, axis: 2, cz * cs, (cz + 1) * cs);
            if (rowCount == 0) continue;

            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < rowCount; i++) { minX = MathF.Min(minX, row[i].X); maxX = MathF.Max(maxX, row[i].X); }
            CellRange(minX, maxX, cs, normal.X, out int x0, out int x1);

            for (int cx = x0; cx <= x1; cx++)
            {
                int n = ClipSlab(row[..rowCount], cell, tmp, axis: 0, cx * cs, (cx + 1) * cs);
                if (n == 0) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < n; i++) { lo = MathF.Min(lo, cell[i].Y); hi = MathF.Max(hi, cell[i].Y); }
                emit.Emit(cx, cz, lo, hi);
            }
        }
    }

    /// <summary>
    /// Cells covering [min, max]. An extent ending exactly on a cell boundary doesn't spill into the
    /// next cell. A zero-width extent (a face perpendicular to this axis) still gets one cell; if it
    /// lies exactly ON a boundary, it goes to the side its solid is on, i.e. against the outward
    /// normal: a +x-facing face at x = 0.5 belongs to cell 0 (the wall's side), not cell 1.
    /// </summary>
    private static void CellRange(float min, float max, float cs, float normalComponent, out int c0, out int c1)
    {
        c0 = (int)MathF.Floor(min / cs);
        c1 = Math.Max(c0, (int)MathF.Ceiling(max / cs) - 1);

        if (max - min < 1e-5f)
        {
            float k = MathF.Round(min / cs);
            if (MathF.Abs(min / cs - k) < 1e-4f && normalComponent != 0f)
                c0 = c1 = normalComponent > 0f ? (int)k - 1 : (int)k;
        }
    }

    /// <summary>Clips a convex polygon to lo &lt;= coord &lt;= hi on the given axis (0 = x, 2 = z).</summary>
    private static int ClipSlab(ReadOnlySpan<Vector3> poly, Span<Vector3> result, Span<Vector3> tmp, int axis, float lo, float hi)
    {
        int n = ClipPlane(poly, tmp, axis, lo, keepAbove: true);
        return n == 0 ? 0 : ClipPlane(tmp[..n], result, axis, hi, keepAbove: false);
    }

    /// <summary>Sutherland–Hodgman against one axis-aligned plane; points on the plane are kept.</summary>
    private static int ClipPlane(ReadOnlySpan<Vector3> poly, Span<Vector3> result, int axis, float d, bool keepAbove)
    {
        int n = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var cur = poly[i];
            var prev = poly[(i + poly.Length - 1) % poly.Length];
            float dc = (axis == 0 ? cur.X : cur.Z) - d, dp = (axis == 0 ? prev.X : prev.Z) - d;
            if (!keepAbove) { dc = -dc; dp = -dp; }
            bool inC = dc >= 0f, inP = dp >= 0f;

            if (inC != inP && n < result.Length)
            {
                float t = dp / (dp - dc);
                result[n++] = prev + (cur - prev) * t;
            }
            if (inC && n < result.Length) result[n++] = cur;
        }
        return n;
    }
}
