using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class DespeckleTests
{
    private const CellKind F = CellKind.Floor, W = CellKind.Wall, U = CellKind.Unknown, S = CellKind.StepUp;

    [Fact]
    public void LoneWall_InFloor_BecomesFloor()
    {
        Assert.Equal(F, Despeckle.Filter(W, new[] { F, F, F, F, F, F, F, F }));
    }

    [Fact]
    public void OneCellWideWallLine_Preserved()
    {
        // Neighbours in order NW, N, NE, W, E, SW, S, SE; the line runs N-S through the cell.
        Assert.Equal(W, Despeckle.Filter(W, new[] { F, W, F, F, F, F, W, F }));
    }

    [Fact]
    public void FewKnownNeighbours_Unchanged()
    {
        Assert.Equal(W, Despeckle.Filter(W, new[] { F, F, F, U, U, U, U, U }));
    }

    [Fact]
    public void Floor_Pinhole_InWall_BecomesWall()
    {
        Assert.Equal(W, Despeckle.Filter(F, new[] { W, W, W, W, W, W, W, W }));
    }

    [Fact]
    public void Speck_TakesMostCommonNeighbour()
    {
        Assert.Equal(S, Despeckle.Filter(F, new[] { S, S, S, W, W, F, U, U }));
    }

    [Fact]
    public void Tie_GoesToLowestKind()
    {
        // Floor(1) and Wall(4) both appear twice among the neighbours; self (Obstacle) appears once.
        Assert.Equal(F, Despeckle.Filter(CellKind.Obstacle, new[] { F, F, W, W, CellKind.Obstacle, U, U, U }));
    }
}
