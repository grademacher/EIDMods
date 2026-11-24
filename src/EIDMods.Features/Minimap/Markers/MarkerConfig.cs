using BepInEx.Configuration;
using UnityEngine;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>[Minimap.Markers] settings. Only primitive/Unity types (hot-reload rule: no enums from this DLL).</summary>
internal sealed class MarkerConfig
{
    public readonly ConfigEntry<bool> Enabled;
    public readonly ConfigEntry<KeyCode> ToggleKey;
    public readonly ConfigEntry<bool> ShowPlayers;
    public readonly ConfigEntry<bool> ShowEnemies;
    public readonly ConfigEntry<string> EnemyMode;
    public readonly ConfigEntry<float> EnemyRadius;
    public readonly ConfigEntry<bool> ShowContainers;
    public readonly ConfigEntry<bool> ShowCorpseLoot;
    public readonly ConfigEntry<bool> ShowFloorItems;
    public readonly ConfigEntry<bool> ShowOpenedLoot;
    public readonly ConfigEntry<bool> ShowQuests;
    public readonly ConfigEntry<string> OtherFloorMarkers;

    public MarkerConfig(ConfigFile cfg)
    {
        const string s = "Minimap.Markers";
        Enabled = cfg.Bind(s, "Enabled", true, "Show markers (players, enemies, loot) on the minimap.");
        ToggleKey = cfg.Bind(s, "ToggleKey", KeyCode.F3, "Key that shows/hides all markers.");
        ShowPlayers = cfg.Bind(s, "ShowPlayers", true, "Teammates: green dots, orange when downed, rim arrows when off the map.");
        ShowEnemies = cfg.Bind(s, "ShowEnemies", true, "Enemies: red dots.");
        EnemyMode = cfg.Bind(s, "EnemyMode", "near",
            new ConfigDescription("near: within EnemyRadius, through walls. lineOfSight: only enemies you could see. all: every enemy. off: none.",
                new AcceptableValueList<string>("near", "lineOfSight", "all", "off")));
        EnemyRadius = cfg.Bind(s, "EnemyRadius", 25f, "Metres around you that enemies are shown in (near / lineOfSight).");
        ShowContainers = cfg.Bind(s, "ShowContainers", true, "Chests, cupboards, crates, bags: yellow dots.");
        ShowCorpseLoot = cfg.Bind(s, "ShowCorpseLoot", true, "Lootable bodies: yellow dots while lootable.");
        ShowFloorItems = cfg.Bind(s, "ShowFloorItems", true, "Single items lying on the ground: small yellow dots.");
        ShowOpenedLoot = cfg.Bind(s, "ShowOpenedLoot", false, "Keep opened containers on the map (faded) instead of hiding them.");
        ShowQuests = cfg.Bind(s, "ShowQuests", true, "Active quest places: cyan diamonds (arrows at the edge when far away). Enemies a quest asks you to kill get a cyan ring.");
        OtherFloorMarkers = cfg.Bind(s, "OtherFloorMarkers", "fade",
            new ConfigDescription("Enemies more than 3 m above/below you: fade or hide. (Teammates always show; loot on other floors is always hidden.)", new AcceptableValueList<string>("fade", "hide")));
    }
}
