using System;
using System.IO;
using System.Linq;
using EIDMods.MapCore;
using Xunit;

namespace EIDMods.MapCore.Tests;

public class HeightfieldCacheTests
{
    private const string Key = "TheMoor_1.0.12_42fd8278";

    private static Heightfield Sample()
    {
        var b = new HeightfieldBuilder(0.5f, -20f);
        b.AddSpan(0, 0, -1f, 0f, SpanFlags.WalkableTop);
        b.AddSpan(0, 0, 0f, 1.2f, SpanFlags.FloorOnly);
        b.AddSpan(-417, 415, 3f, 7.5f, SpanFlags.None);
        b.AddSpan(1000, -2000, 10f, 10f, SpanFlags.WalkableTop);
        return b.Build();
    }

    private static CacheHeader Header(int version = HeightfieldCache.CurrentFormat, string key = Key) =>
        new(version, key, 0.5f, new ulong[] { 1, 0xDEADBEEFCAFEUL, ulong.MaxValue });

    private static byte[] Written(Heightfield hf, CacheHeader header)
    {
        using var ms = new MemoryStream();
        HeightfieldCache.Write(ms, hf, header);
        return ms.ToArray();
    }

    [Fact]
    public void RoundTrip_PreservesSpansAndSignatures()
    {
        var original = Sample();
        var bytes = Written(original, Header());

        Assert.True(HeightfieldCache.TryRead(new MemoryStream(bytes), Key, 0.5f, out var hf, out var header));
        Assert.Equal(HeightfieldCache.CurrentFormat, header.FormatVersion);
        Assert.Equal(Key, header.MapKey);
        Assert.Equal(new ulong[] { 1, 0xDEADBEEFCAFEUL, ulong.MaxValue }, header.ColliderSignatures.OrderBy(x => x).ToArray());
        Assert.Equal(original.ColumnCount, hf.ColumnCount);
        Assert.Equal(original.OriginY, hf.OriginY);
        foreach (var (cx, cz) in original.Columns())
            Assert.Equal(original.GetSpans(cx, cz).ToArray(), hf.GetSpans(cx, cz).ToArray());
    }

    [Fact]
    public void TryRead_OtherMapKey_ReturnsFalse()
    {
        var bytes = Written(Sample(), Header());
        Assert.False(HeightfieldCache.TryRead(new MemoryStream(bytes), "Catacombs_1.0.12_42fd8278", 0.5f, out var hf, out _));
        Assert.Null(hf);
    }

    [Fact]
    public void TryRead_OtherCellSize_ReturnsFalse()
    {
        var bytes = Written(Sample(), Header());
        Assert.False(HeightfieldCache.TryRead(new MemoryStream(bytes), Key, 0.25f, out _, out _));
    }

    [Fact]
    public void TryRead_TruncatedFile_ReturnsFalse()
    {
        var bytes = Written(Sample(), Header());
        foreach (int len in new[] { 0, 5, bytes.Length / 2, bytes.Length - 1 })
            Assert.False(HeightfieldCache.TryRead(new MemoryStream(bytes[..len]), Key, 0.5f, out _, out _), $"length {len}");
    }

    [Fact]
    public void TryRead_Garbage_ReturnsFalse()
    {
        var rng = new Random(7);
        var garbage = new byte[4096];
        rng.NextBytes(garbage);
        Assert.False(HeightfieldCache.TryRead(new MemoryStream(garbage), Key, 0.5f, out _, out _));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void TryRead_ThreeMillionColumns_UnderTwoSeconds()
    {
        // Opt-in (EIDMODS_PERF=1): The Moor bakes to ~2.9M columns; a cache hit must be quick.
        if (Environment.GetEnvironmentVariable("EIDMODS_PERF") != "1") return;

        var b = new HeightfieldBuilder(0.5f, -1000f);
        for (int z = 0; z < 1700; z++)
        for (int x = 0; x < 1700; x++)
        {
            b.AddSpan(x, z, -1f, 0f, SpanFlags.WalkableTop);
            if ((x + z) % 7 == 0) b.AddSpan(x, z, 2f, 5f, SpanFlags.None);
        }
        var hf = b.Build();
        var bytes = Written(hf, Header());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(HeightfieldCache.TryRead(new MemoryStream(bytes), Key, 0.5f, out var read, out _));
        sw.Stop();
        Assert.Equal(hf.ColumnCount, read.ColumnCount);
        Assert.True(sw.Elapsed.TotalSeconds < 2.0, $"read took {sw.Elapsed.TotalSeconds:F2}s ({bytes.Length / 1024} KB)");
    }

    [Fact]
    public void TryRead_OldFormatVersion_ReturnsFalse()
    {
        var bytes = Written(Sample(), Header(version: HeightfieldCache.CurrentFormat - 1));
        Assert.False(HeightfieldCache.TryRead(new MemoryStream(bytes), Key, 0.5f, out _, out _));
    }
}
