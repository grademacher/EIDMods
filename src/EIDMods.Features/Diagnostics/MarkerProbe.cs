using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// F4: what the marker providers would see. Actors split into players and enemies, plus the
/// downed check, enemy components (to find a faction/hostility path) and loot objects with their
/// opened state and scan times. Read-only.
/// </summary>
internal static class MarkerProbe
{
    public static void Run(Vector3 me)
    {
        var log = Mod.Log;
        log.LogInfo($"[Markers] ===== marker probe at {me} =====");
        Try("actors", () => Actors(me));
        Try("players", () => Players(me));
        Try("loot", () => Loot(me));
        Try("nearby loot components", () => NearbyLootComponents(me));
        Try("quests", () => Quests(me));
        Mod.Log.LogInfo($"[Markers] cursor lockState={Cursor.lockState} visible={Cursor.visible}");
        log.LogInfo("[Markers] ===== end marker probe =====");
    }

    private static void Actors(Vector3 me)
    {
        var all = AG.Actors.Actor.All;
        var byType = new Dictionary<string, int>();
        var pawns = new List<(float dist, AG.Enemies.Pawn pawn)>();
        int n = all.Count;
        for (int i = 0; i < n; i++)
        {
            var a = all[i];
            if (a == null) continue;
            string t = a.GetIl2CppType().Name;
            byType[t] = byType.TryGetValue(t, out var c) ? c + 1 : 1;
            var p = a.TryCast<AG.Enemies.Pawn>();
            if (p != null) pawns.Add((Vector3.Distance(me, p.transform.position), p));
        }
        var sb = new StringBuilder();
        foreach (var kv in byType) sb.Append($"{kv.Key}={kv.Value} ");
        Mod.Log.LogInfo($"[Markers] Actor.All: {n} ({sb})");

        pawns.Sort((a, b) => a.dist.CompareTo(b.dist));
        for (int i = 0; i < pawns.Count && i < 10; i++)
        {
            var (d, p) = pawns[i];
            Mod.Log.LogInfo($"[Markers]   pawn '{p.name}' type={p.GetIl2CppType().Name} alive={p.IsAlive} dist={d:F1} dy={p.transform.position.y - me.y:F1} layer={LayerMask.LayerToName(p.gameObject.layer)}");
        }
        if (pawns.Count > 0)
        {
            // Every component on the nearest pawn: looking for a faction/hostility path.
            var comps = pawns[0].pawn.GetComponents<Component>();
            var names = new StringBuilder();
            for (int i = 0; i < comps.Length; i++) names.Append(comps[i].GetIl2CppType().FullName).Append(", ");
            Mod.Log.LogInfo($"[Markers]   nearest pawn components: {names}");
        }
    }

    private static void Players(Vector3 me)
    {
        var players = AG.Actors.Player.Players;
        var local = AG.Actors.Player.Local;
        int n = players.Count;
        Mod.Log.LogInfo($"[Markers] Player.Players: {n}");
        for (int i = 0; i < n; i++)
        {
            var p = players[i];
            if (p == null) continue;
            string state = "?";
            try
            {
                var current = p.stateMachineComponent?.StateMachine?.Current;
                state = current == null ? "null" : current.GetIl2CppType().Name;
            }
            catch (Exception e) { state = "error: " + e.Message; }
            bool downed = state == "DownedState";
            Mod.Log.LogInfo($"[Markers]   player '{p.DisplayName}' local={(local != null && p.Pointer == local.Pointer)} alive={p.IsAlive} " +
                            $"aliveAndConnected={p.IsAliveAndConnected} state={state} downed={downed} dist={Vector3.Distance(me, p.transform.position):F1}");
        }
    }

    private static void Loot(Vector3 me)
    {
        Scan<AG.Environment.Loot.LootTableContainer>(me, c => $"hasBeenOpened={c.hasBeenOpened} interactable={c.IsInteractable}");
        Scan<AG.Environment.Loot.LootContainer>(me, c => $"interactable={c.IsInteractable}");
        Scan<AG.Environment.Loot.PawnLootTableContainer>(me, c => $"interactable={c.IsInteractable}");
        Scan<AG.Environment.Loot.SingleLootItem>(me, c => $"interactable={c.IsInteractable}");
    }

    /// <summary>
    /// Everything loot/interaction-related on objects within 3 m (any layer, triggers included), to
    /// find where a corpse's loot really lives.
    /// </summary>
    private static void NearbyLootComponents(Vector3 me)
    {
        var cols = Physics.OverlapSphere(me, 3f, ~0, QueryTriggerInteraction.Collide);
        var seen = new HashSet<IntPtr>();
        Mod.Log.LogInfo($"[Markers] colliders within 3 m: {cols.Length}");
        for (int i = 0; i < cols.Length; i++)
        {
            var col = cols[i];
            if (col == null) continue;
            var root = col.transform.root;
            if (!seen.Add(root.gameObject.Pointer)) continue;
            var comps = root.GetComponentsInChildren<Component>(true);
            var hits = new StringBuilder();
            for (int c = 0; c < comps.Length; c++)
            {
                if (comps[c] == null) continue;
                string n = comps[c].GetIl2CppType().FullName;
                if (n.Contains("Loot") || n.Contains("Interact") || n.Contains("Inventor") || n.Contains("Ragdoll") || n.Contains("Corpse"))
                    hits.Append($"{n} on '{comps[c].gameObject.name}' active={comps[c].gameObject.activeInHierarchy}; ");
            }
            if (hits.Length > 0)
                Mod.Log.LogInfo($"[Markers]   root '{root.name}' ({LayerMask.LayerToName(root.gameObject.layer)}, dist {Vector3.Distance(me, root.position):F1}): {hits}");
        }
    }

    /// <summary>Active quests (local save), each objective's type/state/maps, and the world objects that could locate it.</summary>
    private static void Quests(Vector3 me)
    {
        var history = AG.Inventories.Stash.History;
        Mod.Log.LogInfo($"[Markers] quest history: {(history == null ? "null" : $"active={history.ActiveTaskCount}")}");
        if (history == null) return;
        var active = history.GetActive();
        var list = new Il2CppSystem.Collections.Generic.List<AG.Tasks.Task>(active);
        Mod.Log.LogInfo($"[Markers] GetActive: {list.Count}");
        for (int t = 0; t < list.Count; t++)
        {
            var task = list[t];
            if (task == null) continue;
            string name = Safe(() => task.Name?.GetLocalizedString());
            var objs = task.Objectives;
            Mod.Log.LogInfo($"[Markers]   task '{task.name}' name='{name}' objectives={(objs == null ? -1 : objs.Length)}");
            if (objs == null) continue;
            for (int o = 0; o < objs.Length; o++)
            {
                var obj = objs[o];
                if (obj == null) continue;
                string maps = Safe(() =>
                {
                    var req = obj.MapRequirements;
                    if (req == null || req.Length == 0) return "any";
                    var sb = new StringBuilder();
                    for (int m = 0; m < req.Length; m++) sb.Append(req[m]?.name).Append(' ');
                    return sb.ToString();
                });
                string detail = Safe(() => Describe(obj));
                Mod.Log.LogInfo($"[Markers]     obj {o} {obj.GetIl2CppType().Name} state={Safe(() => obj.CurrentState.ToString())} maps=[{maps}] summary='{Safe(() => obj.GetSummary())}' {detail}");
            }
        }

        Scan<AG.Environment.Locations.LocationTrigger>(me, c => $"id={c.ID} location={c.locationData?.name} bounds={Safe(() => c.GetComponent<Collider>()?.bounds.ToString())}");
        Scan<AG.Tasks.Objectives.ObjectiveTarget>(me, c => $"id={c.ID} active={c.gameObject.activeInHierarchy}");
        Scan<AG.Environment.Components.ExitAreaComponent>(me, c => $"location={c.LocationData?.name}");
        Scan<AG.Items.Exitpoint>(me, c => $"location={c.locationData?.name}");
    }

    private static string Describe(AG.Tasks.Objectives.Objective obj)
    {
        var enter = obj.TryCast<AG.Tasks.Objectives.EnterLocation>();
        if (enter != null) return $"location={enter.LocationData?.name}";
        var interact = obj.TryCast<AG.Tasks.Objectives.InteractWithTarget>();
        if (interact != null) { var r = interact.Reference; return $"targetId={r.ID} scene={r.ScenePath}"; }
        var exit = obj.TryCast<AG.Tasks.Objectives.ExitThrough>();
        if (exit != null)
        {
            var sb = new StringBuilder("exits=");
            var targets = exit.locations;
            if (targets != null) for (int i = 0; i < targets.Length; i++) sb.Append(targets[i]?.name).Append(' ');
            return sb.ToString();
        }
        var kill = obj.TryCast<AG.Tasks.Objectives.KillEnemy>();
        if (kill != null) return $"enemy={kill.Target?.name} {kill.CurrentCount}/{kill.RequiredKillCount}";
        var critter = obj.TryCast<AG.Tasks.Objectives.KillCritter>();
        if (critter != null) return "critter";
        return "";
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception e) { return "err:" + e.GetType().Name; }
    }

    private static void Scan<T>(Vector3 me, Func<T, string> describe) where T : Component
    {
        var sw = Stopwatch.StartNew();
        var found = Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsSortMode.None);
        double ms = sw.Elapsed.TotalMilliseconds;
        var items = new List<(float d, T c)>();
        for (int i = 0; i < found.Length; i++)
        {
            var c = found[i].TryCast<T>();
            if (c != null) items.Add((Vector3.Distance(me, c.transform.position), c));
        }
        items.Sort((a, b) => a.d.CompareTo(b.d));
        Mod.Log.LogInfo($"[Markers] {typeof(T).Name}: {items.Count} found in {ms:F1} ms");
        for (int i = 0; i < items.Count && i < 5; i++)
        {
            var (d, c) = items[i];
            string extra;
            try { extra = describe(c); } catch (Exception e) { extra = "error: " + e.Message; }
            Mod.Log.LogInfo($"[Markers]   '{c.name}' dist={d:F1} dy={c.transform.position.y - me.y:F1} {extra}");
        }
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Mod.Log.LogWarning($"[Markers] {what} failed: {e.GetType().Name}: {e.Message}"); }
    }
}
