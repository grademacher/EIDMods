using System;
using System.Collections.Generic;
using System.Text;
using EIDMods.MapCore.Markers;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>Quest data other providers need: the enemy types an active "kill" objective asks for.</summary>
internal sealed class QuestTargets
{
    public readonly HashSet<IntPtr> KillTemplates = new();
}

/// <summary>
/// Active quest objectives from the local player's own save (AG.Inventories.Stash.History), so it works
/// the same for host and clients and never touches shared game state. Objectives that are places get a
/// cyan diamond with the objective text as label:
///   EnterLocation      → LocationTrigger whose locationData matches (collider centre)
///   InteractWithTarget → ObjectiveTarget with the same ID
///   ExitThrough        → ExitAreaComponent / Exitpoint whose locationData is one of the targets
/// KillEnemy objectives fill <see cref="QuestTargets.KillTemplates"/> for the enemy rings. Item
/// objectives (acquire, hand over, craft) have no place and get nothing (F4 probe, 2026-11-23).
///
/// Cost: the objective list is re-read every 2 s (a handful of objects). World objects are found with
/// FindObjectsByType (10–15 ms per type), only when the set of place objectives changes or scenes
/// load, one type per frame.
/// </summary>
internal sealed class QuestMarkers : IMarkerProvider
{
    private const float RefreshInterval = 2f, SceneCheckInterval = 1f;

    private enum Goal { Enter, Interact, Exit }

    private sealed class Objective
    {
        public Goal Goal;
        public string Label;
        public readonly List<IntPtr> LocationPtrs = new();
        public readonly List<string> LocationNames = new();
        public string TargetId;
        public readonly List<Vector3> Positions = new();
    }

    private readonly MarkerConfig _cfg;
    private readonly QuestTargets _targets;
    private readonly List<Objective> _objectives = new();
    private readonly StringBuilder _sig = new();
    private string _signature = "";
    private string _scenePath;
    private int _sceneCount = -1;
    private float _nextRefresh, _nextSceneCheck;

    // Staged resolve: one FindObjectsByType per frame.
    private int _resolveStage = -1;
    private readonly List<(IntPtr loc, string name, Vector3 pos)> _triggers = new(), _exits = new();
    private readonly List<(string id, Vector3 pos)> _objectiveTargets = new();

    public QuestMarkers(MarkerConfig cfg, QuestTargets targets)
    {
        _cfg = cfg;
        _targets = targets;
    }

    public void Update(float now, Vector3 playerPos)
    {
        if (!_cfg.ShowQuests.Value) { Clear(); return; }

        if (now >= _nextSceneCheck)
        {
            _nextSceneCheck = now + SceneCheckInterval;
            string path = SceneManager.GetActiveScene().path;
            int count = SceneManager.sceneCount;
            if (path != _scenePath) { Clear(); _scenePath = path; _nextRefresh = 0f; }
            if (count != _sceneCount) { _sceneCount = count; StartResolve(); }
        }

        if (now >= _nextRefresh)
        {
            _nextRefresh = now + RefreshInterval;
            ReadObjectives();
        }

        if (_resolveStage >= 0) ResolveStep();
    }

    public void Collect(float now, List<MapMarker> into)
    {
        foreach (var o in _objectives)
            foreach (var p in o.Positions)
                into.Add(new MapMarker(p.x, p.y, p.z, MarkerKind.Quest, o.Label));
    }

    public void Reset() => Clear();

    private void Clear()
    {
        _objectives.Clear();
        _targets.KillTemplates.Clear();
        _signature = "";
        _resolveStage = -1;
    }

    /// <summary>Re-reads active objectives; starts a world lookup when the set of place objectives changed.</summary>
    private void ReadObjectives()
    {
        var history = AG.Inventories.Stash.History;
        if (history == null) return;
        var active = new Il2CppSystem.Collections.Generic.List<AG.Tasks.Task>(history.GetActive());
        string scenePath = _scenePath ?? SceneManager.GetActiveScene().path;

        var fresh = new List<Objective>();
        _sig.Clear();
        _targets.KillTemplates.Clear();
        for (int t = 0; t < active.Count; t++)
        {
            var objs = active[t]?.Objectives;
            if (objs == null) continue;
            for (int i = 0; i < objs.Length; i++)
            {
                var obj = objs[i];
                if (obj == null || obj.CurrentState.ToString() == "Complete" || !OnThisMap(obj, scenePath)) continue;

                var kill = obj.TryCast<AG.Tasks.Objectives.KillEnemy>();
                if (kill != null)
                {
                    var template = kill.Target;
                    if (template != null) _targets.KillTemplates.Add(template.Pointer);
                    continue;
                }

                var o = ToPlace(obj);
                if (o == null) continue;
                o.Label = SafeSummary(obj);
                fresh.Add(o);
                _sig.Append(obj.Pointer.ToInt64()).Append(';');
            }
        }

        string sig = _sig.ToString();
        if (sig == _signature)
        {
            // Same places: keep resolved positions, refresh labels only.
            for (int i = 0; i < fresh.Count && i < _objectives.Count; i++) _objectives[i].Label = fresh[i].Label;
            return;
        }
        _signature = sig;
        _objectives.Clear();
        _objectives.AddRange(fresh);
        StartResolve();
    }

    private static Objective ToPlace(AG.Tasks.Objectives.Objective obj)
    {
        var enter = obj.TryCast<AG.Tasks.Objectives.EnterLocation>();
        if (enter != null)
        {
            var loc = enter.LocationData;
            if (loc == null) return null;
            var o = new Objective { Goal = Goal.Enter };
            o.LocationPtrs.Add(loc.Pointer);
            o.LocationNames.Add(loc.name);
            return o;
        }
        var interact = obj.TryCast<AG.Tasks.Objectives.InteractWithTarget>();
        if (interact != null)
        {
            string id = interact.Reference.ID;
            return string.IsNullOrEmpty(id) ? null : new Objective { Goal = Goal.Interact, TargetId = id };
        }
        var exit = obj.TryCast<AG.Tasks.Objectives.ExitThrough>();
        if (exit != null)
        {
            var locs = exit.locations;
            if (locs == null || locs.Length == 0) return null;
            var o = new Objective { Goal = Goal.Exit };
            for (int i = 0; i < locs.Length; i++)
            {
                if (locs[i] == null) continue;
                o.LocationPtrs.Add(locs[i].Pointer);
                o.LocationNames.Add(locs[i].name);
            }
            return o;
        }
        return null;
    }

    private static bool OnThisMap(AG.Tasks.Objectives.Objective obj, string scenePath)
    {
        var req = obj.MapRequirements;
        if (req == null || req.Length == 0) return true;
        for (int i = 0; i < req.Length; i++)
            if (req[i] != null && req[i].ScenePath == scenePath) return true;
        return false;
    }

    private static string SafeSummary(AG.Tasks.Objectives.Objective obj)
    {
        try { return obj.GetSummary(); } catch (Exception) { return null; }
    }

    private void StartResolve()
    {
        if (_objectives.Count == 0) { _resolveStage = -1; return; }
        _triggers.Clear();
        _exits.Clear();
        _objectiveTargets.Clear();
        _resolveStage = 0;
    }

    /// <summary>One FindObjectsByType per frame (each costs 10–15 ms), then match objectives to positions.</summary>
    private void ResolveStep()
    {
        bool needEnter = false, needInteract = false, needExit = false;
        foreach (var o in _objectives)
        {
            needEnter |= o.Goal == Goal.Enter;
            needInteract |= o.Goal == Goal.Interact;
            needExit |= o.Goal == Goal.Exit;
        }

        switch (_resolveStage++)
        {
            case 0:
                if (needEnter)
                    foreach (var c in Find<AG.Environment.Locations.LocationTrigger>())
                    {
                        var loc = c.locationData;
                        if (loc == null) continue;
                        var col = c.GetComponent<Collider>();
                        _triggers.Add((loc.Pointer, loc.name, col != null ? col.bounds.center : c.transform.position));
                    }
                return;
            case 1:
                if (needInteract)
                    foreach (var c in Find<AG.Tasks.Objectives.ObjectiveTarget>())
                        _objectiveTargets.Add((c.ID, c.transform.position));
                return;
            case 2:
                if (needExit)
                    foreach (var c in Find<AG.Environment.Components.ExitAreaComponent>())
                    {
                        var loc = c.LocationData;
                        if (loc != null) _exits.Add((loc.Pointer, loc.name, c.transform.position));
                    }
                return;
            case 3:
                if (needExit)
                    foreach (var c in Find<AG.Items.Exitpoint>())
                    {
                        var loc = c.locationData;
                        if (loc != null) _exits.Add((loc.Pointer, loc.name, c.transform.position));
                    }
                return;
        }

        _resolveStage = -1;
        int placed = 0;
        foreach (var o in _objectives)
        {
            o.Positions.Clear();
            switch (o.Goal)
            {
                case Goal.Enter: Match(o, _triggers); break;
                case Goal.Exit: Match(o, _exits); break;
                case Goal.Interact:
                    foreach (var (id, pos) in _objectiveTargets)
                        if (id == o.TargetId) o.Positions.Add(pos);
                    break;
            }
            if (o.Positions.Count > 0) placed++;
        }
        Mod.Log.LogInfo($"[Quests] {_objectives.Count} place objective(s) active here, {placed} located in the loaded level; {_targets.KillTemplates.Count} enemy type(s) to kill.");
    }

    /// <summary>Same LocationData object, or the same asset name (in case the save holds a different instance).</summary>
    private static void Match(Objective o, List<(IntPtr loc, string name, Vector3 pos)> candidates)
    {
        foreach (var (loc, name, pos) in candidates)
            for (int i = 0; i < o.LocationPtrs.Count; i++)
                if (loc == o.LocationPtrs[i] || name == o.LocationNames[i]) { o.Positions.Add(pos); break; }
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
