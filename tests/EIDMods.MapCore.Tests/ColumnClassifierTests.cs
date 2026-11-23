using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class ColumnClassifierTests
{
    private const float Tol = 0.06f;

    private sealed class FixedTerrain : IHeightSampler
    {
        private readonly float _h;
        public FixedTerrain(float h) => _h = h;
        public bool TrySample(float x, float z, out float height) { height = _h; return true; }
    }

    /// <summary>Heightfield with the given spans in cell (0,0).</summary>
    private static ColumnClassifier Column(IHeightSampler terrain, params (float b, float t, SpanFlags f)[] spans)
    {
        var builder = new HeightfieldBuilder(0.5f, -100f);
        foreach (var (b, t, f) in spans) builder.AddSpan(0, 0, b, t, f);
        return new ColumnClassifier(builder.Build(), terrain);
    }

    private static ColumnClassifier Column(params (float b, float t, SpanFlags f)[] spans) => Column(null, spans);

    [Fact]
    public void Floor_AtLevel()
    {
        var k = Column((-1f, 0f, SpanFlags.WalkableTop)).Classify(0, 0, 0f, out var dy);
        Assert.Equal(CellKind.Floor, k);
        Assert.InRange(dy, -Tol, Tol);
    }

    [Fact]
    public void TallWall()
    {
        Assert.Equal(CellKind.Wall, Column((0f, 4f, SpanFlags.None)).Classify(0, 0, 0f, out _));
    }

    [Fact]
    public void WaistHighCrate_IsObstacle()
    {
        Assert.Equal(CellKind.Obstacle, Column((0f, 1.2f, SpanFlags.None)).Classify(0, 0, 0f, out _));
    }

    [Fact]
    public void Ledge_IsStepUp()
    {
        var k = Column((0f, 1.0f, SpanFlags.WalkableTop)).Classify(0, 0, 0f, out var dy);
        Assert.Equal(CellKind.StepUp, k);
        Assert.InRange(dy, 1f - Tol, 1f + Tol);
    }

    [Fact]
    public void RayStartsInsideTallWall_IsWall()
    {
        Assert.Equal(CellKind.Wall, Column((0f, 5f, SpanFlags.WalkableTop)).Classify(0, 0, 0f, out _));
    }

    [Fact]
    public void LowWalkableTopBelowKnee_ButBodyBlockedAbove_IsNotStepUp()
    {
        // Body blocked by an overhang (1.0..1.4, no head) above floor; the top below 2.6 is the
        // overhang's own top (non-walkable) -> Obstacle.
        var k = Column((-1f, 0f, SpanFlags.WalkableTop), (1.0f, 1.4f, SpanFlags.None)).Classify(0, 0, 0f, out _);
        Assert.Equal(CellKind.Obstacle, k);
    }

    [Fact]
    public void SmallLayerRock_NeverSolid()
    {
        var k = Column((-1f, 0f, SpanFlags.WalkableTop), (0f, 1.2f, SpanFlags.FloorOnly)).Classify(0, 0, 0f, out var dy);
        Assert.Equal(CellKind.Floor, k);
        Assert.InRange(dy, -Tol, Tol);
    }

    [Fact]
    public void Nothing_IsVoid()
    {
        Assert.Equal(CellKind.Void, Column().Classify(0, 0, 0f, out _));
        Assert.Equal(CellKind.Void, Column().Classify(123, -45, 0f, out _)); // column not in the heightfield
    }

    [Fact]
    public void FloorBeyondRayLength_IsVoid()
    {
        Assert.Equal(CellKind.Void, Column((-20f, -12f, SpanFlags.WalkableTop)).Classify(0, 0, 0f, out _));
    }

    [Fact]
    public void Drop_Reported()
    {
        var k = Column((-3f, -2f, SpanFlags.WalkableTop)).Classify(0, 0, 0f, out var dy);
        Assert.Equal(CellKind.Floor, k);
        Assert.InRange(dy, -2f - Tol, -2f + Tol);
    }

    [Fact]
    public void Terrain_UsedWhenNoSpans()
    {
        var low = Column(new FixedTerrain(0.2f)).Classify(0, 0, 0f, out var dyLow);
        Assert.Equal(CellKind.Floor, low);
        Assert.InRange(dyLow, 0.2f - Tol, 0.2f + Tol);

        var high = Column(new FixedTerrain(1.0f)).Classify(0, 0, 0f, out var dyHigh);
        Assert.Equal(CellKind.StepUp, high);
        Assert.InRange(dyHigh, 1.0f - Tol, 1.0f + Tol);
    }

    [Fact]
    public void Terrain_HillAboveHead_IsWall()
    {
        Assert.Equal(CellKind.Wall, Column(new FixedTerrain(4f)).Classify(0, 0, 0f, out _));
    }

    [Fact]
    public void StandingOnWagon_GroundBelowReadsAsDrop()
    {
        // Review Focus 2: refY on top of a 1.5 m wagon; the neighbouring cell only has ground.
        var k = Column((-1f, 0f, SpanFlags.WalkableTop)).Classify(0, 0, 1.5f, out var dy);
        Assert.Equal(CellKind.Floor, k);
        Assert.InRange(dy, -1.5f - Tol, -1.5f + Tol);
    }
}
