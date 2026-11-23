using System;
using EIDMods.MapCore.Markers;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class MarkerStampTests
{
    private const int Tex = 32;
    private static readonly Rgba Bg = new(0, 0, 0, 255);

    private static Rgba[] Canvas()
    {
        var px = new Rgba[Tex * Tex];
        Array.Fill(px, Bg);
        return px;
    }

    private static Rgba At(Rgba[] px, int x, int y) => px[y * Tex + x];
    private static ProjectedMarker Centre(float alpha = 1f) => new(16, 16, false, 0f, alpha);

    [Fact]
    public void Enemy_DrawsRedDot_RadiusThree_WithOutline()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, Centre(), MarkerKind.Enemy);

        Assert.Equal(MarkerPalette.Enemy, At(px, 16, 16));
        Assert.Equal(MarkerPalette.Enemy, At(px, 19, 16));   // radius 3
        Assert.Equal(MarkerPalette.Outline, At(px, 20, 16)); // outline ring just outside
        Assert.Equal(Bg, At(px, 22, 16));                    // nothing beyond
    }

    [Fact]
    public void Teammate_IsGreen_Downed_IsOrange()
    {
        var a = Canvas();
        MarkerStamp.Draw(a, Tex, Centre(), MarkerKind.Teammate);
        Assert.Equal(MarkerPalette.Teammate, At(a, 16, 16));

        var b = Canvas();
        MarkerStamp.Draw(b, Tex, Centre(), MarkerKind.TeammateDowned);
        Assert.Equal(MarkerPalette.TeammateDowned, At(b, 16, 16));
    }

    [Fact]
    public void FloorItem_IsSmallYellowDot_NoOutline()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, Centre(), MarkerKind.FloorItem);
        Assert.Equal(MarkerPalette.Loot, At(px, 16, 16));
        Assert.Equal(MarkerPalette.Loot, At(px, 17, 16));
        Assert.Equal(Bg, At(px, 18, 16)); // no outline ring for radius 1
    }

    [Fact]
    public void AlphaBlend_HalfAlpha_MixesWithBackground()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, Centre(alpha: 0.5f), MarkerKind.Enemy);
        var c = At(px, 16, 16);
        Assert.InRange(c.R, 105, 115); // 220 * 0.5 over black
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void NearEdge_ClipsWithoutThrowing()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, new ProjectedMarker(0, 31, false, 0f, 1f), MarkerKind.Enemy);
        MarkerStamp.Draw(px, Tex, new ProjectedMarker(-10, 50, false, 0f, 1f), MarkerKind.Enemy);
        Assert.Equal(MarkerPalette.Enemy, At(px, 0, 31));
    }

    [Fact]
    public void RimArrow_PointsAlongAngle()
    {
        // Pointing right (90 degrees): the tip is right of centre, nothing drawn to the far left.
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, new ProjectedMarker(16, 16, true, 90f, 1f), MarkerKind.Teammate);
        Assert.Equal(MarkerPalette.Teammate, At(px, 18, 16));
        Assert.Equal(Bg, At(px, 12, 16));

        // Pointing up (0 degrees, row 0 = bottom so "up" is +y).
        var up = Canvas();
        MarkerStamp.Draw(up, Tex, new ProjectedMarker(16, 16, true, 0f, 1f), MarkerKind.Teammate);
        Assert.Equal(MarkerPalette.Teammate, At(up, 16, 18));
        Assert.Equal(Bg, At(up, 16, 12));
    }

    [Fact]
    public void BufferSizeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => MarkerStamp.Draw(new Rgba[10], Tex, Centre(), MarkerKind.Enemy));
    }
}
