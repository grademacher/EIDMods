using System.Collections.Generic;
using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class BigMapCameraTests
{
    [Fact]
    public void Drag_MovesTheMapWithTheMouse()
    {
        // 500 px panel showing 50 m each way: 5 px per metre. Dragging right 50 px shows what was 10 m to the left.
        var cam = new BigMapCamera(100f, 200f, 50f);
        cam.Drag(50f, 0f, 500f);
        Assert.Equal(90f, cam.CenterX, 3);
        Assert.Equal(200f, cam.CenterZ, 3);
        cam.Drag(0f, 25f, 500f);        // drag up (panel y grows upward): view moves south
        Assert.Equal(195f, cam.CenterZ, 3);
    }

    [Fact]
    public void Zoom_KeepsThePointUnderTheCursorStill()
    {
        var cam = new BigMapCamera(0f, 0f, 50f);
        // Cursor 100 px right of centre on a 500 px panel = 20 m east.
        var before = cam.PanelToWorld(350f, 250f, 500f);
        cam.Zoom(0.5f, 350f, 250f, 500f);
        var after = cam.PanelToWorld(350f, 250f, 500f);
        Assert.Equal(25f, cam.Radius, 3);
        Assert.Equal(before.x, after.x, 3);
        Assert.Equal(before.z, after.z, 3);
    }

    [Fact]
    public void Zoom_IsClamped()
    {
        var cam = new BigMapCamera(0f, 0f, 50f);
        cam.Zoom(0.0001f, 250f, 250f, 500f);
        Assert.Equal(BigMapCamera.MinRadius, cam.Radius);
        cam.Zoom(1e6f, 250f, 250f, 500f);
        Assert.Equal(BigMapCamera.MaxRadius, cam.Radius);
    }

    [Fact]
    public void HeightSlider_SpansTheFloorsWithMargin_AndSnapsNearAFloor()
    {
        var floors = new List<float> { 0f, 6f };
        var s = new HeightSlider(floors, currentHeight: 2f);
        Assert.Equal(-3f, s.Min, 3);
        Assert.Equal(9f, s.Max, 3);
        Assert.Equal(6f, s.HeightAt(s.FractionOf(6.3f)), 3);   // within 0.4 m: snaps to the floor
        Assert.Equal(4.5f, s.HeightAt(s.FractionOf(4.5f)), 3); // in between: free
    }

    [Fact]
    public void HeightSlider_NoFloors_CentresOnYou()
    {
        var s = new HeightSlider(new List<float>(), currentHeight: 10f);
        Assert.Equal(-10f, s.Min, 3);
        Assert.Equal(30f, s.Max, 3);
    }
}
