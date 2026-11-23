using BepInEx.Logging;
using EIDMods.Features.Minimap;

namespace EIDMods.Features;

/// <summary>Entry point the loader instantiates on every (re)load.</summary>
public sealed class FeatureModule : IFeatureModule
{
    private MinimapFeature _minimap;

    public void Load(FeatureContext context)
    {
        Mod.Context = context;
        _minimap = new MinimapFeature(new MinimapConfig(context.Config));
        Mod.Log.LogInfo($"Features #{context.Generation} up (minimap).");
    }

    public void Unload()
    {
        _minimap?.Dispose();
        _minimap = null;
    }

    public void Update() => _minimap?.Update();
    public void LateUpdate() => _minimap?.LateUpdate();
    public void OnGUI() => _minimap?.OnGUI();
}

/// <summary>Static access to the current load's context for feature code.</summary>
internal static class Mod
{
    public static FeatureContext Context;
    public static ManualLogSource Log => Context.Log;
}
