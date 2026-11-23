using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using EIDMods.Minimap;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace EIDMods;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "eidmods.core";
    public const string Name = "EIDMods";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;
    internal static MinimapConfig MinimapConfig;

    public override void Load()
    {
        Logger = Log;
        MinimapConfig = new MinimapConfig(Config);

        // Managed MonoBehaviours must be registered with IL2CPP before they can be added to GameObjects.
        ClassInjector.RegisterTypeInIl2Cpp<MinimapController>();

        var host = new GameObject("EIDMods_Host");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<MinimapController>();

        Logger.LogInfo($"{Name} {Version} loaded.");
    }
}
