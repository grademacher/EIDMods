using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace EIDMods.Features.Spells;

/// <summary>
/// Incantation inputs (the magic direction keys) count the moment you press them.
///
/// The game's ConduitCombatState ignores a direction press while its previous incantation hand
/// animation is still playing (isWaitingForAnimationToComplete, ~0.6–0.9 s per input): the F2 timing
/// probe (2026-11-24) showed 3 of 9 presses counting. A Harmony prefix clears that flag right before
/// each OnMagicUp/Right/Down/Left, so the game accepts the press and starts the hand animation again
/// (it may restart or overlap; other players may see that, which the user accepted).
///
/// Unchanged: the cast itself (CastSpellState, the ~1 s wind-up after Attack) and anything about other
/// players. Unpatched when switched off or on hot reload.
/// </summary>
internal sealed class InstantIncantations : IDisposable
{
    private static readonly string[] Inputs = { "OnMagicUp", "OnMagicRight", "OnMagicDown", "OnMagicLeft" };

    private readonly ConfigEntry<bool> _enabled;
    private Harmony _harmony;

    public InstantIncantations(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Spells", "InstantIncantations", true,
            "Magic direction inputs count immediately instead of being ignored while the previous incantation " +
            "hand animation plays. The cast animation after Attack is unchanged.");
        Apply();
    }

    /// <summary>Call every frame: follows the setting if it's changed in the config file and reloaded.</summary>
    public void Update()
    {
        if (_enabled.Value != (_harmony != null)) Apply();
    }

    private void Apply()
    {
        if (!_enabled.Value) { Dispose(); return; }
        if (_harmony != null) return;
        _harmony = new Harmony("eidmods.features.instantincantations." + Mod.Context.Generation);
        var prefix = new HarmonyMethod(typeof(InstantIncantations).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (var m in typeof(AG.StateMachine.Players.ConduitCombatState).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (Array.IndexOf(Inputs, m.Name) < 0) continue;
            try { _harmony.Patch(m, prefix: prefix); n++; }
            catch (Exception e) { Mod.Log.LogWarning($"[Spells] couldn't hook {m.Name}: {e.Message}"); }
        }
        Mod.Log.LogInfo($"[Spells] instant incantations on ({n}/4 inputs hooked).");
    }

    private static void Prefix(AG.StateMachine.Players.ConduitCombatState __instance)
    {
        try
        {
            if (__instance.isWaitingForAnimationToComplete) __instance.isWaitingForAnimationToComplete = false;
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }
}
