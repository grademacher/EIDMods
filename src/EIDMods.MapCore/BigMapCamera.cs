using System;
using System.Collections.Generic;

namespace EIDMods.MapCore;

/// <summary>
/// What the north-up big map shows: a centre (world X/Z) and a radius (metres from the centre to the
/// panel edge). Panel coordinates are pixels from the panel's bottom-left, y up (same as the texture).
/// </summary>
public sealed class BigMapCamera
{
    public const float MinRadius = 5f, MaxRadius = 300f;

    public float CenterX { get; private set; }
    public float CenterZ { get; private set; }
    public float Radius { get; private set; }

    public BigMapCamera(float centerX, float centerZ, float radius)
    {
        CenterX = centerX;
        CenterZ = centerZ;
        Radius = Math.Clamp(radius, MinRadius, MaxRadius);
    }

    public void CenterOn(float x, float z)
    {
        CenterX = x;
        CenterZ = z;
    }

    /// <summary>The map follows the mouse: dragging right by dx pixels shows what was to the left.</summary>
    public void Drag(float dxPx, float dyPx, float panelPx)
    {
        float metresPerPx = 2f * Radius / panelPx;
        CenterX -= dxPx * metresPerPx;
        CenterZ -= dyPx * metresPerPx;
    }

    /// <summary>Multiplies the radius by factor (&lt; 1 zooms in), keeping the world point under the cursor fixed.</summary>
    public void Zoom(float factor, float cursorX, float cursorY, float panelPx)
    {
        var (wx, wz) = PanelToWorld(cursorX, cursorY, panelPx);
        Radius = Math.Clamp(Radius * factor, MinRadius, MaxRadius);
        var (nx, nz) = PanelToWorld(cursorX, cursorY, panelPx);
        CenterX += wx - nx;
        CenterZ += wz - nz;
    }

    public (float x, float z) PanelToWorld(float px, float py, float panelPx)
    {
        float metresPerPx = 2f * Radius / panelPx;
        return (CenterX + (px - panelPx * 0.5f) * metresPerPx, CenterZ + (py - panelPx * 0.5f) * metresPerPx);
    }
}

/// <summary>
/// The big map's height slider: from 3 m below the lowest floor to 3 m above the highest (or ±20 m
/// around you when the level has no clear floors). Heights within <see cref="Snap"/> of a floor snap
/// to it, so landing on a floor while dragging is easy.
/// </summary>
public sealed class HeightSlider
{
    public const float Margin = 3f, NoFloorsRange = 20f, Snap = 0.4f;

    private readonly IReadOnlyList<float> _floors;
    public float Min { get; }
    public float Max { get; }

    public HeightSlider(IReadOnlyList<float> floors, float currentHeight)
    {
        _floors = floors;
        if (floors.Count == 0)
        {
            Min = currentHeight - NoFloorsRange;
            Max = currentHeight + NoFloorsRange;
        }
        else
        {
            Min = MathF.Min(floors[0], currentHeight) - Margin;
            Max = MathF.Max(floors[^1], currentHeight) + Margin;
        }
    }

    /// <summary>0 at the bottom of the slider, 1 at the top.</summary>
    public float FractionOf(float height) => Math.Clamp((height - Min) / (Max - Min), 0f, 1f);

    public float HeightAt(float fraction)
    {
        float h = Min + Math.Clamp(fraction, 0f, 1f) * (Max - Min);
        foreach (float f in _floors) if (MathF.Abs(f - h) <= Snap) return f;
        return h;
    }
}
