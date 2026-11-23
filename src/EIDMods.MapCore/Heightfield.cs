using System;
using System.Collections.Generic;

namespace EIDMods.MapCore;

/// <summary>
/// Collects raw spans per cell column, then <see cref="Build"/>s an immutable, compact
/// <see cref="Heightfield"/>. Heights are quantised to <c>quantum</c> metres above <c>originY</c>
/// (bottoms rounded down, tops rounded up, so spans only ever grow).
///
/// Merge rule: within a column, spans merge only with spans of the same FloorOnly-ness when they
/// overlap or are within one quantum. The merged WalkableTop comes from the higher top; tops within
/// 0.1 m OR their flags.
/// </summary>
public sealed class HeightfieldBuilder
{
    internal const int ChunkShift = 5;
    internal const int ChunkCells = 1 << ChunkShift;      // 32
    internal const int ChunkMask = ChunkCells - 1;
    internal const int ChunkArea = ChunkCells * ChunkCells;

    private readonly Dictionary<long, ChunkEntries> _chunks = new();

    public float CellSize { get; }
    public float OriginY { get; }
    public float Quantum { get; }

    public HeightfieldBuilder(float cellSize, float originY, float quantum = 0.05f)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (quantum <= 0f) throw new ArgumentOutOfRangeException(nameof(quantum));
        CellSize = cellSize;
        OriginY = originY;
        Quantum = quantum;
    }

    /// <summary>A builder pre-filled with every span of <paramref name="existing"/> (which stays unchanged).</summary>
    public static HeightfieldBuilder From(Heightfield existing)
    {
        var b = new HeightfieldBuilder(existing.CellSize, existing.OriginY, existing.Quantum);
        foreach (var (cx, cz) in existing.Columns())
            foreach (var s in existing.GetSpans(cx, cz))
                b.AddSpan(cx, cz, s.Bottom, s.Top, s.Flags);
        return b;
    }

    public void AddSpan(int cx, int cz, float bottom, float top, SpanFlags flags)
    {
        if (top < bottom) (bottom, top) = (top, bottom);
        long key = Heightfield.Key(cx >> ChunkShift, cz >> ChunkShift);
        if (!_chunks.TryGetValue(key, out var chunk)) _chunks[key] = chunk = new ChunkEntries();
        chunk.Add(new Entry(
            (ushort)(((cz & ChunkMask) << ChunkShift) | (cx & ChunkMask)),
            QuantiseDown(bottom), QuantiseUp(top), flags));
    }

    public Heightfield Build()
    {
        var built = new Dictionary<long, Heightfield.Chunk>(_chunks.Count);
        int columns = 0;
        foreach (var kv in _chunks)
        {
            var merged = kv.Value.Compacted();
            var chunk = Heightfield.Chunk.FromSortedEntries(merged, this);
            columns += chunk.NonEmptyColumns;
            built[kv.Key] = chunk;
        }
        return new Heightfield(CellSize, OriginY, Quantum, built, columns);
    }

    internal float Dequantise(ushort q) => OriginY + q * Quantum;

    private ushort QuantiseDown(float y) => Clamp(MathF.Floor((y - OriginY) / Quantum + 1e-3f));
    private ushort QuantiseUp(float y) => Clamp(MathF.Ceiling((y - OriginY) / Quantum - 1e-3f));
    private static ushort Clamp(float q) => (ushort)Math.Clamp(q, 0f, ushort.MaxValue);

    // ---- raw entries ---------------------------------------------------------------------------

    internal readonly struct Entry
    {
        public readonly ushort Local;
        public readonly ushort Bottom;
        public readonly ushort Top;
        public readonly SpanFlags Flags;

        public Entry(ushort local, ushort bottom, ushort top, SpanFlags flags)
        {
            Local = local; Bottom = bottom; Top = top; Flags = flags;
        }

        public bool FloorOnly => (Flags & SpanFlags.FloorOnly) != 0;
    }

    /// <summary>Raw entries of one chunk; compacted (sorted + merged) whenever they pile up.</summary>
    private sealed class ChunkEntries
    {
        private List<Entry> _entries = new();
        private int _compactedCount;

        public void Add(Entry e)
        {
            _entries.Add(e);
            if (_entries.Count > 2 * _compactedCount + 4096) Compact();
        }

        public List<Entry> Compacted()
        {
            Compact();
            return _entries;
        }

        private void Compact()
        {
            _entries.Sort(static (a, b) =>
            {
                int c = a.Local.CompareTo(b.Local);
                if (c != 0) return c;
                c = a.FloorOnly.CompareTo(b.FloorOnly);
                return c != 0 ? c : a.Bottom.CompareTo(b.Bottom);
            });

            var merged = new List<Entry>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (merged.Count > 0)
                {
                    var last = merged[^1];
                    if (last.Local == e.Local && last.FloorOnly == e.FloorOnly && e.Bottom <= last.Top + 1)
                    {
                        merged[^1] = Merge(last, e);
                        continue;
                    }
                }
                merged.Add(e);
            }

            // Order each column by bottom across both kinds (sorted above by kind, then bottom).
            merged.Sort(static (a, b) =>
            {
                int c = a.Local.CompareTo(b.Local);
                return c != 0 ? c : a.Bottom.CompareTo(b.Bottom);
            });
            _entries = merged;
            _compactedCount = merged.Count;
        }

        private static Entry Merge(Entry a, Entry b)
        {
            const int topTolerance = 2; // quanta (0.1 m at the default 0.05 m quantum)
            ushort top = Math.Max(a.Top, b.Top);
            bool walkA = (a.Flags & SpanFlags.WalkableTop) != 0, walkB = (b.Flags & SpanFlags.WalkableTop) != 0;
            bool walk = Math.Abs(a.Top - b.Top) <= topTolerance ? walkA || walkB : (a.Top > b.Top ? walkA : walkB);
            var flags = (a.Flags & SpanFlags.FloorOnly) | (walk ? SpanFlags.WalkableTop : SpanFlags.None);
            return new Entry(a.Local, Math.Min(a.Bottom, b.Bottom), top, flags);
        }
    }
}

/// <summary>
/// Immutable column heightfield: for each (cx, cz) cell, the solid spans sorted by bottom.
/// Stored as 32×32-cell chunks with per-column offsets into one span array per chunk.
/// </summary>
public sealed class Heightfield
{
    private readonly Dictionary<long, Chunk> _chunks;
    private long _cachedKey = long.MinValue;
    private Chunk _cachedChunk;

    public float CellSize { get; }
    public float OriginY { get; }
    public float Quantum { get; }
    /// <summary>Number of columns that have at least one span.</summary>
    public int ColumnCount { get; }

    internal Heightfield(float cellSize, float originY, float quantum, Dictionary<long, Chunk> chunks, int columnCount)
    {
        CellSize = cellSize;
        OriginY = originY;
        Quantum = quantum;
        _chunks = chunks;
        ColumnCount = columnCount;
    }

    /// <summary>Spans of one column, sorted by Bottom; empty if nothing is there.</summary>
    public ReadOnlySpan<HeightSpan> GetSpans(int cx, int cz)
    {
        long key = Key(cx >> HeightfieldBuilder.ChunkShift, cz >> HeightfieldBuilder.ChunkShift);
        Chunk chunk;
        if (key == _cachedKey) chunk = _cachedChunk;
        else
        {
            if (!_chunks.TryGetValue(key, out chunk)) return ReadOnlySpan<HeightSpan>.Empty;
            _cachedKey = key;
            _cachedChunk = chunk;
        }
        int local = ((cz & HeightfieldBuilder.ChunkMask) << HeightfieldBuilder.ChunkShift) | (cx & HeightfieldBuilder.ChunkMask);
        return chunk.Column(local);
    }

    /// <summary>Every column that has at least one span (unordered).</summary>
    public IEnumerable<(int cx, int cz)> Columns()
    {
        foreach (var kv in _chunks)
        {
            int chx = (int)(kv.Key >> 32), chz = (int)(uint)kv.Key;
            var chunk = kv.Value;
            for (int local = 0; local < HeightfieldBuilder.ChunkArea; local++)
            {
                if (chunk.Count(local) == 0) continue;
                yield return ((chx << HeightfieldBuilder.ChunkShift) | (local & HeightfieldBuilder.ChunkMask),
                              (chz << HeightfieldBuilder.ChunkShift) | (local >> HeightfieldBuilder.ChunkShift));
            }
        }
    }

    internal static long Key(int chx, int chz) => ((long)chx << 32) | (uint)chz;

    internal sealed class Chunk
    {
        private readonly HeightSpan[] _spans;
        private readonly int[] _start;   // ChunkArea + 1 offsets into _spans
        public int NonEmptyColumns { get; }

        private Chunk(HeightSpan[] spans, int[] start, int nonEmpty)
        {
            _spans = spans; _start = start; NonEmptyColumns = nonEmpty;
        }

        public ReadOnlySpan<HeightSpan> Column(int local) =>
            new(_spans, _start[local], _start[local + 1] - _start[local]);

        public int Count(int local) => _start[local + 1] - _start[local];

        /// <summary>Entries must be sorted by Local, then Bottom.</summary>
        public static Chunk FromSortedEntries(List<HeightfieldBuilder.Entry> entries, HeightfieldBuilder b)
        {
            var spans = new HeightSpan[entries.Count];
            var start = new int[HeightfieldBuilder.ChunkArea + 1];
            int nonEmpty = 0, prevLocal = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                spans[i] = new HeightSpan(b.Dequantise(e.Bottom), b.Dequantise(e.Top), e.Flags);
                start[e.Local + 1]++;
                if (e.Local != prevLocal) { nonEmpty++; prevLocal = e.Local; }
            }
            for (int i = 0; i < HeightfieldBuilder.ChunkArea; i++) start[i + 1] += start[i];
            return new Chunk(spans, start, nonEmpty);
        }
    }
}
