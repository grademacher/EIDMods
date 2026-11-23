using BepInEx.Logging;
using EIDMods.Features.MapBake;
using EIDMods.Features.Minimap;

namespace EIDMods.Features;

/// <summary>Entry point the loader instantiates on every (re)load.</summary>
public sealed class FeatureModule : IFeatureModule
{
    private MinimapFeature _minimap;
    private MapBaker _baker;

    public void Load(FeatureContext context)
    {
        Mod.Context = context;
        var cfg = new MinimapConfig(context.Config);
        _baker = new MapBaker(UnityEngine.Mathf.Clamp(cfg.CellSize.Value, 0.1f, 4f), () => cfg.BakeBudgetMs.Value);
        _minimap = new MinimapFeature(cfg);
        Mod.Log.LogInfo($"Features #{context.Generation} up (minimap, map baker).");
    }

    public void Unload()
    {
        _baker?.Dispose();
        _baker = null;
        _minimap?.Dispose();
        _minimap = null;
    }

    public void Update()
    {
        // F9: dump level data around the player to the log (see Diagnostics/LevelProbe).
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F9) && AG.Actors.Player.Local != null)
            Diagnostics.LevelProbe.Run(AG.Actors.Player.Local.transform);

        // F7: bake-decision probe (MapData art, mesh readability, level extent, classifier cost).
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F7) && AG.Actors.Player.Local != null)
            Diagnostics.BakeProbe.Run(AG.Actors.Player.Local.transform);

        _baker?.Update();
        _minimap?.Update();
    }
    public void LateUpdate() => _minimap?.LateUpdate();
    public void OnGUI() => _minimap?.OnGUI();
}

/// <summary>Static access to the current load's context for feature code.</summary>
internal static class Mod
{
    public static FeatureContext Context;
    public static ManualLogSource Log => Context.Log;
}
