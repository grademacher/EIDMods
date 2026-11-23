using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace EIDMods;

/// <summary>
/// Entry point of the hot-reloadable features assembly (EIDMods.Features.dll).
/// The loader finds the single public non-abstract class implementing this, creates it with
/// a parameterless constructor, and forwards Unity's per-frame calls to it.
///
/// Rules for implementations (so a reload leaves nothing behind):
///  - Never call ClassInjector.RegisterTypeInIl2Cpp. Injected types can't be unregistered or
///    re-registered, so the reloaded copy would collide. Use Update/LateUpdate/OnGUI here instead.
///  - Unload() must destroy every GameObject / Texture / RenderTexture it created.
///  - Harmony patches go through <see cref="FeatureContext.Harmony"/>; the loader unpatches it.
/// </summary>
public interface IFeatureModule
{
    void Load(FeatureContext context);
    void Unload();
    void Update();
    void LateUpdate();
    void OnGUI();
}

public sealed class FeatureContext
{
    public ManualLogSource Log { get; }
    public ConfigFile Config { get; }
    /// <summary>Persistent (DontDestroyOnLoad) GameObject owned by the loader. Don't destroy it.</summary>
    public GameObject Host { get; }
    /// <summary>Harmony instance unique to this load; the loader calls UnpatchSelf on unload.</summary>
    public HarmonyLib.Harmony Harmony { get; }
    /// <summary>Increments on every (re)load; handy for logging.</summary>
    public int Generation { get; }

    internal FeatureContext(ManualLogSource log, ConfigFile config, GameObject host, HarmonyLib.Harmony harmony, int generation)
    {
        Log = log; Config = config; Host = host; Harmony = harmony; Generation = generation;
    }
}
