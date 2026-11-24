using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class FloorLevelsTests
{
    private static void Slab(HeightfieldBuilder b, int x0, int z0, int size, float top)
    {
        for (int z = z0; z < z0 + size; z++)
        for (int x = x0; x < x0 + size; x++)
            b.AddSpan(x, z, top - 0.3f, top, SpanFlags.WalkableTop);
    }

    [Fact]
    public void TwoStackedFloors_AreBothFound_LowestFirst()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);   // ground floor
        Slab(b, 0, 0, 40, 4f);   // upper floor over the same area
        var floors = FloorLevels.Find(b.Build());

        Assert.Equal(2, floors.Count);
        Assert.InRange(floors[0], -0.1f, 0.1f);
        Assert.InRange(floors[1], 3.9f, 4.1f);
    }

    [Fact]
    public void SpanWithoutHeadroom_IsNotAFloor()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);
        Slab(b, 0, 0, 40, 1.0f); // a shelf 1 m above: the lower top has no room to stand
        var floors = FloorLevels.Find(b.Build());

        Assert.Single(floors);
        Assert.InRange(floors[0], 0.9f, 1.1f);
    }

    [Fact]
    public void Ramp_DoesNotBecomeManyFloors()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);
        Slab(b, 0, 100, 40, 6f);
        for (int z = 40; z < 100; z++)              // ramp between them, 0.1 m per cell
            for (int x = 0; x < 8; x++)
                b.AddSpan(x, z, (z - 40) * 0.1f - 0.3f, (z - 40) * 0.1f, SpanFlags.WalkableTop);
        var floors = FloorLevels.Find(b.Build());

        Assert.Equal(2, floors.Count);
    }

    [Fact]
    public void FloorsCloserThanSeparation_Merge_IntoTheBusierOne()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);
        Slab(b, 100, 0, 10, 1.5f); // small raised area, 1.5 m up: same storey
        var floors = FloorLevels.Find(b.Build());

        Assert.Single(floors);
        Assert.InRange(floors[0], -0.1f, 0.1f);
    }

    [Fact]
    public void Step_GoesToNextFloor_OrStays()
    {
        var floors = new System.Collections.Generic.List<float> { 0f, 4f, 9f };
        Assert.Equal(4f, FloorLevels.Step(floors, 0.2f, +1));
        Assert.Equal(9f, FloorLevels.Step(floors, 4f, +1));
        Assert.Equal(9f, FloorLevels.Step(floors, 9f, +1));   // already top
        Assert.Equal(4f, FloorLevels.Step(floors, 6f, -1));   // between floors: next one down
        Assert.Equal(0f, FloorLevels.Step(floors, -3f, -1));  // below all: lowest
    }

    [Fact]
    public void NonWalkableTops_AreIgnored()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);
        for (int z = 0; z < 40; z++)          // sloped roof tops over another area: not walkable
            for (int x = 100; x < 140; x++)
                b.AddSpan(x, z, 7.7f, 8f, SpanFlags.None);
        var floors = FloorLevels.Find(b.Build());

        Assert.Single(floors);
    }

    [Fact]
    public void Area_OnlyCountsSurfacesNearTheGivenPoint()
    {
        var b = new HeightfieldBuilder(0.5f, -1000f);
        Slab(b, 0, 0, 40, 0f);          // around (10 m, 10 m)
        Slab(b, 1000, 0, 40, 70f);      // 500 m away, high up
        var hf = b.Build();

        Assert.Equal(2, FloorLevels.Find(hf).Count);
        var near = FloorLevels.Find(hf, centerX: 10f, centerZ: 10f, radius: 120f);
        Assert.Single(near);
        Assert.InRange(near[0], -0.1f, 0.1f);
    }
}
