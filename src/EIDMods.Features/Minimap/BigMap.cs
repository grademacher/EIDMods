using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EIDMods.Features.MapBake;
using EIDMods.Features.Minimap.Markers;
using EIDMods.MapCore;
using EIDMods.MapCore.Markers;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EIDMods.Features.Minimap;

/// <summary>
/// The pop-out map: a large north-up panel in the middle of the screen that you can drag (left mouse),
/// zoom (wheel, +/-), recentre (C / middle mouse) and look at another height (PageUp/PageDown jump
/// between floors, a slider beside the panel moves smoothly, Home follows you again). Uses its own
/// BakedMapView (its own chunk cache at its own height), so the corner minimap is untouched; while it
/// follows your height it copies the minimap's live door/destruction corrections.
///
/// While open, the cursor is freed, the game's own input switch (AG.Core.Keybinds.IsBlocked) is held on
/// so clicks don't swing a weapon, and the camera's look sensitivity is held at 0 so dragging doesn't
/// turn it. All three are put back on close. Local only: nothing is sent to other players.
/// </summary>
internal sealed class BigMap : IDisposable
{
    private const int TexSize = 512;
    private const float BudgetMs = 4f, RedrawInterval = 0.1f, StartRadius = 40f;
    private static readonly Color32 Background = new(14, 13, 12, 235);
    private static readonly Color32 PlayerColor = new(255, 255, 255, 255);
    private static readonly Color32 Outline = new(10, 10, 10, 255);

    private readonly MinimapConfig _cfg;
    private readonly MarkerConfig _markerCfg;

    private BigMapCamera _cam;
    private bool _follow = true;
    private float _height;
    private float _effectiveHeight;

    private List<float> _floors = new();
    private Task<List<float>> _floorTask;
    private int _floorsVersion = -1;
    private Vector3 _floorsAt;
    private const float FloorRadius = 120f, FloorRecomputeDistance = 60f, FloorVerticalRange = 30f;

    private BakedMapView _view;
    private int _viewVersion = -1;
    private Texture2D _tex, _white;
    private readonly Color32[] _pixels = new Color32[TexSize * TexSize];
    private Il2CppStructArray<Color32> _nativePixels;
    private float _nextRedraw, _nextOverrideCopy;
    private bool _dirty;

    private bool _dragging, _sliderDragging;
    private Vector3 _lastMouse;
    private Rect _panel, _slider;   // GUI coordinates (top-left origin)
    private readonly List<(Vector2 pos, string text)> _labels = new();

    private bool _savedState, _prevBlocked, _prevVisible;
    private CursorLockMode _prevLock;
    private AG.CameraComponents.CameraController _camCtl;
    private float _prevSensX, _prevSensY;
    private int _restoreCameraAtFrame = -1;

    public bool IsOpen { get; private set; }

    public BigMap(MinimapConfig cfg, MarkerConfig markerCfg)
    {
        _cfg = cfg;
        _markerCfg = markerCfg;
    }

    public void Open(Vector3 feet)
    {
        if (IsOpen) return;
        IsOpen = true;
        if (_restoreCameraAtFrame >= 0) RestoreCamera(); // reopened within the grace frames
        _cam ??= new BigMapCamera(feet.x, feet.z, StartRadius);
        _cam.CenterOn(feet.x, feet.z);
        _follow = true;
        _dirty = true;
        _prevBlocked = AG.Core.Keybinds.IsBlocked;
        _prevLock = Cursor.lockState;
        _prevVisible = Cursor.visible;
        // Keybinds.IsBlocked stops actions (attacks) but not mouse look: hold the camera's look
        // sensitivity at 0 too (checked live, 2026-11-23).
        _camCtl = FindCameraController();
        if (_camCtl != null)
        {
            _prevSensX = _camCtl.horizontalSensitivity;
            _prevSensY = _camCtl.verticalSensitivity;
        }
        _savedState = true;
    }

    private static AG.CameraComponents.CameraController FindCameraController()
    {
        var found = UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<AG.CameraComponents.CameraController>(), FindObjectsSortMode.None);
        return found.Length > 0 ? found[0].TryCast<AG.CameraComponents.CameraController>() : null;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _dragging = _sliderDragging = false;
        if (_savedState)
        {
            try { AG.Core.Keybinds.IsBlocked = _prevBlocked; } catch (Exception) { }
            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
            // Re-locking warps the cursor to the centre, which the game reads as a mouse flick: keep
            // the camera still for a few more frames (Tick), then give the sensitivity back.
            _restoreCameraAtFrame = Time.frameCount + 3;
            _savedState = false;
        }
    }

    /// <summary>Every frame, open or not: finishes giving the camera back after a close.</summary>
    public void Tick()
    {
        if (_restoreCameraAtFrame < 0 || IsOpen || Time.frameCount < _restoreCameraAtFrame) return;
        RestoreCamera();
    }

    private void RestoreCamera()
    {
        _restoreCameraAtFrame = -1;
        try
        {
            if (_camCtl != null)
            {
                _camCtl.horizontalSensitivity = _prevSensX;
                _camCtl.verticalSensitivity = _prevSensY;
            }
        }
        catch (Exception) { }
        _camCtl = null;
    }

    /// <summary>Input and map building. Call every frame while open.</summary>
    public void Update(Vector3 feet, float refY, MapBaker baker, RevealMask reveal, bool showAll, float cell, BakedMapView minimapView)
    {
        if (!IsOpen) return;
        if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }

        // Hold the cursor free and the game's input off (the game may re-lock either each frame).
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        AG.Core.Keybinds.IsBlocked = true;
        if (_camCtl != null)
        {
            _camCtl.horizontalSensitivity = 0f; // re-applied each frame: the game resets it when options change
            _camCtl.verticalSensitivity = 0f;
        }

        UpdateFloors(baker, feet);
        HandleInput(feet, refY);
        _effectiveHeight = _follow ? refY : _height;

        if (baker.Heightfield == null) { _view = null; return; }
        if (_view == null || _viewVersion != baker.Version || !Mathf.Approximately(_view.CellSize, cell))
        {
            _view = new BakedMapView(baker.Heightfield, baker.Terrain, cell);
            _viewVersion = baker.Version;
        }
        _view.Reveal = reveal;
        _view.ShowAll = showAll;
        _view.SetReference(_effectiveHeight);
        // Doors opened/closed near you: while following your height, take the minimap's live overlay
        // corrections (same reference height, so they mean the same thing). A few cells, twice a second.
        if (_follow && minimapView != null && Time.unscaledTime >= _nextOverrideCopy)
        {
            _nextOverrideCopy = Time.unscaledTime + 0.5f;
            if (_view.CopyOverridesFrom(minimapView)) _dirty = true;
        }
        // Radius × √2 covers the square's corners.
        _view.Update(new Vector3(_cam.CenterX, 0f, _cam.CenterZ), _cam.Radius * 1.42f, BudgetMs);
    }

    /// <summary>Floors within 120 m of you (off the main thread), redone after a new bake or after you move 60 m.</summary>
    private void UpdateFloors(MapBaker baker, Vector3 feet)
    {
        if (_floorTask != null && _floorTask.IsCompleted)
        {
            // Only floors within 30 m above/below you: big flat surfaces high above (the Moor's sewers,
            // 2026-11-23) would otherwise stretch the slider over 100+ m.
            if (_floorTask.Status == TaskStatus.RanToCompletion)
                _floors = _floorTask.Result.FindAll(f => MathF.Abs(f - _floorsAt.y) <= FloorVerticalRange);
            Mod.Log.LogInfo($"[BigMap] floors: {(_floors.Count == 0 ? "none found" : string.Join(", ", _floors.ConvertAll(f => f.ToString("F1"))))}");
            _floorTask = null;
            _dirty = true;
        }
        var hf = baker.Heightfield;
        if (hf == null || _floorTask != null) return;
        float dx = feet.x - _floorsAt.x, dz = feet.z - _floorsAt.z;
        if (_floorsVersion == baker.Version && dx * dx + dz * dz < FloorRecomputeDistance * FloorRecomputeDistance) return;
        _floorsVersion = baker.Version;
        _floorsAt = feet;
        float x = feet.x, z = feet.z;
        _floorTask = Task.Run(() => FloorLevels.Find(hf, x, z, FloorRadius)); // pure C#, off the main thread
    }

    private void HandleInput(Vector3 feet, float refY)
    {
        float size = PanelSize();
        float x0 = (Screen.width - size) * 0.5f, y0 = (Screen.height - size) * 0.5f;
        var mouse = Input.mousePosition; // bottom-left origin, like the panel
        float px = mouse.x - x0, py = mouse.y - y0;
        bool overPanel = px >= 0 && py >= 0 && px <= size && py <= size;
        float sx = x0 + size + SliderGap;
        bool overSlider = mouse.x >= sx - 6 && mouse.x <= sx + SliderWidth + 6 && py >= 0 && py <= size;

        // Zoom: wheel over the panel (about the cursor), or the minimap's zoom keys (about the centre).
        float wheel = Input.mouseScrollDelta.y;
        if (overPanel && wheel != 0f) { _cam.Zoom(MathF.Pow(0.85f, Math.Clamp(wheel, -6f, 6f)), px, py, size); _dirty = true; }
        if (Input.GetKeyDown(_cfg.ZoomInKey.Value)) { _cam.Zoom(0.8f, size * 0.5f, size * 0.5f, size); _dirty = true; }
        if (Input.GetKeyDown(_cfg.ZoomOutKey.Value)) { _cam.Zoom(1.25f, size * 0.5f, size * 0.5f, size); _dirty = true; }

        if (Input.GetKeyDown(KeyCode.C) || (overPanel && Input.GetMouseButtonDown(2)))
        {
            _cam.CenterOn(feet.x, feet.z);
            _dirty = true;
        }

        // Elevation: floors with PageUp/PageDown, smooth with the slider, Home follows you again.
        if (Input.GetKeyDown(KeyCode.Home)) { _follow = true; _dirty = true; }
        int step = Input.GetKeyDown(KeyCode.PageUp) ? 1 : Input.GetKeyDown(KeyCode.PageDown) ? -1 : 0;
        if (step != 0)
        {
            float from = _follow ? refY : _height;
            // Your own height is a stop too, so PageUp comes back after PageDown even in open areas
            // with no floor detected at your level (the Moor, 2026-11-23).
            float to = FloorLevels.Step(_floors, from, step, refY);
            _height = MathF.Abs(to - from) > 0.01f ? to : from + step * 3f; // nothing further: 3 m steps
            _height = Math.Clamp(_height, refY - FloorVerticalRange, refY + FloorVerticalRange);
            _follow = MathF.Abs(_height - refY) < 0.01f; // back at your own height: follow you again
            _dirty = true;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (overSlider) _sliderDragging = true;
            else if (overPanel) _dragging = true;
            _lastMouse = mouse;
        }
        if (!Input.GetMouseButton(0)) _dragging = _sliderDragging = false;

        if (_dragging)
        {
            var d = mouse - _lastMouse;
            if (d.x != 0f || d.y != 0f) { _cam.Drag(d.x, d.y, size); _dirty = true; }
        }
        if (_sliderDragging)
        {
            var s = new HeightSlider(_floors, refY);
            _height = s.HeightAt(py / size);
            _follow = false;
            _dirty = true;
        }
        _lastMouse = mouse;
    }

    /// <summary>Redraws the panel texture: at 10 Hz, or right away after you pan/zoom/change height.</summary>
    public void Compose(Vector3 feet, float facingDeg, List<MapMarker> markers)
    {
        if (!IsOpen) return;
        float now = Time.unscaledTime;
        if (!_dirty && now < _nextRedraw) return;
        _dirty = false;
        _nextRedraw = now + RedrawInterval;

        if (_tex == null)
        {
            _tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                name = "EIDMods_BigMap",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _nativePixels = new Il2CppStructArray<Color32>(TexSize * TexSize);
        }

        float cx = _cam.CenterX, cz = _cam.CenterZ, radius = _cam.Radius;
        float mpp = 2f * radius / TexSize;
        float invCell = _view != null ? 1f / _view.CellSize : 0f;
        const float half = TexSize * 0.5f;
        for (int py = 0; py < TexSize; py++)
        {
            float wz = cz + (py + 0.5f - half) * mpp;
            int cellZ = (int)MathF.Floor(wz * invCell), row = py * TexSize;
            for (int px = 0; px < TexSize; px++)
            {
                if (_view == null) { _pixels[row + px] = Background; continue; }
                float wx = cx + (px + 0.5f - half) * mpp;
                var c = _view.Get((int)MathF.Floor(wx * invCell), cellZ);
                _pixels[row + px] = c.a == 0 ? Background : c;
            }
        }

        // Markers: same rules as the minimap, but north-up, square, relative to the chosen height.
        _labels.Clear();
        float size = PanelSize();
        _panel = new Rect((Screen.width - size) * 0.5f, (Screen.height - size) * 0.5f, size, size);
        var span = MemoryMarshal.Cast<Color32, Rgba>(_pixels.AsSpan());
        if (_markerCfg.Enabled.Value)
        {
            var proj = new MarkerProjector(TexSize, radius, cx, cz, 0f, _effectiveHeight, floorBand: 3f,
                hideOtherFloors: _markerCfg.OtherFloorMarkers.Value == "hide", square: true);
            foreach (var m in markers)
            {
                bool clamp = m.Kind is MarkerKind.Teammate or MarkerKind.TeammateDowned or MarkerKind.Quest
                    or MarkerKind.Extraction or MarkerKind.ExtractionInactive;
                if (!proj.TryProject(m, clamp, out var pm)) continue;
                MarkerStamp.Draw(span, TexSize, pm, m.Kind);
                if (m.Label != null && !pm.OnRim)
                {
                    // The tick above/below a quest diamond means "on another floor"; say how far in words too.
                    float dy = m.Y - _effectiveHeight;
                    string text = pm.Vertical == 0 ? m.Label : $"{m.Label}  ({MathF.Abs(dy):F0} m {(dy > 0 ? "above" : "below")})";
                    _labels.Add((new Vector2(_panel.x + (pm.Px + 7) * size / TexSize, _panel.yMax - (pm.Py + 7) * size / TexSize), text));
                }
            }
        }
        DrawPlayer(feet, facingDeg, cx, cz, mpp);

        long bytes = (long)_pixels.Length * 4;
        unsafe
        {
            var dst = _nativePixels.Pointer + 4 * IntPtr.Size; // IL2CPP array header (see MinimapFeature.Upload)
            fixed (Color32* src = _pixels) Buffer.MemoryCopy(src, (void*)dst, bytes, bytes);
        }
        _tex.SetPixels32(_nativePixels);
        _tex.Apply(false);
    }

    /// <summary>You: white dot with a dark outline and a tick showing where you face.</summary>
    private void DrawPlayer(Vector3 feet, float facingDeg, float cx, float cz, float mpp)
    {
        int pcx = (int)MathF.Floor((feet.x - cx) / mpp + TexSize * 0.5f);
        int pcz = (int)MathF.Floor((feet.z - cz) / mpp + TexSize * 0.5f);
        for (int y = -4; y <= 4; y++)
        for (int x = -4; x <= 4; x++)
        {
            int d2 = x * x + y * y;
            if (d2 <= 9) Put(pcx + x, pcz + y, PlayerColor);
            else if (d2 <= 16) Put(pcx + x, pcz + y, Outline);
        }
        float a = facingDeg * (MathF.PI / 180f);
        for (int i = 4; i <= 11; i++) Put(pcx + (int)MathF.Round(MathF.Sin(a) * i), pcz + (int)MathF.Round(MathF.Cos(a) * i), PlayerColor);
    }

    private void Put(int x, int y, Color32 c)
    {
        if ((uint)x < TexSize && (uint)y < TexSize) _pixels[y * TexSize + x] = c;
    }

    private const float SliderGap = 14f, SliderWidth = 14f;

    private static float PanelSize() => Mathf.Min(Screen.height * 0.8f, Screen.width * 0.7f);

    public void OnGUI(float refY)
    {
        if (!IsOpen || _tex == null || Event.current.type != EventType.Repaint) return;
        if (_white == null)
        {
            _white = new Texture2D(1, 1) { name = "EIDMods_BigMapWhite" };
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }

        float size = PanelSize();
        _panel = new Rect((Screen.width - size) * 0.5f, (Screen.height - size) * 0.5f, size, size);
        Fill(new Rect(_panel.x - 3, _panel.y - 3, size + 6, size + 6), new Color(0.59f, 0.55f, 0.47f, 1f));
        GUI.DrawTexture(_panel, _tex, ScaleMode.StretchToFill, true);

        // Labels clipped to the panel (a group clips its contents; coordinates become panel-relative).
        GUI.BeginGroup(_panel);
        foreach (var (pos, text) in _labels)
        {
            var r = new Rect(pos.x - _panel.x, pos.y - 13 - _panel.y, 420, 28);
            Shadowed(r, text, new Color(0.55f, 0.92f, 1f, 1f));
        }
        GUI.EndGroup();

        // Height slider: track, floor ticks, handle.
        var slider = new HeightSlider(_floors, refY);
        _slider = new Rect(_panel.xMax + SliderGap, _panel.y, SliderWidth, size);
        Fill(_slider, new Color(0.08f, 0.08f, 0.08f, 0.85f));
        foreach (float f in _floors)
        {
            float fy = _slider.yMax - slider.FractionOf(f) * size;
            Fill(new Rect(_slider.x - 4, fy - 1, SliderWidth + 8, 2), new Color(0.85f, 0.8f, 0.7f, 1f));
        }
        float hy = _slider.yMax - slider.FractionOf(_effectiveHeight) * size;
        Fill(new Rect(_slider.x - 3, hy - 4, SliderWidth + 6, 8), _follow ? new Color(1f, 1f, 1f, 1f) : new Color(0.27f, 0.84f, 0.92f, 1f));

        Shadowed(new Rect(_panel.x, _panel.y - 32, size, 30), HeightText(refY), Color.white);
        Shadowed(new Rect(_panel.x - 120, _panel.yMax + 6, size + 240, 30),
            "Drag: move   Wheel: zoom   C: recentre   PgUp/PgDn: floors   Slider: height   Home: follow me   Esc/" + _cfg.BigMapKey.Value + ": close",
            new Color(0.85f, 0.82f, 0.75f, 1f));
    }

    private string HeightText(float refY)
    {
        if (_follow) return $"Following you ({refY:F1} m)   ·   {_cam.Radius:F0} m";
        int idx = _floors.FindIndex(f => MathF.Abs(f - _height) < 0.05f);
        string where = idx >= 0 ? $"Floor {idx + 1} of {_floors.Count} ({_height:F1} m)" : $"Height {_height:F1} m";
        return $"{where}   ·   {_height - refY:+0.0;-0.0;0.0} m from you   ·   {_cam.Radius:F0} m";
    }

    private void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, _white);
        GUI.color = old;
    }

    private static GUIStyle _text;

    private static void Shadowed(Rect r, string text, Color c)
    {
        _text ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 60), wordWrap = false };
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.9f);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _text);
        GUI.color = c;
        GUI.Label(r, text, _text);
        GUI.color = old;
    }

    public void Dispose()
    {
        Close();
        if (_restoreCameraAtFrame >= 0) RestoreCamera(); // being unloaded: no later frames to wait for
        if (_tex != null) UnityEngine.Object.Destroy(_tex);
        if (_white != null) UnityEngine.Object.Destroy(_white);
        _tex = _white = null;
    }
}
