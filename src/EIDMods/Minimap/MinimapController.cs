using System;
using Mirror;
using UnityEngine;

namespace EIDMods.Minimap;

/// <summary>
/// v1 minimap: an orthographic camera above the local player renders into a RenderTexture
/// that is drawn in the corner with IMGUI.
///
/// Known caveats to work through in-game (see README "Minimap approach"):
///  - The game is HDRP and dark by design; the map camera may need its own exposure/lighting
///    override (HDAdditionalCameraData) or a different approach (NavMesh / collider outline).
///  - Ceilings block a top-down view; the near clip plane is placed just above the player's head
///    so geometry above that is clipped away.
/// </summary>
public class MinimapController : MonoBehaviour
{
    public MinimapController(IntPtr ptr) : base(ptr) { }

    private Camera _cam;
    private RenderTexture _rt;
    private Transform _target;
    private bool _targetIsPlayer;
    private float _nextTargetSearch;

    private static MinimapConfig Cfg => Plugin.MinimapConfig;

    private void Update()
    {
        if (Input.GetKeyDown(Cfg.ToggleKey.Value)) Cfg.Enabled.Value = !Cfg.Enabled.Value;
        if (Input.GetKeyDown(Cfg.ZoomInKey.Value)) Cfg.ViewRadius.Value = Mathf.Max(5f, Cfg.ViewRadius.Value * 0.8f);
        if (Input.GetKeyDown(Cfg.ZoomOutKey.Value)) Cfg.ViewRadius.Value = Mathf.Min(200f, Cfg.ViewRadius.Value * 1.25f);

        // Re-check once a second: the player object only exists in a dungeon, and is
        // replaced on respawn / scene change. Until then we fall back to the camera.
        if (Time.unscaledTime >= _nextTargetSearch)
        {
            _nextTargetSearch = Time.unscaledTime + 1f;
            var found = FindLocalPlayer(out _targetIsPlayer);
            if (found != _target)
            {
                _target = found;
                if (_target != null)
                    Plugin.Logger.LogInfo($"Minimap tracking '{_target.name}' ({(_targetIsPlayer ? "local player" : "camera fallback")}).");
            }
        }

        bool active = Cfg.Enabled.Value && _target != null;
        if (active) EnsureCamera();
        if (_cam != null) _cam.enabled = active;
    }

    private void LateUpdate()
    {
        if (_cam == null || !_cam.enabled || _target == null) return;

        float height = Cfg.HeightAbovePlayer.Value;
        var pos = _target.position;
        _cam.transform.position = new Vector3(pos.x, pos.y + height, pos.z);

        // Third-person game: align "up" on the map with where the view camera looks,
        // not where the character's body faces (they differ when strafing / turning).
        var view = Camera.main;
        float yaw = !Cfg.RotateWithPlayer.Value ? 0f
            : (view != null ? view.transform.eulerAngles.y : _target.eulerAngles.y);
        _cam.transform.rotation = Quaternion.Euler(90f, yaw, 0f);

        _cam.orthographicSize = Cfg.ViewRadius.Value;
        // Clip away everything above ~2.5m over the player's feet (ceilings, upper floors).
        _cam.nearClipPlane = Mathf.Max(0.01f, height - 2.5f);
        _cam.farClipPlane = height + 10f;
    }

    private void OnGUI()
    {
        if (_rt == null || _cam == null || !_cam.enabled) return;
        if (Event.current.type != EventType.Repaint) return;

        int size = Cfg.SizePx.Value;
        var rect = new Rect(Screen.width - size - 16, 16, size, size);
        GUI.DrawTexture(rect, _rt, ScaleMode.StretchToFill, false);

        // Player marker in the center.
        var c = rect.center;
        GUI.DrawTexture(new Rect(c.x - 3, c.y - 3, 6, 6), Texture2D.whiteTexture);
    }

    private void EnsureCamera()
    {
        if (_cam != null) return;

        _rt = new RenderTexture(512, 512, 16) { name = "EIDMods_MinimapRT" };
        _rt.Create();

        var go = new GameObject("EIDMods_MinimapCamera");
        DontDestroyOnLoad(go);
        _cam = go.AddComponent<Camera>();
        _cam.orthographic = true;
        _cam.targetTexture = _rt;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
        _cam.depth = -100;
    }

    private static Transform FindLocalPlayer(out bool isPlayer)
    {
        // The game's own static for "my character" (AG.Actors.Player.Local).
        var player = AG.Actors.Player.Local;
        if (player != null) { isPlayer = true; return player.transform; }

        // Mirror's local player object, in case the game's static isn't set yet.
        var local = NetworkClient.localPlayer;
        if (local != null) { isPlayer = true; return local.transform; }

        // Fallback: the main camera (menus). In-game this is the third-person camera,
        // which sits behind the character, so it's only a placeholder.
        isPlayer = false;
        var main = Camera.main;
        return main != null ? main.transform : null;
    }
}
