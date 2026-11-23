using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class RoundRobinTests
{
    [Fact]
    public void Next_WalksTheList_AndReportsTheEndOfEachPass()
    {
        // Final review, Important 1: LootMarkers' "full pass done" check could never fire, so gone
        // loot was never compacted.
        int cursor = 0;
        Assert.Equal(0, RoundRobin.Next(ref cursor, 3, out bool p0)); Assert.False(p0);
        Assert.Equal(1, RoundRobin.Next(ref cursor, 3, out bool p1)); Assert.False(p1);
        Assert.Equal(2, RoundRobin.Next(ref cursor, 3, out bool p2)); Assert.True(p2);
        Assert.Equal(0, RoundRobin.Next(ref cursor, 3, out bool p3)); Assert.False(p3);
    }

    [Fact]
    public void Next_ListShrankBelowCursor_StartsOver()
    {
        int cursor = 7;
        Assert.Equal(0, RoundRobin.Next(ref cursor, 2, out bool pass));
        Assert.False(pass);
    }

    [Fact]
    public void Next_SingleItem_EveryCallCompletesAPass()
    {
        int cursor = 0;
        Assert.Equal(0, RoundRobin.Next(ref cursor, 1, out bool a)); Assert.True(a);
        Assert.Equal(0, RoundRobin.Next(ref cursor, 1, out bool b)); Assert.True(b);
    }
}
