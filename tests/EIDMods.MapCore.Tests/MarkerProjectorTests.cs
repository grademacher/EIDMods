using System;
using EIDMods.MapCore.Markers;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class MarkerProjectorTests
{
    private const int Tex = 256;          // centre pixel is 128
    private const float Radius = 20f;     // 20 m from centre to edge -> 6.4 px per metre

    private static MarkerProjector At(float yaw = 0f, float refY = 0f, bool hide = false) =>
        new(Tex, Radius, playerX: 100f, playerZ: 50f, yawDeg: yaw, refY: refY, floorBand: 3f, hideOtherFloors: hide);

    [Fact]
    public void MarkerAhead_IsAboveCentre_WhenYawZero()
    {
        Assert.True(At().TryProject(new MapMarker(100f, 0f, 60f, MarkerKind.Enemy), false, out var p));
        Assert.InRange(p.Px, 127, 129);
        Assert.InRange(p.Py, 128 + 62, 128 + 66); // 10 m up = 64 px (texture row 0 = bottom)
        Assert.False(p.OnRim);
        Assert.Equal(1f, p.Alpha);
    }

    [Fact]
    public void MarkerAhead_RotatesWithCameraYaw()
    {
        // Camera looking along +x (yaw 90): a marker 10 m to the +x side is straight up on the map.
        Assert.True(At(yaw: 90f).TryProject(new MapMarker(110f, 0f, 50f, MarkerKind.Enemy), false, out var p));
        Assert.InRange(p.Px, 127, 129);
        Assert.InRange(p.Py, 128 + 62, 128 + 66);
    }

    [Fact]
    public void MarkerToTheRight_IsRightOfCentre_WhenYawZero()
    {
        Assert.True(At().TryProject(new MapMarker(105f, 0f, 50f, MarkerKind.Enemy), false, out var p));
        Assert.InRange(p.Px, 128 + 30, 128 + 34);
        Assert.InRange(p.Py, 127, 129);
    }

    [Fact]
    public void OffMap_NotProjected_UnlessClamped()
    {
        var far = new MapMarker(100f, 0f, 50f + 100f, MarkerKind.Teammate);
        Assert.False(At().TryProject(far, clampToRim: false, out _));
        Assert.True(At().TryProject(far, clampToRim: true, out var p));
        Assert.True(p.OnRim);
    }

    [Fact]
    public void ClampedTeammate_SitsInsideRim_PointingTheRightWay()
    {
        // Teammate 100 m to the +x side, camera looking along +z: rim arrow on the right, kept far
        // enough inside the 128 px rim that the arrow (3 px tip) doesn't draw over it.
        Assert.True(At().TryProject(new MapMarker(200f, 0f, 50f, MarkerKind.Teammate), true, out var p));
        Assert.True(p.OnRim);
        Assert.InRange(p.Px, 128 + 116, 128 + 121);
        Assert.InRange(p.Py, 126, 130);
        Assert.InRange(p.RimAngleDeg, 89f, 91f);        // 0 = up, clockwise

        // Same teammate with the camera turned to look along +x: now straight up.
        Assert.True(At(yaw: 90f).TryProject(new MapMarker(200f, 0f, 50f, MarkerKind.Teammate), true, out var q));
        Assert.InRange(q.Py, 128 + 116, 128 + 121);
        Assert.InRange(q.RimAngleDeg, -1f, 1f);
    }

    [Fact]
    public void DotNearTheRim_IsDropped_InsteadOfDrawingOverIt()
    {
        // User feedback: dots bleeding over the rim. A radius-3 dot + outline needs ~8 px of room.
        Assert.True(At().TryProject(new MapMarker(100f, 0f, 50f + 18.0f, MarkerKind.Enemy), false, out _));   // 115 px
        Assert.False(At().TryProject(new MapMarker(100f, 0f, 50f + 18.9f, MarkerKind.Enemy), false, out _));  // 121 px
    }

    [Fact]
    public void Enemy_OtherFloor_FadedOrHidden_PerSetting()
    {
        var above = new MapMarker(100f, 5f, 52f, MarkerKind.Enemy);
        Assert.True(At().TryProject(above, false, out var p));
        Assert.Equal(0.35f, p.Alpha, 3);
        Assert.False(At(hide: true).TryProject(above, false, out _));
    }

    [Fact]
    public void Teammate_AnyFloor_AlwaysFullyVisible()
    {
        // User feedback: teammate dots/arrows always visible, no matter the floor or distance.
        var above = new MapMarker(100f, 12f, 52f, MarkerKind.TeammateDowned);
        Assert.True(At(hide: true).TryProject(above, true, out var p));
        Assert.Equal(1f, p.Alpha);
        var farBelow = new MapMarker(400f, -30f, 50f, MarkerKind.Teammate);
        Assert.True(At(hide: true).TryProject(farBelow, true, out var q));
        Assert.True(q.OnRim);
        Assert.Equal(1f, q.Alpha);
    }

    [Theory]
    [InlineData(MarkerKind.Container)]
    [InlineData(MarkerKind.CorpseLoot)]
    [InlineData(MarkerKind.FloorItem)]
    public void Loot_OtherFloor_AlwaysHidden_EvenInFadeMode(MarkerKind kind)
    {
        // User feedback: loot from another floor showed up inside walls; hide it instead of fading.
        Assert.False(At(hide: false).TryProject(new MapMarker(100f, -2.8f, 52f, kind), false, out _));
        Assert.False(At(hide: false).TryProject(new MapMarker(100f, 2.8f, 52f, kind), false, out _));
        Assert.True(At(hide: false).TryProject(new MapMarker(100f, 1.8f, 52f, kind), false, out var shelf));
        Assert.Equal(1f, shelf.Alpha);
    }

    [Fact]
    public void ScalesWithViewRadius()
    {
        var m = new MapMarker(100f, 0f, 60f, MarkerKind.Enemy);
        var zoomedOut = new MarkerProjector(Tex, 40f, 100f, 50f, 0f, 0f);
        Assert.True(zoomedOut.TryProject(m, false, out var p));
        Assert.InRange(p.Py, 128 + 30, 128 + 34); // 10 m at 3.2 px per metre
    }

    [Fact]
    public void Palette_HasTheAgreedColoursAndSizes()
    {
        Assert.Equal(new Rgba(80, 200, 90, 255), MarkerPalette.For(MarkerKind.Teammate));
        Assert.Equal(new Rgba(255, 150, 40, 255), MarkerPalette.For(MarkerKind.TeammateDowned));
        Assert.Equal(new Rgba(220, 50, 45, 255), MarkerPalette.For(MarkerKind.Enemy));
        Assert.Equal(new Rgba(240, 205, 60, 255), MarkerPalette.For(MarkerKind.Container));
        Assert.Equal(3, MarkerPalette.Radius(MarkerKind.Enemy));
        Assert.Equal(2, MarkerPalette.Radius(MarkerKind.Container));
        Assert.Equal(1, MarkerPalette.Radius(MarkerKind.FloorItem));
        Assert.True(MarkerPalette.For(MarkerKind.ContainerOpened).A < 255);
    }

    [Fact]
    public void Quest_ShowsOnAnyFloor_WithVerticalHint()
    {
        var proj = At(refY: 0f, hide: true);
        Assert.True(proj.TryProject(new MapMarker(100f, 8f, 55f, MarkerKind.Quest), false, out var above));
        Assert.Equal(1f, above.Alpha);
        Assert.Equal(1, above.Vertical);

        Assert.True(proj.TryProject(new MapMarker(100f, -8f, 55f, MarkerKind.Quest), false, out var below));
        Assert.Equal(-1, below.Vertical);

        Assert.True(proj.TryProject(new MapMarker(100f, 1f, 55f, MarkerKind.Quest), false, out var level));
        Assert.Equal(0, level.Vertical);
    }

    [Fact]
    public void QuestEnemy_FollowsEnemyFloorRules()
    {
        Assert.False(At(refY: 0f, hide: true).TryProject(new MapMarker(100f, 8f, 55f, MarkerKind.EnemyQuestTarget), false, out _));
        Assert.True(At(refY: 0f, hide: false).TryProject(new MapMarker(100f, 8f, 55f, MarkerKind.EnemyQuestTarget), false, out var p));
        Assert.True(p.Alpha < 1f);
    }

    [Fact]
    public void LabelTravelsWithMarker()
    {
        var m = new MapMarker(1f, 2f, 3f, MarkerKind.Quest, "Enter the Atrium");
        Assert.Equal("Enter the Atrium", m.Label);
        Assert.Null(new MapMarker(1f, 2f, 3f, MarkerKind.Enemy).Label);
    }
}
