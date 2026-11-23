using System;
using System.Collections.Generic;
using EIDMods.MapCore;
using EIDMods.MapCore.Markers;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>
/// Loot as small yellow dots: containers (LootTableContainer / LootContainer), corpse loot
/// (PawnLootTableContainer) and floor items (SingleLootItem).
///
/// Discovery: FindObjectsByType costs 10-16 ms per type in one call (F4 probe, 2026-11-23) and can't
/// be time-sliced, so it only runs once per level and when more scenes load, one type per frame.
/// Corpse loot and drops are found with a small death watch: a second after an enemy dies, a 3 m
/// OverlapSphere at the body finds the new loot objects (microseconds, not a scene-wide scan; final
/// review: per-kill scans stalled the frame 20-30 ms). Opened/picked-up state is polled about 60
/// objects per frame; destroyed ones are compacted away at the end of each full pass.
/// Only explored cells show loot, unless the full map is revealed.
/// </summary>
internal sealed class LootMarkers : IMarkerProvider
{
    private const int PollPerFrame = 60;
    private const float SceneCheckInterval = 1f, KillLootDelay = 1f, DeathWatchInterval = 0.25f;
    private const float DeathWatchRadius = 80f, LootSearchRadius = 3f;

    private enum Source : byte { TableContainer, Container, Corpse, FloorItem }

    private sealed class Entry
    {
        public Component Obj;
        public Source Source;
        public Vector3 Pos;
        public bool Opened, Gone;
    }

    private readonly MarkerConfig _cfg;
    private readonly Func<float, float, bool> _isExplored;
    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private readonly List<Entry> _list = new();
    private readonly Queue<Source> _scanQueue = new();
    private readonly List<IntPtr> _dead = new();

    // Death watch: live pawns near the player (last position), and bodies to search for loot.
    private readonly Dictionary<IntPtr, Vector3> _alivePawns = new();
    private readonly HashSet<IntPtr> _seenAlive = new();
    private readonly List<(Vector3 pos, float at)> _pendingBodies = new();
    private float _nextDeathWatch;

    private string _scenePath;
    private int _sceneCount = -1, _pollIndex;
    private float _nextSceneCheck;

    /// <param name="isExplored">(worldX, worldZ) → whether that spot may show loot (explored, or full map).</param>
    public LootMarkers(MarkerConfig cfg, Func<float, float, bool> isExplored)
    {
        _cfg = cfg;
        _isExplored = isExplored;
    }

    public void Update(float now, Vector3 playerPos)
    {
        if (now >= _nextSceneCheck)
        {
            _nextSceneCheck = now + SceneCheckInterval;
            string path = SceneManager.GetActiveScene().path;
            int count = SceneManager.sceneCount;
            if (path != _scenePath) { Reset(); _scenePath = path; }
            if (count != _sceneCount) { _sceneCount = count; QueueAll(); }
        }

        if (_cfg.ShowCorpseLoot.Value || _cfg.ShowFloorItems.Value) WatchDeaths(now, playerPos);
        SearchBodies(now);

        if (_scanQueue.Count > 0) Scan(_scanQueue.Dequeue());
        Poll();
    }

    /// <summary>Notes where nearby pawns die (independent of the enemy-marker settings).</summary>
    private void WatchDeaths(float now, Vector3 playerPos)
    {
        if (now < _nextDeathWatch) return;
        _nextDeathWatch = now + DeathWatchInterval;
        float r2 = DeathWatchRadius * DeathWatchRadius;

        _seenAlive.Clear();
        var actors = AG.Actors.Actor.All;
        int n = actors?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            var pawn = actors[i]?.TryCast<AG.Enemies.Pawn>();
            if (pawn == null) continue;
            if (!pawn.IsAlive)
            {
                if (_alivePawns.Remove(pawn.Pointer))
                    _pendingBodies.Add((pawn.transform.position, now + KillLootDelay));
                continue;
            }
            var pos = pawn.transform.position;
            if ((pos - playerPos).sqrMagnitude > r2) continue;
            _alivePawns[pawn.Pointer] = pos;
            _seenAlive.Add(pawn.Pointer);
        }
        // Pawns that left the list or the radius: forget them (no body search).
        _dead.Clear();
        foreach (var k in _alivePawns.Keys) if (!_seenAlive.Contains(k)) _dead.Add(k);
        foreach (var k in _dead) _alivePawns.Remove(k);
    }

    /// <summary>A second after a death, look for loot objects around the body (cheap overlap query).</summary>
    private void SearchBodies(float now)
    {
        for (int b = _pendingBodies.Count - 1; b >= 0; b--)
        {
            var (pos, at) = _pendingBodies[b];
            if (now < at) continue;
            _pendingBodies.RemoveAt(b);

            var cols = Physics.OverlapSphere(pos, LootSearchRadius, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null) continue;
                var corpse = col.GetComponentInParent<AG.Environment.Loot.PawnLootTableContainer>();
                if (corpse != null) { Add(corpse, Source.Corpse); continue; }
                var item = col.GetComponentInParent<AG.Environment.Loot.SingleLootItem>();
                if (item != null) Add(item, Source.FloorItem);
            }
        }
    }

    private void Add(Component c, Source source)
    {
        if (_entries.ContainsKey(c.Pointer)) return;
        var e = new Entry { Obj = c, Source = source, Pos = c.transform.position };
        _entries[c.Pointer] = e;
        _list.Add(e);
    }

    public void Collect(float now, List<MapMarker> into)
    {
        foreach (var e in _list)
        {
            if (e.Gone) continue;
            MarkerKind kind;
            switch (e.Source)
            {
                case Source.TableContainer:
                case Source.Container:
                    if (!_cfg.ShowContainers.Value) continue;
                    if (e.Opened && !_cfg.ShowOpenedLoot.Value) continue;
                    kind = e.Opened ? MarkerKind.ContainerOpened : MarkerKind.Container;
                    break;
                case Source.Corpse:
                    if (!_cfg.ShowCorpseLoot.Value || e.Opened) continue;
                    kind = MarkerKind.CorpseLoot;
                    break;
                default:
                    if (!_cfg.ShowFloorItems.Value || e.Opened) continue;
                    kind = MarkerKind.FloorItem;
                    break;
            }
            if (!_isExplored(e.Pos.x, e.Pos.z)) continue;
            into.Add(new MapMarker(e.Pos.x, e.Pos.y, e.Pos.z, kind));
        }
    }

    public void Reset()
    {
        _entries.Clear();
        _list.Clear();
        _scanQueue.Clear();
        _pollIndex = 0;
        _sceneCount = -1;
        _alivePawns.Clear();
        _pendingBodies.Clear();
    }

    private void QueueAll()
    {
        _scanQueue.Enqueue(Source.TableContainer);
        _scanQueue.Enqueue(Source.Container);
        _scanQueue.Enqueue(Source.Corpse);
        _scanQueue.Enqueue(Source.FloorItem);
    }

    private void Scan(Source source)
    {
        var type = source switch
        {
            Source.TableContainer => Il2CppType.Of<AG.Environment.Loot.LootTableContainer>(),
            Source.Container => Il2CppType.Of<AG.Environment.Loot.LootContainer>(),
            Source.Corpse => Il2CppType.Of<AG.Environment.Loot.PawnLootTableContainer>(),
            _ => Il2CppType.Of<AG.Environment.Loot.SingleLootItem>(),
        };
        var found = Object.FindObjectsByType(type, FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            var c = found[i]?.TryCast<Component>();
            if (c != null) Add(c, source);
        }
    }

    /// <summary>
    /// Checks a slice of the entries each frame: destroyed → gone; opened / no longer interactable →
    /// opened. At the end of each full pass, gone entries are compacted away.
    /// </summary>
    private void Poll()
    {
        int n = Math.Min(PollPerFrame, _list.Count);
        bool passDone = false;
        for (int k = 0; k < n; k++)
        {
            var e = _list[RoundRobin.Next(ref _pollIndex, _list.Count, out bool completed)];
            passDone |= completed;
            if (e.Gone) continue;
            try
            {
                if (e.Obj == null) { e.Gone = true; continue; }
                switch (e.Source)
                {
                    case Source.TableContainer:
                        var t = e.Obj.TryCast<AG.Environment.Loot.LootTableContainer>();
                        e.Opened = t == null || t.hasBeenOpened;
                        break;
                    default:
                        var it = e.Obj.TryCast<AG.Interactables.Interactable>();
                        e.Opened = it == null || !it.IsInteractable;
                        break;
                }
            }
            catch (Exception) { e.Gone = true; }
        }

        if (!passDone) return;
        _dead.Clear();
        foreach (var kv in _entries) if (kv.Value.Gone) _dead.Add(kv.Key);
        if (_dead.Count == 0) return;
        foreach (var k in _dead) _entries.Remove(k);
        _list.RemoveAll(x => x.Gone);
        _pollIndex = 0;
    }
}
