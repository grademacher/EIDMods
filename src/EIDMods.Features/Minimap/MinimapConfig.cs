using BepInEx.Configuration;
using UnityEngine;

namespace EIDMods.Features.Minimap;

internal class MinimapConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyCode> ToggleKey;
    public readonly ConfigEntry<KeyCode> BigMapKey;
    public readonly ConfigEntry<KeyCode> ZoomInKey;
    public readonly ConfigEntry<KeyCode> ZoomOutKey;
    public readonly ConfigEntry<int> SizePx;
    public readonly ConfigEntry<float> ViewRadius;
    public readonly ConfigEntry<bool> RotateWithPlayer;
    public readonly ConfigEntry<float> CellSize;
    public readonly ConfigEntry<float> SampleRadius;
    public readonly ConfigEntry<int> SampleBudget;
    public readonly ConfigEntry<float> RefreshSeconds;
    public readonly ConfigEntry<float> BakeBudgetMs;
    public readonly ConfigEntry<bool> RevealFullMap;
    public readonly ConfigEntry<float> RevealRadius;

    public MinimapConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Minimap", "Enabled", true, "Show the minimap.");
        ToggleKey = cfg.Bind("Minimap", "ToggleKey", KeyCode.N, "Key that shows/hides the corner minimap.");
        BigMapKey = cfg.Bind("Minimap", "BigMapKey", KeyCode.M, "Key that opens/closes the big map (Escape also closes it).");
        // One-time move of the minimap toggle from M to N, now that M opens the big map (user, 2026-11-23).
        var migrated = cfg.Bind("Minimap", "KeysMigrated", false, "Internal: the minimap toggle was moved from M to N.");
        if (!migrated.Value)
        {
            if (ToggleKey.Value == KeyCode.M) ToggleKey.Value = KeyCode.N;
            migrated.Value = true;
        }
        ZoomInKey = cfg.Bind("Minimap", "ZoomInKey", KeyCode.Equals, "Zoom the minimap in.");
        ZoomOutKey = cfg.Bind("Minimap", "ZoomOutKey", KeyCode.Minus, "Zoom the minimap out.");
        SizePx = cfg.Bind("Minimap", "SizePx", 256, "On-screen size of the minimap in pixels.");
        ViewRadius = cfg.Bind("Minimap", "ViewRadius", 20f, "World units (metres) visible from center to edge.");
        RotateWithPlayer = cfg.Bind("Minimap", "RotateWithPlayer", true, "Rotate the map so 'up' is where the camera looks.");

        CellSize = cfg.Bind("Minimap.Sampling", "CellSize", 0.5f, "Map resolution in metres per cell. Smaller = sharper but more physics queries.");
        SampleRadius = cfg.Bind("Minimap.Sampling", "SampleRadius", 30f, "How far around the player (metres) the level is scanned.");
        SampleBudget = cfg.Bind("Minimap.Sampling", "SampleBudget", 300, "Max cells scanned per frame (each is 1-2 physics queries).");
        RefreshSeconds = cfg.Bind("Minimap.Sampling", "RefreshSeconds", 4f, "Re-scan cells older than this, to pick up doors and moved objects.");

        BakeBudgetMs = cfg.Bind("Minimap.Bake", "BakeBudgetMs", 4f, "Main-thread milliseconds per frame spent collecting level geometry while baking a map.");
        // NOTE: config entry types must come from outside the hot-reloaded features DLL. An enum
        // declared here is a new type on every reload, while BepInEx keeps the first binding, so
        // the second Bind throws InvalidCastException.
        RevealFullMap = cfg.Bind("Minimap.Bake", "RevealFullMap", false, "false: only show where you've been. true: show the whole baked level.");
        RevealRadius = cfg.Bind("Minimap.Bake", "RevealRadius", 25f, "Metres around the player that count as explored.");
    }
}
