using BepInEx.Logging;
using EIDMods.Features.MapBake;
using EIDMods.Features.Minimap;

namespace EIDMods.Features;

/// <summary>Entry point the loader instantiates on every (re)load.</summary>
public sealed class FeatureModule : IFeatureModule
{
    private MinimapFeature _minimap;
    private MapBaker _baker;
    private Diagnostics.CastTimingProbe _castProbe;

    public void Load(FeatureContext context)
    {
        Mod.Context = context;
        // Pick up edits to the .cfg file on every hot reload (BepInEx only reads it at startup).
        context.Config.Reload();
        var cfg = new MinimapConfig(context.Config);
        _baker = new MapBaker(UnityEngine.Mathf.Clamp(cfg.CellSize.Value, 0.1f, 4f), () => cfg.BakeBudgetMs.Value);
        _castProbe = new Diagnostics.CastTimingProbe();
        _minimap = new MinimapFeature(cfg, new Minimap.Markers.MarkerConfig(context.Config), _baker);
        Mod.Log.LogInfo($"Features #{context.Generation} up (minimap, map baker).");
    }

    public void Unload()
    {
        _castProbe?.Dispose(); // unpatch before this assembly goes away
        _castProbe = null;
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

        // F2: spell-cast timing probe (logs only; see Diagnostics/CastTimingProbe).
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F2)) _castProbe?.Toggle();
        _castProbe?.Update();

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
