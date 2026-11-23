using System;
using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class StepReachabilityTests
{
    private const CellKind F = CellKind.Floor, S = CellKind.StepUp, O = CellKind.Obstacle, W = CellKind.Wall;

    /// <summary>Rows are listed north (top) to south; index = z * width + x with z = 0 the bottom row.</summary>
    private static (CellKind[] kinds, float[] dy, int w, int h) Grid(CellKind[,] k, float[,] d)
    {
        int h = k.GetLength(0), w = k.GetLength(1);
        var kinds = new CellKind[w * h];
        var dy = new float[w * h];
        for (int row = 0; row < h; row++)
        for (int x = 0; x < w; x++)
        {
            int z = h - 1 - row;
            kinds[z * w + x] = k[row, x];
            dy[z * w + x] = d[row, x];
        }
        return (kinds, dy, w, h);
    }

    private static CellKind At(CellKind[] kinds, int w, int h, int x, int row) => kinds[(h - 1 - row) * w + x];

    [Fact]
    public void Railing_OneMetreAboveTheLanding_BecomesWall()
    {
        // Landing floor at 0, railing top at 1.0, stairwell drop at -2 on the other side.
        var (kinds, dy, w, h) = Grid(
            new[,] { { F, F, S, S, F }, { F, F, S, S, F }, { F, F, S, S, F } },
            new[,] { { 0f, 0f, 1f, 1f, -2f }, { 0f, 0f, 1f, 1f, -2f }, { 0f, 0f, 1f, 1f, -2f } });
        StepReachability.Resolve(kinds, dy, w, h, maxClimb: 0.6f);

        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(W, At(kinds, w, h, 2, row));
            Assert.Equal(W, At(kinds, w, h, 3, row));
        }
    }

    [Fact]
    public void Staircase_RisingFromTheFloor_StaysStepUp()
    {
        var (kinds, dy, w, h) = Grid(
            new[,] { { F, S, S, S, S, S } },
            new[,] { { 0f, 0.35f, 0.7f, 1.05f, 1.4f, 1.75f } });
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);

        for (int x = 1; x < 6; x++) Assert.Equal(S, At(kinds, w, h, x, 0));
    }

    [Fact]
    public void PlateauReachedByStairs_StaysStepUp_EvenItsFlatInterior()
    {
        var (kinds, dy, w, h) = Grid(
            new[,] { { F, F, S, S, S }, { F, S, S, S, S }, { F, F, S, S, S } },
            new[,] { { 0f, 0f, 1.2f, 1.2f, 1.2f }, { 0f, 0.6f, 1.2f, 1.2f, 1.2f }, { 0f, 0f, 1.2f, 1.2f, 1.2f } });
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);

        for (int row = 0; row < 3; row++)
        for (int x = 2; x < 5; x++)
            Assert.Equal(S, At(kinds, w, h, x, row));
    }

    [Fact]
    public void TableTop_WithNoWayUp_BecomesWall()
    {
        var (kinds, dy, w, h) = Grid(
            new[,] { { F, F, F, F }, { F, S, S, F }, { F, F, F, F } },
            new[,] { { 0f, 0f, 0f, 0f }, { 0f, 0.8f, 0.8f, 0f }, { 0f, 0f, 0f, 0f } });
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);

        Assert.Equal(W, At(kinds, w, h, 1, 1));
        Assert.Equal(W, At(kinds, w, h, 2, 1));
    }

    [Fact]
    public void LowLedge_WithinClimb_StaysStepUp()
    {
        var (kinds, dy, w, h) = Grid(new[,] { { F, S, S } }, new[,] { { 0f, 0.55f, 0.55f } });
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);
        Assert.Equal(S, At(kinds, w, h, 1, 0));
        Assert.Equal(S, At(kinds, w, h, 2, 0));
    }

    [Fact]
    public void ReachableDiagonally()
    {
        var (kinds, dy, w, h) = Grid(new[,] { { F, W }, { W, S } }, new[,] { { 0f, 0f }, { 0f, 0.4f } });
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);
        Assert.Equal(S, At(kinds, w, h, 1, 1));
    }

    [Fact]
    public void OtherKinds_AreNeverChanged()
    {
        var (kinds, dy, w, h) = Grid(new[,] { { F, W, O, CellKind.Void, CellKind.Unknown } }, new float[1, 5]);
        var before = (CellKind[])kinds.Clone();
        StepReachability.Resolve(kinds, dy, w, h, 0.6f);
        Assert.Equal(before, kinds);
    }

    [Fact]
    public void WrongSizes_Throw()
    {
        Assert.Throws<ArgumentException>(() => StepReachability.Resolve(new CellKind[4], new float[3], 2, 2, 0.6f));
    }
}
