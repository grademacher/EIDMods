using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using EIDMods.MapCore;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVec = System.Numerics.Vector3;

namespace EIDMods.Features.MapBake;

/// <summary>
/// Bakes the current level's <see cref="Heightfield"/> from collider geometry:
/// cache → (miss) collect on the main thread in time slices → rasterize on a background task →
/// Ready → save cache. Every few seconds, if more scenes have loaded (sub-scenes streaming in),
/// new colliders are collected and rasterized incrementally on top. Cancelled on Dispose (hot
/// reload) and on level change.
/// </summary>
internal sealed class MapBaker : IDisposable
{
    public enum State { Idle, LoadingCache, Collecting, Rasterizing, Ready, Failed }

    private const float OriginY = -1000f;        // ushort quanta at 0.05 m cover -1000..+2276 m
    private const float RecheckSeconds = 5f;

    private readonly float _cellSize;
    private readonly Func<float> _budgetMs;
    private readonly string _cacheDir = Path.Combine(Paths.CachePath, "EIDMods", "maps");

    private CancellationTokenSource _cts = new();
    private Task<(bool hit, Heightfield hf, CacheHeader header)> _cacheTask;
    private Task<Heightfield> _rasterTask;
    private GeometryCollector _collector;
    private bool _incremental;
    private Stopwatch _phase;

    private UnityEngine.Object _player;
    private string _key, _mapName;
    private HashSet<ulong> _signatures = new();
    private float _nextRecheck;
    private int _sceneCount;

    public State Current { get; private set; } = State.Idle;
    public float Progress { get; private set; }
    public Heightfield Heightfield { get; private set; }
    public IHeightSampler Terrain { get; private set; }
    public string MapName => _mapName;
    /// <summary>Increments whenever <see cref="Heightfield"/> is replaced (views rebuild on change).</summary>
    public int Version { get; private set; }

    public MapBaker(float cellSize, Func<float> budgetMs)
    {
        _cellSize = cellSize;
        _budgetMs = budgetMs;
    }

    public void Update()
    {
        var player = AG.Actors.Player.Local;
        if (player == null)
        {
            if (Current != State.Idle) Reset("no local player (left level)");
            return;
        }
        if (_player != null && player.Pointer != _player.Pointer) Reset("new local player (level change)");

        try
        {
            switch (Current)
            {
                case State.Idle: Begin(player); break;
                case State.LoadingCache: PollCache(); break;
                case State.Collecting: Collect(); break;
                case State.Rasterizing: PollRaster(); break;
                case State.Ready: MaybeIncremental(); break;
            }
        }
        catch (Exception e)
        {
            Mod.Log.LogError($"[Bake] failed in {Current}: {e}");
            Current = State.Failed;
        }
    }

    private void Begin(AG.Actors.Player player)
    {
        if (!MapKey.TryGetCurrent(out _key, out _mapName))
        {
            _key = null;
            return; // not a known map (menus); try again next frame
        }
        _player = player;
        _sceneCount = SceneManager.sceneCount;
        _phase = Stopwatch.StartNew();
        Current = State.LoadingCache;
        Mod.Log.LogInfo($"[Bake] map '{_mapName}' key {_key}: looking for cache");

        string path = CachePath();
        string key = _key;
        float cs = _cellSize;
        var token = _cts.Token;
        _cacheTask = Task.Run(() =>
        {
            if (!File.Exists(path)) return (false, (Heightfield)null, (CacheHeader)null);
            using var fs = File.OpenRead(path);
            bool ok = HeightfieldCache.TryRead(fs, key, cs, out var hf, out var header);
            token.ThrowIfCancellationRequested();
            return (ok, hf, header);
        }, token);
    }

    private void PollCache()
    {
        if (!_cacheTask.IsCompleted) return;
        if (_cacheTask.IsFaulted) Mod.Log.LogWarning($"[Bake] cache read failed: {_cacheTask.Exception?.GetBaseException().Message}");

        if (_cacheTask.IsCompletedSuccessfully && _cacheTask.Result.hit)
        {
            var (_, hf, header) = _cacheTask.Result;
            _signatures = new HashSet<ulong>(header.ColliderSignatures);
            SetReady(hf);
            Mod.Log.LogInfo($"[Bake] cache hit: {hf.ColumnCount:N0} columns, {_signatures.Count:N0} colliders, ready in {_phase.ElapsedMilliseconds} ms; managed heap {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
            // Sub-scenes may already have loaded more than the cache saw: check right away.
            _nextRecheck = 0f;
            _sceneCount = -1;
            return;
        }

        Mod.Log.LogInfo("[Bake] cache miss: collecting collider geometry");
        StartCollect(incremental: false);
    }

    private void StartCollect(bool incremental)
    {
        _incremental = incremental;
        _collector = new GeometryCollector(incremental ? _signatures : null, _cellSize);
        _phase = Stopwatch.StartNew();
        if (!incremental) Current = State.Collecting;
    }

    private void Collect()
    {
        bool done = _collector.Step(_budgetMs());
        if (!_incremental) Progress = 0.8f * _collector.Progress;
        if (!done) return;

        var c = _collector;
        var r = c.Result;
        Mod.Log.LogInfo($"[Bake] collected {r.Colliders:N0} colliders ({r.Meshes.Count:N0} meshes, {r.Boxes.Count:N0} boxes, {r.Probed:N0} probed, {r.Skipped:N0} known) " +
                        $"in {c.TotalMs:F0} ms main thread over {_phase.ElapsedMilliseconds} ms; snapshot {c.SnapshotMs:F0} ms, max step {c.MaxStepMs:F1} ms");
        _collector = null;

        if (_incremental && r.Colliders == 0) { Current = State.Ready; return; }

        var baseHf = _incremental ? Heightfield : null;
        float cs = _cellSize;
        var token = _cts.Token;
        _phase = Stopwatch.StartNew();
        _rasterTask = Task.Run(() => Rasterize(r, baseHf, cs, token), token);
        foreach (var s in r.Signatures) _signatures.Add(s);
        Current = State.Rasterizing;
    }

    private static Heightfield Rasterize(CollectedGeometry g, Heightfield baseHf, float cellSize, CancellationToken token)
    {
        var b = baseHf != null ? HeightfieldBuilder.From(baseHf) : new HeightfieldBuilder(cellSize, OriginY);
        var world = Array.Empty<NVec>();

        for (int i = 0; i < g.Meshes.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var m = g.Meshes[i];
            if (world.Length < m.LocalVerts.Length) world = new NVec[m.LocalVerts.Length];
            for (int v = 0; v < m.LocalVerts.Length; v++) world[v] = NVec.Transform(m.LocalVerts[v], m.LocalToWorld);
            var verts = new ReadOnlySpan<NVec>(world, 0, m.LocalVerts.Length);
            if (m.Solid) TriangleRasterizer.RasterizeSolid(b, verts, m.Indices, m.Flags);
            else TriangleRasterizer.RasterizeShell(b, verts, m.Indices, m.Flags);
        }

        var boxVerts = new List<NVec>(8);
        var boxIdx = new List<int>(36);
        for (int i = 0; i < g.Boxes.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var (center, size, l2w, flags) = g.Boxes[i];
            boxVerts.Clear();
            boxIdx.Clear();
            BoxTriangles.Append(boxVerts, boxIdx, l2w, center, size);
            TriangleRasterizer.RasterizeSolid(b, boxVerts.ToArray(), boxIdx.ToArray(), flags);
        }

        foreach (var (cx, cz, bottom, top, flags) in g.DirectSpans) b.AddSpan(cx, cz, bottom, top, flags);
        token.ThrowIfCancellationRequested();
        return b.Build();
    }

    private void PollRaster()
    {
        if (!_rasterTask.IsCompleted) { Progress = Math.Max(Progress, 0.85f); return; }
        if (_rasterTask.IsCanceled) return;
        if (_rasterTask.IsFaulted)
        {
            Current = State.Failed;
            Mod.Log.LogError($"[Bake] rasterize failed: {_rasterTask.Exception?.GetBaseException()}");
            return;
        }

        var hf = _rasterTask.Result;
        Mod.Log.LogInfo($"[Bake] rasterized {(_incremental ? "incremental " : "")}geometry in {_phase.ElapsedMilliseconds} ms (background): {hf.ColumnCount:N0} columns; managed heap {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
        SetReady(hf);
        SaveCache(hf);
    }

    private void SetReady(Heightfield hf)
    {
        Heightfield = hf;
        Terrain ??= TerrainSampler.FromActive();
        Version++;
        Progress = 1f;
        Current = State.Ready;
        _nextRecheck = Time.unscaledTime + RecheckSeconds;
    }

    private void MaybeIncremental()
    {
        if (_collector != null) { Collect(); return; }
        if (Time.unscaledTime < _nextRecheck) return;
        _nextRecheck = Time.unscaledTime + RecheckSeconds;

        int scenes = SceneManager.sceneCount;
        if (scenes == _sceneCount) return;
        Mod.Log.LogInfo($"[Bake] loaded scenes changed ({_sceneCount} -> {scenes}): collecting new colliders");
        _sceneCount = scenes;
        StartCollect(incremental: true);
    }

    private void SaveCache(Heightfield hf)
    {
        string path = CachePath(), dir = _cacheDir;
        var header = new CacheHeader(HeightfieldCache.CurrentFormat, _key, _cellSize, new List<ulong>(_signatures));
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                using (var fs = File.Create(tmp)) HeightfieldCache.Write(fs, hf, header);
                File.Move(tmp, path, overwrite: true);
                Mod.Log.LogInfo($"[Bake] cache saved: {path} ({new FileInfo(path).Length / 1024:N0} KB)");
            }
            catch (Exception e)
            {
                Mod.Log.LogWarning($"[Bake] cache save failed: {e.Message}");
            }
        });
    }

    private string CachePath() => Path.Combine(_cacheDir, _key + ".bin.gz");

    private void Reset(string why)
    {
        if (Current is State.LoadingCache or State.Collecting or State.Rasterizing)
            Mod.Log.LogInfo($"[Bake] cancelled ({why}) during {Current}");
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _cacheTask = null;
        _rasterTask = null;
        _collector = null;
        _player = null;
        _key = null;
        _signatures = new HashSet<ulong>();
        Heightfield = null;
        Terrain = null;
        Version++;
        Progress = 0f;
        Current = State.Idle;
    }

    public void Dispose()
    {
        if (Current is State.LoadingCache or State.Collecting or State.Rasterizing)
            Mod.Log.LogInfo($"[Bake] cancelled (unload) during {Current}");
        _cts.Cancel();
        _cts.Dispose();
    }
}
