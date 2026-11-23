using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace EIDMods;

/// <summary>
/// Loads EIDMods.Features.dll from bytes (so the file is never locked), and reloads it when the
/// file changes or the reload key is pressed.
///
/// Each load goes into its own AssemblyLoadContext; game/interop/BepInEx assemblies resolve from
/// the default context, so features see the same types as everything else. Old contexts are not
/// unloaded (IL2CPP interop makes collectible contexts unsafe), so each reload leaks the old
/// assembly. That's a few hundred KB, fine for development.
/// </summary>
internal static class FeatureHost
{
    private const string FeaturesFileName = "EIDMods.Features.dll";

    private static ManualLogSource _log;
    private static ConfigFile _config;
    private static GameObject _host;
    private static string _featuresPath;
    private static ConfigEntry<KeyCode> _reloadKey;
    private static ConfigEntry<bool> _autoReload;

    private static IFeatureModule _module;
    private static HarmonyLib.Harmony _harmony;
    private static int _generation;
    private static bool _faulted;

    private static FileSystemWatcher _watcher;
    private static volatile bool _fileChanged;
    private static DateTime _lastChangeUtc;
    private static float _retryAt = -1f;

    public static void Init(ManualLogSource log, ConfigFile config, GameObject host, string featuresDir)
    {
        _log = log;
        _config = config;
        _host = host;
        _featuresPath = Path.Combine(featuresDir, FeaturesFileName);
        _reloadKey = config.Bind("HotReload", "ReloadKey", KeyCode.F10, "Reload EIDMods.Features.dll.");
        _autoReload = config.Bind("HotReload", "AutoReload", true, "Reload automatically when EIDMods.Features.dll changes on disk.");

        Directory.CreateDirectory(featuresDir);
        _watcher = new FileSystemWatcher(featuresDir, FeaturesFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler onChange = (_, _) => { _lastChangeUtc = DateTime.UtcNow; _fileChanged = true; };
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => { _lastChangeUtc = DateTime.UtcNow; _fileChanged = true; };
        _watcher.EnableRaisingEvents = true;

        Reload("startup");
    }

    public static void Update()
    {
        if (Input.GetKeyDown(_reloadKey.Value)) Reload("hotkey");
        // Debounce: the build writes the dll (and pdb) in several steps.
        else if (_autoReload.Value && _fileChanged && (DateTime.UtcNow - _lastChangeUtc).TotalSeconds > 0.75)
        {
            _fileChanged = false;
            Reload("file changed");
        }
        else if (_retryAt > 0f && Time.unscaledTime >= _retryAt)
        {
            _retryAt = -1f;
            Reload("retry");
        }

        Call(m => m.Update(), "Update");
    }

    public static void LateUpdate() => Call(m => m.LateUpdate(), "LateUpdate");
    public static void OnGUI() => Call(m => m.OnGUI(), "OnGUI");

    private static void Reload(string reason)
    {
        if (!File.Exists(_featuresPath))
        {
            _log.LogWarning($"[HotReload] {_featuresPath} not found; build EIDMods.Features.");
            return;
        }

        byte[] dll, pdb = null;
        try
        {
            dll = ReadShared(_featuresPath);
            var pdbPath = Path.ChangeExtension(_featuresPath, ".pdb");
            if (File.Exists(pdbPath)) pdb = ReadShared(pdbPath);
        }
        catch (IOException e)
        {
            // Build still writing the file; try again shortly.
            _log.LogInfo($"[HotReload] features dll busy ({e.Message}); retrying.");
            _retryAt = Time.unscaledTime + 0.5f;
            return;
        }

        UnloadCurrent();

        _generation++;
        try
        {
            var alc = new AssemblyLoadContext($"EIDMods.Features#{_generation}", isCollectible: false);
            var asm = pdb != null
                ? alc.LoadFromStream(new MemoryStream(dll), new MemoryStream(pdb))
                : alc.LoadFromStream(new MemoryStream(dll));

            var type = asm.GetTypes().FirstOrDefault(t =>
                typeof(IFeatureModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (type == null)
            {
                _log.LogError($"[HotReload] No IFeatureModule implementation in {FeaturesFileName}.");
                return;
            }

            _harmony = new HarmonyLib.Harmony($"eidmods.features.{_generation}");
            var module = (IFeatureModule)Activator.CreateInstance(type);
            module.Load(new FeatureContext(_log, _config, _host, _harmony, _generation));
            _module = module;
            _faulted = false;
            _log.LogInfo($"[HotReload] Loaded features #{_generation} ({reason}), built {File.GetLastWriteTime(_featuresPath):HH:mm:ss}.");
        }
        catch (Exception e)
        {
            _log.LogError($"[HotReload] Failed to load features #{_generation}: {e}");
            UnloadCurrent();
        }
    }

    private static void UnloadCurrent()
    {
        if (_module != null)
        {
            try { _module.Unload(); }
            catch (Exception e) { _log.LogError($"[HotReload] Unload threw: {e}"); }
            _module = null;
        }
        if (_harmony != null)
        {
            try { _harmony.UnpatchSelf(); }
            catch (Exception e) { _log.LogError($"[HotReload] UnpatchSelf threw: {e}"); }
            _harmony = null;
        }
    }

    /// <summary>
    /// Calls into the features. On the first exception the module is paused (not unloaded) so a
    /// per-frame bug doesn't flood the log; the next reload un-pauses.
    /// </summary>
    private static void Call(Action<IFeatureModule> call, string what)
    {
        if (_module == null || _faulted) return;
        try { call(_module); }
        catch (Exception e)
        {
            _faulted = true;
            _log.LogError($"[HotReload] Features #{_generation} threw in {what}; paused until next reload (F10 or rebuild).\n{e}");
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
