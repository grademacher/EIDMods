using System.Linq;
using System.Threading.Tasks;
using System;
using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class HeightfieldTests
{
    private const float Tol = 0.05f;

    private static HeightfieldBuilder NewBuilder() => new(cellSize: 0.5f, originY: -100f);

    private static void AssertSpan(HeightSpan s, float bottom, float top, SpanFlags flags)
    {
        Assert.InRange(s.Bottom, bottom - Tol, bottom + Tol);
        Assert.InRange(s.Top, top - Tol, top + Tol);
        Assert.Equal(flags, s.Flags);
    }

    [Fact]
    public void AddSpan_SingleSpan_RoundTrips()
    {
        var b = NewBuilder();
        b.AddSpan(3, -4, 1.0f, 2.0f, SpanFlags.WalkableTop);
        var hf = b.Build();

        var spans = hf.GetSpans(3, -4).ToArray();
        Assert.Single(spans);
        AssertSpan(spans[0], 1.0f, 2.0f, SpanFlags.WalkableTop);
    }

    [Fact]
    public void Build_MergesOverlappingSpans()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.None);
        b.AddSpan(0, 0, 0.9f, 3f, SpanFlags.None);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Single(spans);
        AssertSpan(spans[0], 0f, 3f, SpanFlags.None);
    }

    [Fact]
    public void Build_MergesTouchingSpans_WithinQuantum()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.None);
        b.AddSpan(0, 0, 1.04f, 2f, SpanFlags.None);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Single(spans);
        AssertSpan(spans[0], 0f, 2f, SpanFlags.None);
    }

    [Fact]
    public void Build_KeepsSeparateSpans_SortedByBottom()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 5f, 6f, SpanFlags.None);
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.None);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Equal(2, spans.Length);
        AssertSpan(spans[0], 0f, 1f, SpanFlags.None);
        AssertSpan(spans[1], 5f, 6f, SpanFlags.None);
    }

    [Fact]
    public void Build_WalkableFromHigherTop()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 3f, SpanFlags.None);
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.WalkableTop);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Single(spans);
        AssertSpan(spans[0], 0f, 3f, SpanFlags.None);
    }

    [Fact]
    public void Build_WalkableOred_WhenTopsWithinTenCentimetres()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 2.0f, SpanFlags.None);
        b.AddSpan(0, 0, 1f, 2.05f, SpanFlags.WalkableTop);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Single(spans);
        Assert.Equal(SpanFlags.WalkableTop, spans[0].Flags);
    }

    [Fact]
    public void Build_DoesNotMergeFloorOnlyWithSolid()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, -1f, 0f, SpanFlags.None);
        b.AddSpan(0, 0, 0f, 1.2f, SpanFlags.FloorOnly);
        var spans = b.Build().GetSpans(0, 0).ToArray();

        Assert.Equal(2, spans.Length);
        AssertSpan(spans[0], -1f, 0f, SpanFlags.None);
        AssertSpan(spans[1], 0f, 1.2f, SpanFlags.FloorOnly);
    }

    [Fact]
    public void GetSpans_EmptyColumn_IsEmpty()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.None);
        var hf = b.Build();

        Assert.Equal(0, hf.GetSpans(1, 0).Length);
        Assert.Equal(0, hf.GetSpans(-1, -1).Length);
        Assert.Equal(0, hf.GetSpans(-5000, 7000).Length);
    }

    [Fact]
    public void Columns_ListsEveryColumnWithSpans()
    {
        var b = NewBuilder();
        b.AddSpan(0, 0, 0f, 1f, SpanFlags.None);
        b.AddSpan(-33, 40, 0f, 1f, SpanFlags.None);
        b.AddSpan(-33, 40, 5f, 6f, SpanFlags.None);
        var hf = b.Build();

        Assert.Equal(2, hf.ColumnCount);
        Assert.Equal(new[] { (-33, 40), (0, 0) }, hf.Columns().OrderBy(c => c.cx).ToArray());
    }

    [Fact]
    public void From_ExistingHeightfield_KeepsSpansAndAcceptsNew()
    {
        var b = NewBuilder();
        b.AddSpan(2, 2, 0f, 1f, SpanFlags.WalkableTop);
        var first = b.Build();

        var b2 = HeightfieldBuilder.From(first);
        b2.AddSpan(2, 2, 0.5f, 4f, SpanFlags.None);
        b2.AddSpan(9, 9, 0f, 1f, SpanFlags.None);
        var second = b2.Build();

        Assert.Equal(first.CellSize, second.CellSize);
        Assert.Equal(first.OriginY, second.OriginY);
        var merged = second.GetSpans(2, 2).ToArray();
        Assert.Single(merged);
        AssertSpan(merged[0], 0f, 4f, SpanFlags.None);
        Assert.Equal(1, second.GetSpans(9, 9).Length);
        // The original is immutable.
        AssertSpan(first.GetSpans(2, 2)[0], 0f, 1f, SpanFlags.WalkableTop);
    }

    [Fact]
    public void GetSpans_ConcurrentReadersOnDifferentChunks_SeeTheirOwnSpans()
    {
        // Final review, Important 1: the main thread classifies while a background task writes the
        // cache / builds an incremental bake from the same Heightfield.
        var b = NewBuilder();
        b.AddSpan(0, 0, 1f, 2f, SpanFlags.None);    // chunk (0,0)
        b.AddSpan(32, 0, 5f, 6f, SpanFlags.None);   // chunk (1,0), same local column
        var hf = b.Build();

        int bad = 0;
        void Reader(int cx, float expected)
        {
            for (int i = 0; i < 3_000_000; i++)
            {
                var s = hf.GetSpans(cx, 0);
                if (s.Length != 1 || Math.Abs(s[0].Bottom - expected) > 0.1f) System.Threading.Interlocked.Increment(ref bad);
            }
        }
        Parallel.Invoke(() => Reader(0, 1f), () => Reader(32, 5f), () => Reader(0, 1f), () => Reader(32, 5f));
        Assert.Equal(0, bad);
    }
}
