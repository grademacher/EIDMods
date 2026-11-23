using System;
using System.Collections.Generic;
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
/// be time-sliced, so instead of the plan's periodic rescan the objects are found once per level and
/// again when more scenes load, one type per frame. Corpse loot and floor items are also rescanned
/// a second after a nearby enemy dies. Opened/picked-up state is polled about 60 objects per frame.
/// Only explored cells show loot, unless the full map is revealed.
/// </summary>
internal sealed class LootMarkers : IMarkerProvider
{
    private const int PollPerFrame = 60;
    private const float SceneCheckInterval = 1f, KillRescanDelay = 1f;

    private enum Source : byte { TableContainer, Container, Corpse, FloorItem }

    private sealed class Entry
    {
        public Component Obj;
        public Source Source;
        public Vector3 Pos;
        public bool Opened, Gone;
    }

    private readonly MarkerConfig _cfg;
    private readonly EnemyMarkers _enemies;
    private readonly Func<float, float, bool> _isExplored;
    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private readonly List<Entry> _list = new();
    private readonly Queue<Source> _scanQueue = new();
    private readonly List<IntPtr> _dead = new();

    private string _scenePath;
    private int _sceneCount = -1, _kills, _pollIndex;
    private float _nextSceneCheck, _killRescanAt = -1f;

    /// <param name="isExplored">(worldX, worldZ) → whether that spot may show loot (explored, or full map).</param>
    public LootMarkers(MarkerConfig cfg, EnemyMarkers enemies, Func<float, float, bool> isExplored)
    {
        _cfg = cfg;
        _enemies = enemies;
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

        if (_enemies.Kills != _kills) { _kills = _enemies.Kills; _killRescanAt = now + KillRescanDelay; }
        if (_killRescanAt > 0f && now >= _killRescanAt)
        {
            _killRescanAt = -1f;
            _scanQueue.Enqueue(Source.Corpse);
            _scanQueue.Enqueue(Source.FloorItem);
        }

        if (_scanQueue.Count > 0) Scan(_scanQueue.Dequeue());
        Poll();
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
        _killRescanAt = -1f;
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
            if (c == null || _entries.ContainsKey(c.Pointer)) continue;
            var e = new Entry { Obj = c, Source = source, Pos = c.transform.position };
            _entries[c.Pointer] = e;
            _list.Add(e);
        }
    }

    /// <summary>Checks a slice of the entries each frame: destroyed → gone; opened / no longer interactable → opened.</summary>
    private void Poll()
    {
        int n = Math.Min(PollPerFrame, _list.Count);
        for (int k = 0; k < n; k++)
        {
            if (_pollIndex >= _list.Count) _pollIndex = 0;
            var e = _list[_pollIndex++];
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

        // Compact occasionally so destroyed objects don't accumulate.
        if (_pollIndex == 0 && _list.Count > 0)
        {
            _dead.Clear();
            foreach (var kv in _entries) if (kv.Value.Gone) _dead.Add(kv.Key);
            if (_dead.Count > 0)
            {
                foreach (var k in _dead) _entries.Remove(k);
                _list.RemoveAll(x => x.Gone);
            }
        }
    }
}
