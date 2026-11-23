using System;
using System.Collections.Generic;
using System.Diagnostics;
using EIDMods.Features.Minimap;
using EIDMods.MapCore;
using Il2CppInterop.Runtime;
using UnityEngine;
using NMat = System.Numerics.Matrix4x4;
using NVec = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace EIDMods.Features.MapBake;

/// <summary>A collider's geometry in local space plus its local→world transform (row-vector convention).</summary>
internal sealed class MeshInstance
{
    public NVec[] LocalVerts;
    public int[] Indices;
    public NMat LocalToWorld;
    public SpanFlags Flags;
    public bool Solid;
}

/// <summary>Everything the background rasterizer needs; plain managed data only.</summary>
internal sealed class CollectedGeometry
{
    public readonly List<MeshInstance> Meshes = new();
    public readonly List<(NVec center, NVec size, NMat localToWorld, SpanFlags flags)> Boxes = new();
    public readonly List<(int cx, int cz, float bottom, float top, SpanFlags flags)> DirectSpans = new();
    public readonly HashSet<ulong> Signatures = new();
    public int Colliders, Skipped, Probed;
    public long Triangles;
}

/// <summary>
/// Main-thread, time-sliced extraction of static level colliders into <see cref="CollectedGeometry"/>.
/// Boxes and readable meshes are copied as data; anything else (unreadable meshes, spheres,
/// capsules, …) is measured per footprint cell with Collider.Raycast. Terrain is skipped (sampled
/// at classification time).
/// </summary>
internal sealed class GeometryCollector
{
    private const long MaxProbeCells = 40_000;

    private readonly IReadOnlySet<ulong> _skip;
    private readonly float _cellSize;
    private readonly Dictionary<int, (NVec[] verts, int[] idx)> _meshCache = new();
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Object> _colliders;
    private int _next;

    // Resumable state for probing one large collider across several steps.
    private Collider _probe;
    private Bounds _probeBounds;
    private int _probeCx0, _probeCx1, _probeCz0, _probeCz1, _probeCx, _probeCz;
    private SpanFlags _probeFlags;

    public CollectedGeometry Result { get; } = new();
    public float Progress => _colliders == null || _colliders.Length == 0 ? 0f : (float)_next / _colliders.Length;
    public double MaxStepMs { get; private set; }
    public double TotalMs { get; private set; }
    public double SnapshotMs { get; private set; }

    public GeometryCollector(IReadOnlySet<ulong> skip, float cellSize)
    {
        _skip = skip ?? new HashSet<ulong>();
        _cellSize = cellSize;
    }

    /// <summary>Does up to <paramref name="budgetMs"/> of work. Returns true when everything is collected.</summary>
    public bool Step(float budgetMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (_colliders == null)
            {
                Snapshot();
                SnapshotMs = sw.Elapsed.TotalMilliseconds;
                return _colliders.Length == 0;
            }

            while (sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (_probe != null) { ContinueProbe(sw, budgetMs); continue; }
                if (_next >= _colliders.Length) return true;
                Process(_colliders[_next++]?.TryCast<Collider>());
            }
            return _next >= _colliders.Length && _probe == null;
        }
        finally
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            TotalMs += ms;
            if (_colliders != null && ms > MaxStepMs) MaxStepMs = ms;
        }
    }

    /// <summary>
    /// Only grabs the raw array: filtering 75k colliders (several interop calls each) in one frame
    /// was a 320 ms hitch, so that happens per collider inside the time-sliced steps.
    /// </summary>
    private void Snapshot()
    {
        _colliders = Object.FindObjectsByType(Il2CppType.Of<Collider>(), FindObjectsSortMode.None);
    }

    private void Process(Collider c)
    {
        if (c == null) return; // destroyed since the snapshot
        if (((1 << c.gameObject.layer) & MapGrid.FloorMask) == 0) return;
        if (c.isTrigger || !c.enabled || c.attachedRigidbody != null) return;
        if (c.TryCast<TerrainCollider>() != null) return;

        ulong sig = ColliderSignature.Of(c);
        if (_skip.Contains(sig) || !Result.Signatures.Add(sig)) { Result.Skipped++; return; }
        Result.Colliders++;

        var flags = c.gameObject.layer == 9 ? SpanFlags.FloorOnly : SpanFlags.None;

        var box = c.TryCast<BoxCollider>();
        if (box != null)
        {
            Result.Boxes.Add((ToN(box.center), ToN(box.size), ToN(box.transform.localToWorldMatrix), flags));
            Result.Triangles += 12;
            return;
        }

        var mc = c.TryCast<MeshCollider>();
        if (mc != null)
        {
            var mesh = mc.sharedMesh;
            if (mesh == null) return; // no mesh: no collision either
            if (mesh.isReadable)
            {
                var (verts, idx) = ReadMesh(mesh);
                Result.Meshes.Add(new MeshInstance
                {
                    LocalVerts = verts, Indices = idx, LocalToWorld = ToN(mc.transform.localToWorldMatrix),
                    Flags = flags, Solid = mc.convex,
                });
                Result.Triangles += idx.Length / 3;
                return;
            }
        }

        StartProbe(c, flags);
    }

    private (NVec[] verts, int[] idx) ReadMesh(Mesh mesh)
    {
        int id = mesh.GetInstanceID();
        if (_meshCache.TryGetValue(id, out var cached)) return cached;
        var verts = CopyVector3s(mesh.vertices);
        var idx = CopyInts(mesh.triangles);
        _meshCache[id] = (verts, idx);
        return (verts, idx);
    }

    // ---- generic colliders: measure with Collider.Raycast per footprint cell ----------------------

    private void StartProbe(Collider c, SpanFlags flags)
    {
        _probe = c;
        _probeFlags = flags;
        _probeBounds = c.bounds;
        _probeCx0 = Mathf.FloorToInt(_probeBounds.min.x / _cellSize);
        _probeCx1 = Mathf.FloorToInt(_probeBounds.max.x / _cellSize);
        _probeCz0 = Mathf.FloorToInt(_probeBounds.min.z / _cellSize);
        _probeCz1 = Mathf.FloorToInt(_probeBounds.max.z / _cellSize);
        _probeCx = _probeCx0;
        _probeCz = _probeCz0;

        // Huge generic colliders (whole-level blockers, kill volumes that aren't triggers, ...) would
        // cost millions of raycasts; skip them and say so.
        long cells = (long)(_probeCx1 - _probeCx0 + 1) * (_probeCz1 - _probeCz0 + 1);
        if (cells > MaxProbeCells)
        {
            Mod.Log.LogInfo($"[Bake] skipping unreadable collider '{c.name}' ({c.GetIl2CppType().Name}): {cells:N0} footprint cells");
            _probe = null;
            return;
        }
        Result.Probed++;
    }

    private void ContinueProbe(Stopwatch sw, float budgetMs)
    {
        var b = _probeBounds;
        float len = b.size.y + 0.2f;
        while (_probeCz <= _probeCz1)
        {
            if (sw.Elapsed.TotalMilliseconds >= budgetMs) return;
            if (_probe == null) break; // destroyed mid-probe

            float x = (_probeCx + 0.5f) * _cellSize, z = (_probeCz + 0.5f) * _cellSize;
            if (_probe.Raycast(new Ray(new Vector3(x, b.max.y + 0.1f, z), Vector3.down), out var top, len)
                && _probe.Raycast(new Ray(new Vector3(x, b.min.y - 0.1f, z), Vector3.up), out var bottom, len))
            {
                var f = _probeFlags | (top.normal.y > 0.75f ? SpanFlags.WalkableTop : SpanFlags.None);
                Result.DirectSpans.Add((_probeCx, _probeCz, bottom.point.y, top.point.y, f));
            }

            if (++_probeCx > _probeCx1) { _probeCx = _probeCx0; _probeCz++; }
        }
        _probe = null;
    }

    // ---- conversions ------------------------------------------------------------------------------

    private static NVec ToN(Vector3 v) => new(v.x, v.y, v.z);

    /// <summary>Unity matrices are column-vector (M * v); System.Numerics is row-vector (v * M): transpose.</summary>
    private static NMat ToN(Matrix4x4 m) => new(
        m.m00, m.m10, m.m20, m.m30,
        m.m01, m.m11, m.m21, m.m31,
        m.m02, m.m12, m.m22, m.m32,
        m.m03, m.m13, m.m23, m.m33);

    // IL2CPP arrays: object header (klass, monitor), bounds, max_length = 4 pointers, then elements.
    private static unsafe NVec[] CopyVector3s(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> src)
    {
        var dst = new NVec[src.Length];
        long bytes = (long)dst.Length * sizeof(NVec);
        fixed (NVec* d = dst)
            Buffer.MemoryCopy((void*)(src.Pointer + 4 * IntPtr.Size), d, bytes, bytes);
        return dst;
    }

    private static unsafe int[] CopyInts(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> src)
    {
        var dst = new int[src.Length];
        long bytes = (long)dst.Length * sizeof(int);
        fixed (int* d = dst)
            Buffer.MemoryCopy((void*)(src.Pointer + 4 * IntPtr.Size), d, bytes, bytes);
        return dst;
    }
}

/// <summary>Stable-across-sessions identity of a collider (instance IDs change every session).</summary>
internal static class ColliderSignature
{
    public static ulong Of(Collider c)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        void Int(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= prime; } }
        void Str(string s) { foreach (char ch in s ?? "") { h ^= ch; h *= prime; } Int(-1); }

        Int(c.gameObject.layer);
        Str(c.GetIl2CppType().Name);
        var mc = c.TryCast<MeshCollider>();
        Str(mc != null && mc.sharedMesh != null ? mc.sharedMesh.name : "");
        var b = c.bounds;
        Int(Mathf.RoundToInt(b.center.x * 100)); Int(Mathf.RoundToInt(b.center.y * 100)); Int(Mathf.RoundToInt(b.center.z * 100));
        Int(Mathf.RoundToInt(b.size.x * 100)); Int(Mathf.RoundToInt(b.size.y * 100)); Int(Mathf.RoundToInt(b.size.z * 100));
        return h;
    }
}
