using System;
using System.Collections.Generic;
using EIDMods.MapCore.Markers;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>
/// Extraction points: violet squares. Two kinds exist in the game (metadata, 2026-11-24):
///   ExitAreaComponent — a walk-in zone with a countdown; its State is on/off (faded while off)
///   Exitpoint         — an exit you interact with; skipped while IsHidden
/// Found with FindObjectsByType (10–15 ms per type) once per level and when scenes load, one type per
/// frame; their on/off state is re-read every second. Read-only.
/// </summary>
internal sealed class ExtractionMarkers : IMarkerProvider
{
    private const float SceneCheckInterval = 1f, StateInterval = 1f;

    private sealed class Exit
    {
        public AG.Environment.Components.ExitAreaComponent Area;
        public AG.Items.Exitpoint Point;
        public Vector3 Pos;
        public string Label;
        public bool Active = true, Hidden;
    }

    private readonly MarkerConfig _cfg;
    private readonly List<Exit> _exits = new();
    private readonly List<Exit> _found = new();
    private string _scenePath;
    private int _sceneCount = -1, _stage = -1;
    private float _nextSceneCheck, _nextState;

    public ExtractionMarkers(MarkerConfig cfg) => _cfg = cfg;

    public void Update(float now, Vector3 playerPos)
    {
        if (!_cfg.ShowExtraction.Value) { Reset(); return; }

        if (now >= _nextSceneCheck)
        {
            _nextSceneCheck = now + SceneCheckInterval;
            string path = SceneManager.GetActiveScene().path;
            int count = SceneManager.sceneCount;
            if (path != _scenePath) { _exits.Clear(); _scenePath = path; _sceneCount = -1; }
            if (count != _sceneCount) { _sceneCount = count; _found.Clear(); _stage = 0; }
        }

        if (_stage >= 0) { FindStep(); return; }

        if (now >= _nextState)
        {
            _nextState = now + StateInterval;
            for (int i = _exits.Count - 1; i >= 0; i--)
            {
                var e = _exits[i];
                try
                {
                    if (e.Area != null) e.Active = e.Area.State;
                    else if (e.Point != null) e.Hidden = e.Point.IsHidden;
                }
                catch (Exception) { _exits.RemoveAt(i); } // destroyed (scene unloaded)
            }
        }
    }

    public void Collect(float now, List<MapMarker> into)
    {
        foreach (var e in _exits)
        {
            if (e.Hidden) continue;
            into.Add(new MapMarker(e.Pos.x, e.Pos.y, e.Pos.z,
                e.Active ? MarkerKind.Extraction : MarkerKind.ExtractionInactive, e.Label));
        }
    }

    public void Reset()
    {
        _exits.Clear();
        _found.Clear();
        _stage = -1;
        _sceneCount = -1;
    }

    private void FindStep()
    {
        switch (_stage++)
        {
            case 0:
                foreach (var c in Find<AG.Environment.Components.ExitAreaComponent>())
                {
                    var col = c.GetComponentInChildren<Collider>();
                    _found.Add(new Exit
                    {
                        Area = c,
                        Pos = col != null ? col.bounds.center : c.transform.position,
                        Label = LabelFor(c.LocationData),
                        Active = c.State,
                    });
                }
                return;
            case 1:
                foreach (var c in Find<AG.Items.Exitpoint>())
                    _found.Add(new Exit { Point = c, Pos = c.transform.position, Label = LabelFor(c.locationData), Hidden = c.IsHidden });
                return;
        }
        _stage = -1;
        _exits.Clear();
        _exits.AddRange(_found);
        int on = 0;
        foreach (var e in _exits) if (e.Active && !e.Hidden) on++;
        Mod.Log.LogInfo($"[Exits] {_exits.Count} extraction point(s) in the loaded level, {on} open.");
    }

    private static string LabelFor(AG.Environment.Locations.LocationData loc)
    {
        string name = null;
        try { name = loc?.Name?.GetLocalizedString(); } catch (Exception) { }
        if (string.IsNullOrEmpty(name)) name = loc?.name;
        return string.IsNullOrEmpty(name) ? "Exit" : "Exit: " + name;
    }

    private static IEnumerable<T> Find<T>() where T : Component
    {
        var found = Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            var c = found[i]?.TryCast<T>();
            if (c != null) yield return c;
        }
    }
}
