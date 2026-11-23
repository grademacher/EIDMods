using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EIDMods.Features.MapBake;

/// <summary>Identifies the current map + game build, for the bake cache file name.</summary>
internal static class MapKey
{
    /// <summary>
    /// Finds the AG.Levels.MapData whose ScenePath is the active scene and builds
    /// "&lt;MapID&gt;_&lt;Application.version&gt;_&lt;Application.buildGUID&gt;" (filename-safe).
    /// </summary>
    public static bool TryGetCurrent(out string key, out string mapName)
    {
        key = null;
        mapName = null;
        string scenePath = SceneManager.GetActiveScene().path;
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AG.Levels.MapData>());
        for (int i = 0; i < all.Length; i++)
        {
            var md = all[i].Cast<AG.Levels.MapData>();
            if (md.ScenePath != scenePath) continue;
            mapName = md.name;
            key = Sanitize($"{md.map}_{Application.version}_{Application.buildGUID}");
            return true;
        }
        return false;
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => bad.Contains(ch) || ch == ' ' || ch == ':' ? '_' : ch).ToArray());
    }
}
