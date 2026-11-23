using System;

namespace EIDMods.MapCore.Markers;

/// <summary>
/// Draws a marker into the minimap's RGBA buffer (row 0 = bottom): a filled dot sized and coloured by
/// <see cref="MarkerPalette"/>, a 1 px dark outline ring for dots of radius 2+ (so they read on both
/// light walls and dark floors), or, for rim-clamped markers, a small arrow pointing along
/// RimAngleDeg (0 = up, clockwise). Alpha-blended (palette alpha × marker alpha) and clipped.
/// </summary>
public static class MarkerStamp
{
    public static void Draw(Span<Rgba> pixels, int texSize, in ProjectedMarker p, MarkerKind kind)
    {
        if (pixels.Length != texSize * texSize) throw new ArgumentException("pixels must be texSize * texSize");
        var color = MarkerPalette.For(kind);
        float alpha = p.Alpha * color.A / 255f;

        if (p.OnRim)
        {
            DrawArrow(pixels, texSize, p.Px, p.Py, p.RimAngleDeg, color, alpha);
            return;
        }

        int r = MarkerPalette.Radius(kind);
        int outer = r >= 2 ? r + 1 : r;
        for (int dy = -outer; dy <= outer; dy++)
        for (int dx = -outer; dx <= outer; dx++)
        {
            int d2 = dx * dx + dy * dy;
            if (d2 <= r * r) Blend(pixels, texSize, p.Px + dx, p.Py + dy, color, alpha);
            else if (d2 <= outer * outer) Blend(pixels, texSize, p.Px + dx, p.Py + dy, MarkerPalette.Outline, alpha);
        }
    }

    private static void DrawArrow(Span<Rgba> pixels, int texSize, int cx, int cy, float angleDeg, Rgba color, float alpha)
    {
        float a = angleDeg * (MathF.PI / 180f);
        float dirX = MathF.Sin(a), dirY = MathF.Cos(a);   // 0 = up (+y), 90 = right (+x)
        float perpX = dirY, perpY = -dirX;
        // Triangle: tip 3 px ahead, base 2 px behind and 2.5 px to each side.
        float tx = cx + dirX * 3f, ty = cy + dirY * 3f;
        float bx = cx - dirX * 2f, by = cy - dirY * 2f;
        float ax = bx + perpX * 2.5f, ay = by + perpY * 2.5f;
        float cx2 = bx - perpX * 2.5f, cy2 = by - perpY * 2.5f;

        for (int y = cy - 4; y <= cy + 4; y++)
        for (int x = cx - 4; x <= cx + 4; x++)
            if (InTriangle(x, y, tx, ty, ax, ay, cx2, cy2)) Blend(pixels, texSize, x, y, color, alpha);
    }

    private static bool InTriangle(float px, float py, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        float d1 = Edge(px, py, x1, y1, x2, y2), d2 = Edge(px, py, x2, y2, x3, y3), d3 = Edge(px, py, x3, y3, x1, y1);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static float Edge(float px, float py, float x1, float y1, float x2, float y2) =>
        (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2);

    private static void Blend(Span<Rgba> pixels, int texSize, int x, int y, Rgba c, float a)
    {
        if ((uint)x >= (uint)texSize || (uint)y >= (uint)texSize || a <= 0f) return;
        ref var dst = ref pixels[y * texSize + x];
        if (a >= 1f) { dst = new Rgba(c.R, c.G, c.B, 255); return; }
        float k = 1f - a;
        dst = new Rgba((byte)(c.R * a + dst.R * k), (byte)(c.G * a + dst.G * k), (byte)(c.B * a + dst.B * k),
                       (byte)Math.Max(dst.A, (int)(255 * a)));
    }
}
