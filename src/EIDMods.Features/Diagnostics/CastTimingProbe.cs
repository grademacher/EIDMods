using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EIDMods.Features.Diagnostics;

/// <summary>
/// F2 toggles: logs a millisecond timeline of a spell cast, to find where the "input delay" comes from.
///   key     — the magic / attack key going down (read by us, the moment Unity sees it)
///   state   — ConduitCombatState.OnMagic*/Add/AttemptCast/OnAttack/animation events (the game handling it)
///   cast    — CastSpellState.Enter/OnActivate/Exit (cast animation; OnActivate ≈ the spell releasing)
///   orbs    — MagicOrbController.Set/CmdSet/RpcSet/ApplyCombo (incantation orbs; Rpc = back from the host)
/// Harmony postfixes that only log: game behaviour is unchanged, and everything is unpatched on F2 or
/// on hot reload. Read the "[Cast]" lines in LogOutput.log.
/// </summary>
internal sealed class CastTimingProbe : IDisposable
{
    private static readonly (Type type, string[] methods)[] Targets =
    {
        (typeof(AG.StateMachine.Players.ConduitCombatState), new[]
        {
            "OnMagicUp", "OnMagicRight", "OnMagicDown", "OnMagicLeft", "Add", "AttemptCast", "OnAttack",
            "OnAnimationEventReceived", "ClearIncantation", "Cancel", "Enter", "Exit",
        }),
        (typeof(AG.StateMachine.Players.CastSpellState), new[]
        {
            "Enter", "OnActivate", "OnAnimatorEventReceived", "OnAnimatorStateExit", "Exit",
        }),
        (typeof(AG.Magic.MagicOrbController), new[] { "Set", "CmdSet", "RpcSet", "ApplyCombo" }),
    };

    private static readonly string[] WatchedKeys = { "MagicUp", "MagicRight", "MagicDown", "MagicLeft", "Attack" };
    private readonly List<(string action, KeyCode key)> _keys = new();

    private static double _t0 = -1, _last;

    private Harmony _harmony;
    public bool Enabled => _harmony != null;

    public void Toggle()
    {
        if (Enabled) { Dispose(); Mod.Log.LogInfo("[Cast] timing probe off"); return; }
        _harmony = new Harmony("eidmods.features.casttiming." + Mod.Context.Generation);
        var post = new HarmonyMethod(typeof(CastTimingProbe).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (var (type, names) in Targets)
            foreach (var name in names)
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name) continue;
                    try { _harmony.Patch(m, postfix: post); n++; }
                    catch (Exception e) { Mod.Log.LogWarning($"[Cast] couldn't hook {type.Name}.{name}: {e.Message}"); }
                }
        _t0 = -1;
        ReadKeys();
        string host = Mirror.NetworkServer.active ? "host" : "client";
        Mod.Log.LogInfo($"[Cast] timing probe on ({n} hooks, you are the {host}). Cast a few spells, then press F2 again.");
    }

    /// <summary>Call every frame: timestamps the keys themselves, before the game reacts.</summary>
    public void Update()
    {
        if (!Enabled) return;
        foreach (var (action, key) in _keys)
            if (Input.GetKeyDown(key)) Log($"key   {action} ({key}) down");
    }

    /// <summary>The game's current bindings (Keybinds.Get(InputAction)), via reflection: the enum's interop name isn't stable to reference.</summary>
    private void ReadKeys()
    {
        _keys.Clear();
        var get = typeof(AG.Core.Keybinds).GetMethod("Get", BindingFlags.Static | BindingFlags.Public);
        var enumType = get?.GetParameters()[0].ParameterType;
        if (enumType == null) return;
        foreach (var name in WatchedKeys)
        {
            try
            {
                var key = (KeyCode)get.Invoke(null, new[] { Enum.Parse(enumType, name) });
                if (key != KeyCode.None) _keys.Add((name, key));
            }
            catch (Exception) { }
        }
        Mod.Log.LogInfo($"[Cast] watching keys: {string.Join(", ", _keys.ConvertAll(k => $"{k.action}={k.key}"))}");
    }

    private static void Postfix(MethodBase __originalMethod, object __instance)
    {
        string extra = "";
        try
        {
            if (__instance is AG.StateMachine.Players.ConduitCombatState c)
                extra = $" (waitingForAnimation={c.isWaitingForAnimationToComplete}, queue={c.queue?.Count ?? -1}, cancelling={c.isCancelling})";
        }
        catch (Exception) { }
        string group = __originalMethod.DeclaringType == typeof(AG.Magic.MagicOrbController) ? "orbs " :
                       __originalMethod.DeclaringType == typeof(AG.StateMachine.Players.CastSpellState) ? "cast " : "state";
        Log($"{group} {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}{extra}");
    }

    private static void Log(string what)
    {
        double now = Time.realtimeSinceStartupAsDouble * 1000.0;
        if (_t0 < 0 || now - _last > 3000) { _t0 = now; Mod.Log.LogInfo("[Cast] ----- new sequence -----"); }
        _last = now;
        Mod.Log.LogInfo($"[Cast] +{(now - _t0).ToString("F1").PadLeft(7)} ms  frame {Time.frameCount}  {what}");
    }

    public void Dispose()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }
}
