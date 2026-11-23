using BepInEx.Configuration;
using UnityEngine;

namespace EIDMods.Minimap;

internal class MinimapConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyCode> ToggleKey;
    public readonly ConfigEntry<KeyCode> ZoomInKey;
    public readonly ConfigEntry<KeyCode> ZoomOutKey;
    public readonly ConfigEntry<int> SizePx;
    public readonly ConfigEntry<float> ViewRadius;
    public readonly ConfigEntry<float> HeightAbovePlayer;
    public readonly ConfigEntry<bool> RotateWithPlayer;

    public MinimapConfig(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Minimap", "Enabled", true, "Show the minimap.");
        ToggleKey = cfg.Bind("Minimap", "ToggleKey", KeyCode.M, "Key that shows/hides the minimap.");
        ZoomInKey = cfg.Bind("Minimap", "ZoomInKey", KeyCode.Equals, "Zoom the minimap in.");
        ZoomOutKey = cfg.Bind("Minimap", "ZoomOutKey", KeyCode.Minus, "Zoom the minimap out.");
        SizePx = cfg.Bind("Minimap", "SizePx", 256, "On-screen size of the minimap in pixels.");
        ViewRadius = cfg.Bind("Minimap", "ViewRadius", 20f, "World units visible from center to edge.");
        HeightAbovePlayer = cfg.Bind("Minimap", "HeightAbovePlayer", 12f,
            "How far above the player the map camera sits. Keep it low enough to stay under dungeon ceilings' far side.");
        RotateWithPlayer = cfg.Bind("Minimap", "RotateWithPlayer", true, "Rotate the map so 'up' is where you face.");
    }
}
