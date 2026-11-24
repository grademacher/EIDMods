using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EIDMods.Features.Minimap.Markers;
using EIDMods.MapCore.Markers;
using EIDMods.Features.MapBake;
using EIDMods.MapCore;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EIDMods.Features.Minimap;

/// <summary>
/// Minimap drawn from level collision data (see <see cref="MapGrid"/>), not from a camera, so the
/// game's darkness doesn't matter. Each frame a round, camera-aligned image is composed on the CPU
/// from the grid and uploaded to a Texture2D that IMGUI draws in the corner.
///
/// Plain class (not a MonoBehaviour) so it can be hot-reloaded: the loader forwards
/// Update/LateUpdate/OnGUI, and Dispose() removes everything it created.
/// </summary>
internal sealed class MinimapFeature : IDisposable
{
    private const int TexSize = 256;
    private static readonly Color32 Background = new(14, 13, 12, 200);
    private static readonly Color32 Rim = new(150, 140, 120, 255);
    private static readonly Color32 Marker = new(255, 255, 255, 255);
    private static readonly Color32 Clear = new(0, 0, 0, 0);

    private const float OverlayRadius = 10f, OverlayRefreshSeconds = 2f, ViewBudgetMs = 2f;
    private const int OverlayBudget = 20, PerfFrames = 300;

    private readonly MinimapConfig _cfg;
    private readonly BigMap _bigMap;
    private readonly MapBaker _baker;
    private readonly ReferenceHeight _ref = new();

    private MapGrid _grid;            // full physics scanner before the bake is ready; live overlay after
    private BakedMapView _view;
    private int _viewVersion = -1;
    private RevealMask _reveal;
    private string _revealKey;
    private readonly Stopwatch _perf = new();
    private int _perfFrames;
    private double _perfAvgMs;
    private readonly Stopwatch _perfWindow = new();
    private int _perfLoggedBaked, _perfLoggedScan;
    private readonly Stopwatch _tView = new(), _tOverlay = new(), _tUpload = new(), _tMarkers = new();

    // Markers: drawn in this order (later on top): loot, enemies, teammates.
    private readonly MarkerConfig _markerCfg;
    private readonly List<IMarkerProvider> _providers = new();
    private readonly List<MapMarker> _markers = new();
    private bool[] _providerFailed = Array.Empty<bool>();

    /// <summary>
    /// Runs one provider call; an exception disables only that provider (logged once) until the next
    /// reload, instead of pausing all features (the loader pauses everything on an uncaught throw).
    /// </summary>
    private void Guard(int i, string what, Action call)
    {
        try { call(); }
        catch (Exception e)
        {
            _providerFailed[i] = true;
            Mod.Log.LogError($"[Markers] {_providers[i].GetType().Name}.{what} threw; disabled until reload: {e}");
        }
    }
    private string _perfDetail = "";
    private Transform _target;
    private bool _targetIsPlayer;
    private float _nextTargetSearch;

    private Texture2D _tex;
    private readonly Color32[] _pixels = new Color32[TexSize * TexSize];
    private Il2CppStructArray<Color32> _nativePixels;

    public MinimapFeature(MinimapConfig cfg, MarkerConfig markerCfg, MapBaker baker)
    {
        _cfg = cfg;
        _baker = baker;
        _markerCfg = markerCfg;
        _bigMap = new BigMap(cfg, markerCfg);
        var questTargets = new QuestTargets();
        _providers.Add(new LootMarkers(markerCfg, IsExplored));
        _providers.Add(new QuestMarkers(markerCfg, questTargets));
        _providers.Add(new EnemyMarkers(markerCfg, questTargets));
        _providers.Add(new PlayerMarkers(markerCfg));
        _providerFailed = new bool[_providers.Count];
    }

    public void Update()
    {
        _bigMap.Tick();
        if (Input.GetKeyDown(_cfg.ToggleKey.Value)) _cfg.Enabled.Value = !_cfg.Enabled.Value;
        if (!_bigMap.IsOpen && Input.GetKeyDown(_cfg.ZoomInKey.Value)) _cfg.ViewRadius.Value = Mathf.Max(5f, _cfg.ViewRadius.Value * 0.8f);
        if (!_bigMap.IsOpen && Input.GetKeyDown(_cfg.ZoomOutKey.Value)) _cfg.ViewRadius.Value = Mathf.Min(120f, _cfg.ViewRadius.Value * 1.25f);

        // Re-check once a second: the player object only exists in a level, and is replaced on
        // respawn / level change. A new character means a new level: start a fresh map.
        if (Time.unscaledTime >= _nextTargetSearch)
        {
            _nextTargetSearch = Time.unscaledTime + 1f;
            var found = FindLocalPlayer(out _targetIsPlayer);
            if (found != _target)
            {
                // New player object (level start, respawn): restart the live scan, but keep the baked
                // view and the explored area, which belong to the map (MapBaker/Key), not the body.
                _target = found;
                _grid = null;
                _ref.Reset();
                if (_target != null)
                    Mod.Log.LogInfo($"Minimap tracking '{_target.name}' ({(_targetIsPlayer ? "local player" : "camera fallback")}).");
            }
        }

        if (_target == null || !_targetIsPlayer) { _bigMap.Close(); return; }
        if (Input.GetKeyDown(_cfg.BigMapKey.Value))
        {
            if (_bigMap.IsOpen) _bigMap.Close();
            else _bigMap.Open(_target.position);
        }
        // The big map needs the same data (baked view, explored area, markers) even with the minimap off.
        if (!_cfg.Enabled.Value && !_bigMap.IsOpen) return;
        _perf.Start();

        // F8: log cell classification along 8 directions (live vs. stored), and the frame cost.
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _grid?.DebugRays(_target.position, 15f, Mod.Log);
            Mod.Log.LogInfo($"[Perf] minimap {_perfAvgMs:F3} ms/frame (avg of {PerfFrames} frames, {(_view != null ? "baked map + overlay" : "physics scan")})");
        }

        float cell = Mathf.Clamp(_cfg.CellSize.Value, 0.1f, 4f);
        if (_grid == null || !Mathf.Approximately(_grid.CellSize, cell)) _grid = new MapGrid(cell);

        var feet = _target.position;
        _ref.Update(feet.y);
        if (_reveal == null || _revealKey != _baker.Key)
        {
            _reveal = new RevealMask(cell);
            _revealKey = _baker.Key;
        }
        _reveal.Reveal(feet, _cfg.RevealRadius.Value);

        // Baked map (re)built whenever the baker publishes a new heightfield (first bake, incremental).
        if (_baker.Heightfield == null) _view = null;
        else if (_viewVersion != _baker.Version || _view == null || !Mathf.Approximately(_view.CellSize, cell))
        {
            _view = new BakedMapView(_baker.Heightfield, _baker.Terrain, cell) { Reveal = _reveal };
            _viewVersion = _baker.Version;
        }

        if (_view != null)
        {
            // F4: what the marker providers see (actors, players' states, loot objects).
            if (Input.GetKeyDown(KeyCode.F4))
            {
                Diagnostics.MarkerProbe.Run(feet);
            }

            // F5: colliders + baked/live cells around the player ("why is X missing from the map").
            if (Input.GetKeyDown(KeyCode.F5))
                Diagnostics.NearbyDump.Run(feet, _ref.Value, _baker.Heightfield, _baker.Terrain, cell);

            // F6: compare baked vs physics classification on random cells around the player.
            if (Input.GetKeyDown(KeyCode.F6))
                Diagnostics.BakeParity.Run(feet, _ref.Value, _baker.Heightfield, _baker.Terrain, cell);
            _view.ShowAll = _cfg.RevealFullMap.Value;
            _view.SetReference(_ref.Value);
            _tView.Start();
            _view.Update(feet, _cfg.ViewRadius.Value, ViewBudgetMs);
            _tView.Stop();
            _tOverlay.Start();
            // Live overlay near the player: doors, destruction, anything changed since the bake.
            _grid.Sample(feet, _ref.Value, OverlayRadius, OverlayBudget, OverlayRefreshSeconds, _view.OverrideFromOverlay);
            _tOverlay.Stop();
        }
        else
        {
            _grid.Sample(feet, _ref.Value, _cfg.SampleRadius.Value, Math.Max(1, _cfg.SampleBudget.Value), _cfg.RefreshSeconds.Value);
        }

        if (Input.GetKeyDown(_markerCfg.ToggleKey.Value)) _markerCfg.Enabled.Value = !_markerCfg.Enabled.Value;
        if (_markerCfg.Enabled.Value)
        {
            _tMarkers.Start();
            float now = Time.unscaledTime;
            for (int i = 0; i < _providers.Count; i++)
                if (!_providerFailed[i]) Guard(i, "Update", () => _providers[i].Update(now, feet));
            _tMarkers.Stop();
        }
        _bigMap.Update(feet, _ref.Value, _baker, _reveal, _cfg.RevealFullMap.Value, cell, _view);
        _perf.Stop();
    }

    public void LateUpdate()
    {
        if (_grid == null || _target == null || !_targetIsPlayer) return;
        if (_bigMap.IsOpen)
        {
            // The corner minimap is hidden while the big map is open.
            CollectMarkers();
            _bigMap.Compose(_target.position, _target.eulerAngles.y, _markers);
            return;
        }
        if (!_cfg.Enabled.Value) return;
        _perf.Start();
        Compose();
        _perf.Stop();

        if (!_perfWindow.IsRunning) _perfWindow.Start();
        if (++_perfFrames >= PerfFrames)
        {
            _perfAvgMs = _perf.Elapsed.TotalMilliseconds / _perfFrames;
            // The game's whole frame time over the same window, to put our cost in proportion.
            double frameMs = _perfWindow.Elapsed.TotalMilliseconds / _perfFrames;
            _perfWindow.Restart();
            _perfDetail = $"view {_tView.Elapsed.TotalMilliseconds / _perfFrames:F2}, overlay {_tOverlay.Elapsed.TotalMilliseconds / _perfFrames:F2}, " +
                          $"markers {_tMarkers.Elapsed.TotalMilliseconds / _perfFrames:F2} ({_markers.Count}), " +
                          $"upload {_tUpload.Elapsed.TotalMilliseconds / _perfFrames:F2} ms | game frame {frameMs:F2} ms " +
                          $"({1000.0 / frameMs:F0} fps), minimap = {100.0 * _perfAvgMs / frameMs:F1} % of the frame";
            _perf.Reset(); _tView.Reset(); _tOverlay.Reset(); _tUpload.Reset(); _tMarkers.Reset();
            _perfFrames = 0;

            // Log the cost for the first few windows per mode automatically (F8 logs it on demand).
            bool baked = _view != null;
            if (baked ? _perfLoggedBaked < 4 : _perfLoggedScan < 4)
            {
                if (baked) _perfLoggedBaked++; else _perfLoggedScan++;
                Mod.Log.LogInfo($"[Perf] minimap {_perfAvgMs:F3} ms/frame (avg of {PerfFrames} frames, {(baked ? "baked map + overlay" : "physics scan")}): {_perfDetail}");
            }
        }
    }

    public void OnGUI()
    {
        if (_bigMap.IsOpen) { _bigMap.OnGUI(_ref.Value); return; }
        if (!_cfg.Enabled.Value || _tex == null || _grid == null || !_targetIsPlayer) return;
        if (Event.current.type != EventType.Repaint) return;

        int size = _cfg.SizePx.Value;
        var rect = new Rect(Screen.width - size - 16, 16, size, size);
        GUI.DrawTexture(rect, _tex, ScaleMode.StretchToFill, true);

        // First bake of a level in progress (incremental re-bakes keep showing the old map).
        if (_baker.Heightfield == null && _baker.Current is MapBaker.State.LoadingCache or MapBaker.State.Collecting or MapBaker.State.Rasterizing)
            GUI.Label(new Rect(rect.x, rect.yMax + 4, size, 22), $"Mapping level… {_baker.Progress:P0}");
    }

    public void Dispose()
    {
        _bigMap.Dispose();
        if (_tex != null) Object.Destroy(_tex);
        _tex = null;
        _nativePixels = null;
        _grid = null;
        _view = null;
    }

    /// <summary>Loot only shows where the map is revealed (explored, or RevealFullMap).</summary>
    private bool IsExplored(float x, float z)
    {
        if (_cfg.RevealFullMap.Value) return true;
        if (_reveal == null || _grid == null) return false;
        float cell = _grid.CellSize;
        return _reveal.IsRevealed((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell));
    }

    /// <summary>This frame's markers from every provider, in draw order (loot, quests, enemies, teammates).</summary>
    private void CollectMarkers()
    {
        _markers.Clear();
        if (!_markerCfg.Enabled.Value) return;
        float now = Time.unscaledTime;
        for (int i = 0; i < _providers.Count; i++)
            if (!_providerFailed[i]) Guard(i, "Collect", () => _providers[i].Collect(now, _markers));
    }

    /// <summary>Teammates, enemies and loot as dots on top of the map (under the player's own marker).</summary>
    private void StampMarkers(Vector3 feet, float radius, float yawDeg)
    {
        if (!_markerCfg.Enabled.Value) { _markers.Clear(); return; }
        _tMarkers.Start();
        CollectMarkers();

        var proj = new MarkerProjector(TexSize, radius, feet.x, feet.z, yawDeg, _ref.Value,
            floorBand: 3f, hideOtherFloors: _markerCfg.OtherFloorMarkers.Value == "hide");
        var span = MemoryMarshal.Cast<Color32, Rgba>(_pixels.AsSpan());
        foreach (var m in _markers)
        {
            // Teammates and quest places stay on the map as rim arrows when far away.
            bool clamp = m.Kind is MarkerKind.Teammate or MarkerKind.TeammateDowned or MarkerKind.Quest;
            if (proj.TryProject(m, clampToRim: clamp, out var pm)) MarkerStamp.Draw(span, TexSize, pm, m.Kind);
        }
        _tMarkers.Stop();
    }

    /// <summary>Builds the round, rotated map image from the grid and uploads it.</summary>
    private void Compose()
    {
        if (_tex == null)
        {
            _tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                name = "EIDMods_Minimap",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _nativePixels = new Il2CppStructArray<Color32>(TexSize * TexSize);
        }

        var feet = _target.position;
        var view = Camera.main;
        float yawDeg = !_cfg.RotateWithPlayer.Value ? 0f : (view != null ? view.transform.eulerAngles.y : _target.eulerAngles.y);
        float yaw = yawDeg * Mathf.Deg2Rad;
        float sin = Mathf.Sin(yaw), cos = Mathf.Cos(yaw);
        // Screen up = camera forward (sin, cos); screen right = camera right (cos, -sin), in world XZ.

        float radius = _cfg.ViewRadius.Value;
        float worldPerPx = 2f * radius / TexSize;
        float invCell = 1f / _grid.CellSize;
        const float half = TexSize * 0.5f;
        float rimInner = (half - 2.5f) * (half - 2.5f), rimOuter = half * half;

        for (int py = 0; py < TexSize; py++)
        {
            float v = (py + 0.5f - half) * worldPerPx;             // texture row 0 = bottom of the map
            float dv = py + 0.5f - half;
            int row = py * TexSize;
            for (int px = 0; px < TexSize; px++)
            {
                float du = px + 0.5f - half;
                float d2 = du * du + dv * dv;
                if (d2 > rimOuter) { _pixels[row + px] = Clear; continue; }
                if (d2 > rimInner) { _pixels[row + px] = Rim; continue; }

                float u = du * worldPerPx;
                float wx = feet.x + cos * u + sin * v;
                float wz = feet.z - sin * u + cos * v;
                // Managed floor: UnityEngine.Mathf calls are IL2CPP interop calls, and two per pixel
                // (130k per frame) cost ~13 ms.
                int cx = (int)MathF.Floor(wx * invCell), cz = (int)MathF.Floor(wz * invCell);
                var c = _view != null ? _view.Get(cx, cz) : default;
                if (c.a == 0) c = _grid.Get(cx, cz); // not built yet / overlay-only cells
                _pixels[row + px] = c.a == 0 ? Background : c;
            }
        }

        StampMarkers(feet, radius, yawDeg);
        DrawMarker(_target.eulerAngles.y - yawDeg);
        _tUpload.Start();
        Upload();
        _tUpload.Stop();
    }

    /// <summary>Dot in the centre plus a short tick showing which way the character faces.</summary>
    private void DrawMarker(float relativeYawDeg)
    {
        int c = TexSize / 2;
        for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
            if (x * x + y * y <= 5) _pixels[(c + y) * TexSize + c + x] = Marker;

        float a = relativeYawDeg * Mathf.Deg2Rad;
        float dx = Mathf.Sin(a), dy = Mathf.Cos(a);
        for (int i = 3; i <= 9; i++)
        {
            int x = c + Mathf.RoundToInt(dx * i), y = c + Mathf.RoundToInt(dy * i);
            _pixels[y * TexSize + x] = Marker;
        }
    }

    private unsafe void Upload()
    {
        // One bulk copy into the IL2CPP array instead of 65k interop element writes.
        // An IL2CPP array is: object header (klass, monitor), bounds, max_length = 4 pointers,
        // then the elements. (Same math as Il2CppArrayBase.ArrayStartPointer, which is protected.)
        long bytes = (long)_pixels.Length * sizeof(Color32);
        var dst = _nativePixels.Pointer + 4 * IntPtr.Size;
        fixed (Color32* src = _pixels)
            Buffer.MemoryCopy(src, (void*)dst, bytes, bytes);
        _tex.SetPixels32(_nativePixels);
        _tex.Apply(false);
    }

    private static Transform FindLocalPlayer(out bool isPlayer)
    {
        // The game's own static for "my character" (AG.Actors.Player.Local).
        var player = AG.Actors.Player.Local;
        if (player != null) { isPlayer = true; return player.transform; }

        // Mirror's local player object, in case the game's static isn't set yet.
        var local = NetworkClient.localPlayer;
        if (local != null) { isPlayer = true; return local.transform; }

        // Menus: nothing to map. (The camera is only used as a "we're not in a level" signal.)
        isPlayer = false;
        var main = Camera.main;
        return main != null ? main.transform : null;
    }
}
