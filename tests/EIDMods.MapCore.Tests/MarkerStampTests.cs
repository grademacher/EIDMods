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

    [Fact]
    public void Quest_DrawsCyanDiamond_WithOutline()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, Centre(), MarkerKind.Quest);

        Assert.Equal(MarkerPalette.Quest, At(px, 16, 16));
        Assert.Equal(MarkerPalette.Quest, At(px, 20, 16));   // diamond tip, radius 4
        Assert.Equal(MarkerPalette.Quest, At(px, 16, 20));
        Assert.NotEqual(MarkerPalette.Quest, At(px, 19, 19)); // corner of the square is not part of a diamond
        Assert.Equal(MarkerPalette.Outline, At(px, 21, 16)); // outline just past the tip
    }

    [Fact]
    public void Quest_AboveYou_GetsTickOnTop_BelowYou_TickUnderneath()
    {
        var up = Canvas();
        MarkerStamp.Draw(up, Tex, new ProjectedMarker(16, 16, false, 0f, 1f, Vertical: 1), MarkerKind.Quest);
        Assert.Equal(MarkerPalette.Quest, At(up, 16, 23));   // tick above the diamond (row 0 = bottom)
        Assert.Equal(Bg, At(up, 16, 9));

        var down = Canvas();
        MarkerStamp.Draw(down, Tex, new ProjectedMarker(16, 16, false, 0f, 1f, Vertical: -1), MarkerKind.Quest);
        Assert.Equal(MarkerPalette.Quest, At(down, 16, 9));
        Assert.Equal(Bg, At(down, 16, 23));
    }

    [Fact]
    public void QuestEnemy_IsRedDot_WithCyanRing()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, Centre(), MarkerKind.EnemyQuestTarget);

        Assert.Equal(MarkerPalette.Enemy, At(px, 16, 16));
        Assert.Equal(MarkerPalette.Enemy, At(px, 19, 16));
        Assert.Equal(MarkerPalette.Quest, At(px, 21, 16));   // cyan ring around the dot
        Assert.Equal(Bg, At(px, 24, 16));
    }

    [Fact]
    public void RimArrow_HasDarkOutline_SoItReadsOnLightWalls()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, new ProjectedMarker(16, 16, true, 0f, 1f), MarkerKind.Teammate);
        Assert.Equal(MarkerPalette.Teammate, At(px, 16, 18)); // body of the arrow (pointing up)
        Assert.Equal(MarkerPalette.Outline, At(px, 16, 20));  // dark rim just past the tip
    }

    [Fact]
    public void QuestRimArrow_IsLargerThanTeammateArrow()
    {
        var px = Canvas();
        MarkerStamp.Draw(px, Tex, new ProjectedMarker(16, 16, true, 0f, 1f), MarkerKind.Quest);
        Assert.Equal(MarkerPalette.Quest, At(px, 16, 20));   // reaches where a teammate arrow has only outline
        Assert.Equal(MarkerPalette.Outline, At(px, 16, 21));
    }
}
