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
            DrawArrow(pixels, texSize, p.Px, p.Py, p.RimAngleDeg, color, alpha, kind == MarkerKind.Quest ? 1.4f : 1f);
            return;
        }

        int r = MarkerPalette.Radius(kind);
        if (kind == MarkerKind.Quest)
        {
            DrawPlace(pixels, texSize, p, r, color, alpha, square: false);
            return;
        }
        if (kind is MarkerKind.Extraction or MarkerKind.ExtractionInactive)
        {
            DrawPlace(pixels, texSize, p, r, color, alpha, square: true);
            return;
        }
        if (kind == MarkerKind.EnemyQuestTarget)
        {
            // Red dot, a dark gap, then a 1 px cyan ring: "this one counts for your quest".
            for (int dy = -r - 2; dy <= r + 2; dy++)
            for (int dx = -r - 2; dx <= r + 2; dx++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 <= r * r) Blend(pixels, texSize, p.Px + dx, p.Py + dy, color, alpha);
                else if (d2 <= (r + 1) * (r + 1)) Blend(pixels, texSize, p.Px + dx, p.Py + dy, MarkerPalette.Outline, alpha);
                else if (d2 <= (r + 2) * (r + 2)) Blend(pixels, texSize, p.Px + dx, p.Py + dy, MarkerPalette.Quest, alpha);
            }
            return;
        }
        int outer = r >= 2 ? r + 1 : r;
        for (int dy = -outer; dy <= outer; dy++)
        for (int dx = -outer; dx <= outer; dx++)
        {
            int d2 = dx * dx + dy * dy;
            if (d2 <= r * r) Blend(pixels, texSize, p.Px + dx, p.Py + dy, color, alpha);
            else if (d2 <= outer * outer) Blend(pixels, texSize, p.Px + dx, p.Py + dy, MarkerPalette.Outline, alpha);
        }
    }

    /// <summary>
    /// A place: filled diamond (quests) or square (extraction) with a 1 px outline, plus a short tick above
    /// or below when it is on another floor.
    /// </summary>
    private static void DrawPlace(Span<Rgba> pixels, int texSize, in ProjectedMarker p, int r, Rgba color, float alpha, bool square)
    {
        for (int dy = -r - 1; dy <= r + 1; dy++)
        for (int dx = -r - 1; dx <= r + 1; dx++)
        {
            int d = square ? Math.Max(Math.Abs(dx), Math.Abs(dy)) : Math.Abs(dx) + Math.Abs(dy);
            if (d <= r) Blend(pixels, texSize, p.Px + dx, p.Py + dy, color, alpha);
            else if (d == r + 1) Blend(pixels, texSize, p.Px + dx, p.Py + dy, MarkerPalette.Outline, alpha);
        }
        if (p.Vertical == 0) return;
        int dir = p.Vertical > 0 ? 1 : -1;
        for (int t = r + 2; t <= r + 3; t++)
        for (int dx = -1; dx <= 1; dx++)
            Blend(pixels, texSize, p.Px + dx, p.Py + dir * t, color, alpha);
    }

    private static void DrawArrow(Span<Rgba> pixels, int texSize, int cx, int cy, float angleDeg, Rgba color, float alpha, float size)
    {
        float a = angleDeg * (MathF.PI / 180f);
        float dirX = MathF.Sin(a), dirY = MathF.Cos(a);   // 0 = up (+y), 90 = right (+x)
        float perpX = dirY, perpY = -dirX;
        // Triangle: tip 3 px ahead, base 2 px behind and 2.5 px to each side, inside a triangle 1.5 px
        // larger in the outline colour (so it reads on light walls as well as dark floors).
        for (int y = cy - 7; y <= cy + 7; y++)
        for (int x = cx - 7; x <= cx + 7; x++)
        {
            if (InArrow(x, y, cx, cy, dirX, dirY, perpX, perpY, size, 0f)) Blend(pixels, texSize, x, y, color, alpha);
            else if (InArrow(x, y, cx, cy, dirX, dirY, perpX, perpY, size, 1.5f)) Blend(pixels, texSize, x, y, MarkerPalette.Outline, alpha);
        }
    }

    private static bool InArrow(float x, float y, float cx, float cy, float dirX, float dirY, float perpX, float perpY, float size, float grow)
    {
        float tip = 3f * size + grow, back = 2f * size + grow * 0.5f, side = 2.5f * size + grow;
        float tx = cx + dirX * tip, ty = cy + dirY * tip;
        float bx = cx - dirX * back, by = cy - dirY * back;
        return InTriangle(x, y, tx, ty, bx + perpX * side, by + perpY * side, bx - perpX * side, by - perpY * side);
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
