using System;
using System.Collections.Generic;
using System.Diagnostics;
using EIDMods.MapCore;
using UnityEngine;

namespace EIDMods.Features.Minimap;

/// <summary>Map colours by cell kind (shared by the baked view and the physics overlay/fallback).</summary>
internal static class MapPalette
{
    public static readonly Color32 Wall = new(222, 212, 192, 255);
    public static readonly Color32 Obstacle = new(138, 128, 112, 240);
    public static readonly Color32 FloorLevel = new(78, 72, 64, 235);
    public static readonly Color32 FloorUp = new(118, 110, 97, 240);
    public static readonly Color32 FloorDown = new(40, 37, 34, 235);
    public static readonly Color32 Void = new(8, 8, 10, 225);

    public static Color32 ColorFor(CellKind kind, float dy)
    {
        switch (kind)
        {
            case CellKind.Unknown: return default;
            case CellKind.Wall: return Wall;
            case CellKind.Obstacle: return Obstacle;
            case CellKind.StepUp: return FloorUp;
            case CellKind.Void: return Void;
        }
        // Floor: one flat colour within ±0.5 m (uneven ground banded into noise otherwise), then shade
        // by height; a drop of more than 1.5 m reads as a lower floor.
        if (dy > ClassifierBands.FlatFloor) return Lerp(FloorLevel, FloorUp, (dy - 0.5f) / 0.5f);
        if (dy >= -ClassifierBands.FlatFloor) return FloorLevel;
        if (dy > ClassifierBands.Drop) return Lerp(FloorLevel, FloorDown, (-dy - 0.5f) * 0.5f);
        return Lerp(FloorDown, Void, (dy - ClassifierBands.Drop) / (-8f - ClassifierBands.Drop));
    }

    /// <summary>Managed lerp (Color32.Lerp / Mathf.Clamp01 are IL2CPP interop calls; this runs per cell).</summary>
    private static Color32 Lerp(Color32 a, Color32 b, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return new Color32((byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t),
                           (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));
    }
}

/// <summary>
/// The height cells are classified against: follows the player's feet, but only once they've
/// climbed/dropped more than 1 m (classifying against the raw feet height made borderline cells
/// flip between wall and floor as the player's height bobbed while walking).
/// </summary>
internal sealed class ReferenceHeight
{
    private const float Hysteresis = 1.0f;
    public float Value { get; private set; } = float.NaN;

    /// <summary>Returns true when the reference moved (new floor).</summary>
    public bool Update(float feetY)
    {
        if (!float.IsNaN(Value) && Mathf.Abs(feetY - Value) <= Hysteresis) return false;
        Value = feetY;
        return true;
    }

    public void Reset() => Value = float.NaN;
}

/// <summary>Which cells the player has been near this visit (survives re-bakes of the same level).</summary>
internal sealed class RevealMask
{
    private readonly Dictionary<long, ulong[]> _chunks = new();
    private readonly float _cellSize;
    private Vector2Int _lastCell = new(int.MinValue, int.MinValue);

    public RevealMask(float cellSize) => _cellSize = cellSize;

    /// <summary>Marks every cell within <paramref name="radius"/> metres; cheap no-op until the player changes cell.</summary>
    public void Reveal(Vector3 pos, float radius)
    {
        var cell = new Vector2Int(Mathf.FloorToInt(pos.x / _cellSize), Mathf.FloorToInt(pos.z / _cellSize));
        if (cell == _lastCell) return;
        _lastCell = cell;

        int r = Mathf.CeilToInt(radius / _cellSize);
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dz * dz > r * r) continue;
            int cx = cell.x + dx, cz = cell.y + dz;
            long key = ((long)(cx >> 5) << 32) | (uint)(cz >> 5);
            if (!_chunks.TryGetValue(key, out var bits)) { _chunks[key] = bits = new ulong[16]; _lastKey = long.MinValue; }
            int i = ((cz & 31) << 5) | (cx & 31);
            bits[i >> 6] |= 1UL << (i & 63);
        }
    }

    private long _lastKey = long.MinValue;
    private ulong[] _lastBits;

    public bool IsRevealed(int cx, int cz)
    {
        long key = ((long)(cx >> 5) << 32) | (uint)(cz >> 5);
        ulong[] bits;
        if (key == _lastKey) bits = _lastBits;
        else
        {
            _chunks.TryGetValue(key, out bits);
            _lastKey = key;
            _lastBits = bits;
        }
        if (bits == null) return false;
        int i = ((cz & 31) << 5) | (cx & 31);
        return (bits[i >> 6] & (1UL << (i & 63))) != 0;
    }
}

/// <summary>
/// Draws the baked <see cref="Heightfield"/>: cells are classified (ColumnClassifier) against the
/// current reference height, despeckled, and coloured, lazily per 32×32 chunk within a per-frame
/// time budget, nearest first. When the reference height changes, the old images stay up until
/// their replacements are built (no flashing). The physics overlay corrects cells that changed
/// since the bake (doors, destruction).
/// </summary>
internal sealed class BakedMapView
{
    private const int ChunkArea = 1024;

    private sealed class ViewChunk
    {
        public readonly CellKind[] Kind = new CellKind[ChunkArea];
        public readonly float[] Dy = new float[ChunkArea];
        public readonly Color32[] Display = new Color32[ChunkArea];
        public float KindRefY = float.NaN;    // reference height Kind/Dy were classified for
        public float DisplayRefY = float.NaN; // reference height Display shows
        public bool DisplayDirty;
    }

    private readonly ColumnClassifier _classifier;
    private readonly Dictionary<long, ViewChunk> _chunks = new();
    private readonly Dictionary<long, (CellKind kind, float dy)> _overrides = new();
    private float _refY = float.NaN;

    public float CellSize { get; }
    public RevealMask Reveal { get; set; }
    public bool ShowAll { get; set; }

    public BakedMapView(Heightfield hf, IHeightSampler terrain, float cellSize)
    {
        _classifier = new ColumnClassifier(hf, terrain);
        CellSize = cellSize;
    }

    public void SetReference(float refY)
    {
        if (refY == _refY) return;
        _refY = refY;
        _overrides.Clear(); // overlay values were relative to the old reference
    }

    /// <summary>Display colour of a cell; default (a == 0) if not built yet or not revealed.</summary>
    public Color32 Get(int cx, int cz)
    {
        if (!ShowAll && (Reveal == null || !Reveal.IsRevealed(cx, cz))) return default;
        long key = Key(cx >> 5, cz >> 5);
        if (key != _getKey)
        {
            _chunks.TryGetValue(key, out _getChunk);
            _getKey = key;
        }
        return _getChunk != null && !float.IsNaN(_getChunk.DisplayRefY) ? _getChunk.Display[Local(cx, cz)] : default;
    }

    private long _getKey = long.MinValue;
    private ViewChunk _getChunk;

    /// <summary>
    /// The live physics overlay classified this cell (against the same reference height); if it
    /// disagrees with the bake, it wins until the reference changes.
    /// </summary>
    public void OverrideFromOverlay(int cx, int cz, CellKind kind, float dy)
    {
        long cellKey = Key(cx, cz);
        if (!_chunks.TryGetValue(Key(cx >> 5, cz >> 5), out var c) || c.KindRefY != _refY)
        {
            _overrides[cellKey] = (kind, dy);
            return;
        }
        int i = Local(cx, cz);
        if (c.Kind[i] == kind && Math.Abs(c.Dy[i] - dy) < 0.25f) { _overrides.Remove(cellKey); return; }
        _overrides[cellKey] = (kind, dy);
        c.Kind[i] = kind;
        c.Dy[i] = dy;
        MarkDisplayDirtyAround(cx, cz);
    }

    /// <summary>Builds/refreshes chunks around the player, nearest first, within the time budget.</summary>
    public void Update(Vector3 player, float viewRadius, float budgetMs)
    {
        if (float.IsNaN(_refY)) return;
        var sw = Stopwatch.StartNew();
        int pcx = Mathf.FloorToInt(player.x / CellSize) >> 5, pcz = Mathf.FloorToInt(player.z / CellSize) >> 5;
        int ring = Mathf.CeilToInt(viewRadius / (CellSize * 32)) + 1;

        for (int r = 0; r <= ring; r++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue; // ring r only
            if (!EnsureDisplay(pcx + dx, pcz + dz, sw, budgetMs)) return;
        }
    }

    /// <summary>False when the time budget ran out before this chunk could be finished.</summary>
    private bool EnsureDisplay(int chx, int chz, Stopwatch sw, float budgetMs)
    {
        var c = EnsureKinds(chx, chz, sw, budgetMs);
        if (c == null) return false;
        if (c.DisplayRefY == _refY && !c.DisplayDirty) return true;
        // Despeckle needs the neighbours' kinds along the chunk border.
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            if ((dx != 0 || dz != 0) && EnsureKinds(chx + dx, chz + dz, sw, budgetMs) == null) return false;

        Span<CellKind> n8 = stackalloc CellKind[8];
        int baseX = chx << 5, baseZ = chz << 5;
        for (int lz = 0; lz < 32; lz++)
        for (int lx = 0; lx < 32; lx++)
        {
            int i = (lz << 5) | lx, cx = baseX + lx, cz = baseZ + lz, n = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dz != 0) n8[n++] = KindAt(cx + dx, cz + dz);

            var kind = c.Kind[i];
            var shown = Despeckle.Filter(kind, n8);
            c.Display[i] = shown == kind ? MapPalette.ColorFor(kind, c.Dy[i]) : MapPalette.ColorFor(shown, 0f);
        }
        c.DisplayRefY = _refY;
        c.DisplayDirty = false;
        return true;
    }

    /// <summary>Classifies a chunk if needed; null if that would start after the budget ran out.</summary>
    private ViewChunk EnsureKinds(int chx, int chz, Stopwatch sw, float budgetMs)
    {
        long key = Key(chx, chz);
        if (!_chunks.TryGetValue(key, out var c)) _chunks[key] = c = new ViewChunk();
        if (c.KindRefY == _refY) return c;
        if (sw.Elapsed.TotalMilliseconds > budgetMs) return null;

        int baseX = chx << 5, baseZ = chz << 5;
        for (int lz = 0; lz < 32; lz++)
        for (int lx = 0; lx < 32; lx++)
        {
            int i = (lz << 5) | lx, cx = baseX + lx, cz = baseZ + lz;
            if (_overrides.TryGetValue(Key(cx, cz), out var o)) { c.Kind[i] = o.kind; c.Dy[i] = o.dy; continue; }
            c.Kind[i] = _classifier.Classify(cx, cz, _refY, out c.Dy[i]);
        }
        c.KindRefY = _refY;
        return c;
    }

    private CellKind KindAt(int cx, int cz) =>
        _chunks.TryGetValue(Key(cx >> 5, cz >> 5), out var c) && c.KindRefY == _refY ? c.Kind[Local(cx, cz)] : CellKind.Unknown;

    private void MarkDisplayDirtyAround(int cx, int cz)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            if (_chunks.TryGetValue(Key((cx + dx) >> 5, (cz + dz) >> 5), out var c)) c.DisplayDirty = true;
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;
    private static int Local(int cx, int cz) => ((cz & 31) << 5) | (cx & 31);
}
