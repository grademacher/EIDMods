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
    public void ClampedTeammate_SitsOnRim_PointingTheRightWay()
    {
        // Review Focus 3: teammate 100 m to the +x side, camera looking along +z: rim arrow on the right.
        Assert.True(At().TryProject(new MapMarker(200f, 0f, 50f, MarkerKind.Teammate), true, out var p));
        Assert.True(p.OnRim);
        Assert.InRange(p.Px, 128 + 118, 128 + 124);   // 4 px inside the 128 px rim
        Assert.InRange(p.Py, 126, 130);
        Assert.InRange(p.RimAngleDeg, 89f, 91f);        // 0 = up, clockwise

        // Same teammate with the camera turned to look along +x: now straight up.
        Assert.True(At(yaw: 90f).TryProject(new MapMarker(200f, 0f, 50f, MarkerKind.Teammate), true, out var q));
        Assert.InRange(q.Py, 128 + 118, 128 + 124);
        Assert.InRange(q.RimAngleDeg, -1f, 1f);
    }

    [Fact]
    public void OtherFloor_Faded_OrHidden()
    {
        // Review Focus 2: a teammate 5 m directly above, on another floor.
        var above = new MapMarker(100f, 5f, 52f, MarkerKind.Teammate);
        Assert.True(At().TryProject(above, true, out var p));
        Assert.Equal(0.35f, p.Alpha, 3);
        Assert.False(At(hide: true).TryProject(above, true, out _));

        var sameFloor = new MapMarker(100f, 2.5f, 52f, MarkerKind.Teammate);
        Assert.True(At(hide: true).TryProject(sameFloor, true, out var s));
        Assert.Equal(1f, s.Alpha);
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
}
