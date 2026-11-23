using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace EIDMods;

/// <summary>
/// Loader plugin. Keep this small and stable: changes here need a game restart.
/// Feature code lives in EIDMods.Features and is hot-reloaded by <see cref="FeatureHost"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "eidmods.core";
    public const string Name = "EIDMods";
    public const string Version = "0.2.0";

    public override void Load()
    {
        ClassInjector.RegisterTypeInIl2Cpp<HostBehaviour>();

        var host = new GameObject("EIDMods_Host");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<HostBehaviour>();

        Log.LogInfo($"{Name} loader {Version} loaded.");
        FeatureHost.Init(Log, Config, host, Path.Combine(Paths.BepInExRootPath, "EIDMods"));
    }
}
