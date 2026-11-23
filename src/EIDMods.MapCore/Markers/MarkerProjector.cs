using System;

namespace EIDMods.MapCore.Markers;

/// <summary>
/// World position to minimap pixel, with the same math the minimap composes with: the map is
/// centred on the player, "up" is the camera's forward (sin yaw, cos yaw) and texture row 0 is the
/// bottom. Compose maps a pixel offset (u, v) to world as wx = x + cos·u + sin·v and
/// wz = z − sin·u + cos·v, so this inverts that rotation.
/// </summary>
public sealed class MarkerProjector
{
    private const float RimInset = 4f;    // clamped markers sit this many px inside the rim
    private const float OnMapLimit = 3f;  // markers closer than this to the rim count as off-map
    private const float OtherFloorAlpha = 0.35f;

    private readonly float _half, _pxPerMetre, _px, _pz, _sin, _cos, _refY, _floorBand;
    private readonly bool _hideOtherFloors;

    public MarkerProjector(int texSize, float viewRadius, float playerX, float playerZ, float yawDeg, float refY,
        float floorBand = 3f, bool hideOtherFloors = false)
    {
        _half = texSize * 0.5f;
        _pxPerMetre = _half / viewRadius;
        _px = playerX;
        _pz = playerZ;
        float yaw = yawDeg * (MathF.PI / 180f);
        _sin = MathF.Sin(yaw);
        _cos = MathF.Cos(yaw);
        _refY = refY;
        _floorBand = floorBand;
        _hideOtherFloors = hideOtherFloors;
    }

    /// <param name="clampToRim">Off-map markers (teammates) sit on the rim instead of being dropped.</param>
    public bool TryProject(in MapMarker m, bool clampToRim, out ProjectedMarker p)
    {
        p = default;
        float alpha = 1f;
        if (MathF.Abs(m.Y - _refY) > _floorBand)
        {
            if (_hideOtherFloors) return false;
            alpha = OtherFloorAlpha;
        }

        float dx = m.X - _px, dz = m.Z - _pz;
        float u = (_cos * dx - _sin * dz) * _pxPerMetre;
        float v = (_sin * dx + _cos * dz) * _pxPerMetre;
        float dist = MathF.Sqrt(u * u + v * v);
        if (!float.IsFinite(dist)) return false;

        bool onRim = false;
        if (dist > _half - OnMapLimit)
        {
            if (!clampToRim) return false;
            float r = _half - RimInset;
            u = u / dist * r;
            v = v / dist * r;
            onRim = true;
        }

        float angle = MathF.Atan2(u, v) * (180f / MathF.PI); // 0 = up, clockwise
        p = new ProjectedMarker((int)MathF.Floor(_half + u), (int)MathF.Floor(_half + v), onRim, angle, alpha);
        return true;
    }
}
