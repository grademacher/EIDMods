using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class OverlayPolicyTests
{
    [Theory]
    // Door opened / wall destroyed since the bake: the live view is open where the bake is blocked.
    [InlineData(CellKind.Wall, CellKind.Floor)]
    [InlineData(CellKind.Obstacle, CellKind.Floor)]
    [InlineData(CellKind.Wall, CellKind.Void)]
    // Door closed / prop placed since the bake: the live view is blocked where the bake is open.
    [InlineData(CellKind.Floor, CellKind.Wall)]
    [InlineData(CellKind.Floor, CellKind.Obstacle)]
    [InlineData(CellKind.Floor, CellKind.StepUp)]
    public void Overrides_WhenOpenVersusBlockedDiffers(CellKind baked, CellKind live)
    {
        Assert.True(OverlayPolicy.ShouldOverride(baked, live, liveConservativelyOpen: true));
    }

    [Theory]
    [InlineData(CellKind.Wall, CellKind.Floor)]
    [InlineData(CellKind.Obstacle, CellKind.Void)]
    public void BlockedToOpen_NeedsTheConservativeCheckToBeClear(CellKind baked, CellKind live)
    {
        // The narrow (inset) box can miss a thin wall the bake has; only a fully clear cell opens it.
        Assert.False(OverlayPolicy.ShouldOverride(baked, live, liveConservativelyOpen: false));
    }

    [Fact]
    public void OpenToBlocked_DoesNotNeedTheConservativeCheck()
    {
        Assert.True(OverlayPolicy.ShouldOverride(CellKind.Floor, CellKind.Wall, liveConservativelyOpen: false));
    }

    [Theory]
    // Physics ray starting inside a solid wall reads "StepUp" (the catacomb slope bug): keep the bake.
    [InlineData(CellKind.Wall, CellKind.StepUp)]
    [InlineData(CellKind.Wall, CellKind.Obstacle)]
    [InlineData(CellKind.Obstacle, CellKind.Wall)]
    [InlineData(CellKind.StepUp, CellKind.Wall)]
    // Floor vs void/floor shades: no geometry change implied.
    [InlineData(CellKind.Floor, CellKind.Void)]
    [InlineData(CellKind.Void, CellKind.Floor)]
    [InlineData(CellKind.Floor, CellKind.Floor)]
    [InlineData(CellKind.Wall, CellKind.Wall)]
    public void KeepsBake_WhenBothBlockedOrBothOpen(CellKind baked, CellKind live)
    {
        Assert.False(OverlayPolicy.ShouldOverride(baked, live, liveConservativelyOpen: true));
    }

    [Fact]
    public void Unknown_NeverOverrides()
    {
        Assert.False(OverlayPolicy.ShouldOverride(CellKind.Wall, CellKind.Unknown, true));
        Assert.False(OverlayPolicy.ShouldOverride(CellKind.Unknown, CellKind.Floor, true));
    }
}
