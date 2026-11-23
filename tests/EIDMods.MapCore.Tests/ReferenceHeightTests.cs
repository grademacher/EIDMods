using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class ReferenceHeightTests
{
    [Fact]
    public void FirstUpdate_Sets()
    {
        var r = new ReferenceHeight();
        Assert.True(r.Update(5f));
        Assert.Equal(5f, r.Value);
    }

    [Fact]
    public void WithinHalfAMetre_EitherWay_DoesNotMove()
    {
        var r = new ReferenceHeight();
        r.Update(0f);
        Assert.False(r.Update(0.45f));
        Assert.False(r.Update(-0.45f));
        Assert.Equal(0f, r.Value);
    }

    [Fact]
    public void GoingDown_FollowsPastHalfAMetre()
    {
        var r = new ReferenceHeight();
        r.Update(0f);
        Assert.True(r.Update(-0.55f));
        Assert.Equal(-0.55f, r.Value);
    }

    [Fact]
    public void GoingUp_FollowsPastHalfAMetre()
    {
        var r = new ReferenceHeight();
        r.Update(0f);
        Assert.True(r.Update(0.55f));
        Assert.Equal(0.55f, r.Value);
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var r = new ReferenceHeight();
        r.Update(3f);
        r.Reset();
        Assert.True(float.IsNaN(r.Value));
        Assert.True(r.Update(-7f));
    }
}
