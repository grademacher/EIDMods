using System;
using System.Collections.Generic;
using UnityEngine;
using EIDMods.MapCore;

namespace EIDMods.Features.Minimap;

/// <summary>
/// Map cells classified live with physics queries (no cameras, no lighting). Before a level's bake
/// is ready this is the whole minimap; afterwards it runs near the player only, as an overlay that
/// corrects the baked map (doors, destruction).
///
/// The world is split into square cells (CellSize metres). Each cell is classified (wall, obstacle,
/// step up, floor, void) relative to the reference height passed to <see cref="Sample"/>
/// (see ReferenceHeight: the player's feet with 1 m hysteresis).
///
/// Raw classifications are kept per cell; what's drawn is a despeckled "display" colour, rebuilt
/// per chunk when cells in or next to it change (isolated solid cells read as floor, floor cells
/// boxed in by solids read as solid).
///
/// Cells are kept as the player explores and re-sampled when stale (doors open/close) or when
/// the reference height changes (new floor).
/// </summary>
internal sealed class MapGrid
{
    public const int ChunkCells = 32;
    private const int ChunkArea = ChunkCells * ChunkCells;

    // Level geometry only (from the probe, 2026-11-23): 0 Default (incl. terrain), 7 Always Render,
    // 9 Small, 10 Medium, 13 Distant, 14 Terrain. Leaves out ClothCollision, Hitbox, Pawn, Actor,
    // Trigger, Items, Ragdoll, Water, Navigation Obstacle, ...
    public static readonly int FloorMask = (1 << 0) | (1 << 7) | (1 << 9) | (1 << 10) | (1 << 13) | (1 << 14);
    // What can make a cell a wall/obstacle: same, minus "Small" props (rocks, bones, clutter),
    // which were the main source of single-cell noise.
    public static readonly int SolidMask = FloorMask & ~(1 << 9);

    private sealed class Chunk
    {
        public readonly CellKind[] Kind = new CellKind[ChunkArea];
        public readonly Color32[] Raw = new Color32[ChunkArea];       // colour straight from classification
        public readonly Color32[] Display = new Color32[ChunkArea];   // despeckled; a == 0: nothing to draw
        public readonly float[] SampledRefY = new float[ChunkArea];
        public readonly float[] SampledAt = new float[ChunkArea];
        public bool Dirty;
    }

    private readonly Dictionary<long, Chunk> _chunks = new();
    private readonly List<long> _dirty = new();
    private long _cachedKey = long.MinValue;
    private Chunk _cachedChunk;

    private Vector2Int[] _offsets = Array.Empty<Vector2Int>();
    private float _offsetsRadius = -1f;
    private int _cursor;
    private Vector2Int _lastCenter = new(int.MinValue, int.MinValue);

    public float CellSize { get; }
    public int ChunkCount => _chunks.Count;
    /// <summary>Reference height of the last <see cref="Sample"/> call (see ReferenceHeight).</summary>
    public float ReferenceY { get; private set; } = float.NaN;

    public MapGrid(float cellSize) => CellSize = cellSize;

    /// <summary>Display colour of a cell, or default (a == 0) if never sampled.</summary>
    public Color32 Get(int cx, int cz)
    {
        var chunk = FindChunk(cx >> 5, cz >> 5, create: false);
        return chunk == null ? default : chunk.Display[((cz & 31) << 5) | (cx & 31)];
    }

    /// <summary>
    /// Samples up to <paramref name="budget"/> cells around <paramref name="feet"/>, nearest first,
    /// classified against <paramref name="refY"/>, then rebuilds the display colours of chunks that
    /// changed. <paramref name="onClassified"/> sees every freshly classified cell (the baked view
    /// uses it as a live overlay). Returns how many cells were sampled.
    /// </summary>
    public int Sample(Vector3 feet, float refY, float radius, int budget, float refreshSeconds,
        Action<int, int, CellKind, float, bool> onClassified = null)
    {
        if (!Mathf.Approximately(radius, _offsetsRadius)) BuildOffsets(radius);

        var center = new Vector2Int(Mathf.FloorToInt(feet.x / CellSize), Mathf.FloorToInt(feet.z / CellSize));
        if (center != _lastCenter) { _lastCenter = center; _cursor = 0; } // moved: restart from nearest

        if (refY != ReferenceY) { ReferenceY = refY; _cursor = 0; } // new floor: restart from nearest

        float now = Time.unscaledTime;
        int sampled = 0, scanned = 0, maxScan = Math.Min(_offsets.Length, 6000);
        while (sampled < budget && scanned < maxScan)
        {
            var o = _offsets[_cursor];
            _cursor = (_cursor + 1) % _offsets.Length;
            scanned++;

            int cx = center.x + o.x, cz = center.y + o.y;
            var chunk = FindChunk(cx >> 5, cz >> 5, create: true);
            int i = ((cz & 31) << 5) | (cx & 31);

            bool stale = chunk.Kind[i] == CellKind.Unknown
                         || chunk.SampledRefY[i] != refY
                         || now - chunk.SampledAt[i] > refreshSeconds;
            if (!stale) continue;

            float x = (cx + 0.5f) * CellSize, z = (cz + 0.5f) * CellSize;
            var kind = ClassifyKind(x, z, refY, out float dy);
            var color = MapPalette.ColorFor(kind, dy);
            chunk.SampledRefY[i] = refY;
            chunk.SampledAt[i] = now;
            sampled++;
            if (onClassified != null)
                onClassified(cx, cz, kind, dy, !OverlayPolicy.IsBlocked(kind) && IsConservativelyClear(x, z, refY));

            if (chunk.Kind[i] == kind && chunk.Raw[i].Equals(color)) continue; // unchanged: nothing to redraw
            chunk.Kind[i] = kind;
            chunk.Raw[i] = color;
            MarkDirty(cx, cz);
        }

        RebuildDirty();
        return sampled;
    }

    /// <summary>
    /// Nothing at body height anywhere in the cell (full-cell box, no inset). The overlay needs this
    /// before it may "open" a cell the bake has as blocked: the inset box can miss a thin wall.
    /// </summary>
    // Same heights as the bake's ColumnClassifier (ClassifierBands), as box centre/half-height.
    private const float BodyMid = (ClassifierBands.BodyLow + ClassifierBands.BodyHigh) / 2, BodyHalf = (ClassifierBands.BodyHigh - ClassifierBands.BodyLow) / 2;
    private const float HeadMid = (ClassifierBands.HeadLow + ClassifierBands.HeadHigh) / 2, HeadHalf = (ClassifierBands.HeadHigh - ClassifierBands.HeadLow) / 2;

    private bool IsConservativelyClear(float x, float z, float feetY)
    {
        float c = CellSize;
        return !Physics.CheckBox(new Vector3(x, feetY + BodyMid, z), new Vector3(c * 0.45f, BodyHalf, c * 0.45f),
            Quaternion.identity, SolidMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>What's at world (x, z) for someone standing at feetY. dy = floor height - feetY.</summary>
    public CellKind ClassifyKind(float x, float z, float feetY, out float dy)
    {
        float c = CellSize;
        dy = 0f;

        // Something solid between knee and head height?
        // Same inset as the bake (ClassifierBands.CellInset): geometry only grazing the cell's edge
        // strip (door frames) doesn't occupy it.
        float h = Math.Max(0.05f, c * 0.5f - ClassifierBands.CellInset);
        bool blocked = Physics.CheckBox(new Vector3(x, feetY + BodyMid, z), new Vector3(h, BodyHalf, h),
            Quaternion.identity, SolidMask, QueryTriggerInteraction.Ignore);

        if (blocked)
        {
            // Rising ground / stairs / a low ledge has a walkable top not far above us; a wall doesn't.
            // The top must be above knee height: the box only overlaps from feet+0.6 up, so a hit lower
            // than that means the ray started INSIDE a tall wall (mesh colliders don't register hits
            // from the inside) and fell through to the ground behind it.
            if (Physics.Raycast(new Vector3(x, feetY + ClassifierBands.StepRayFrom, z), Vector3.down, out var top, ClassifierBands.StepRayFrom, FloorMask, QueryTriggerInteraction.Ignore)
                && top.normal.y > 0.75f && top.point.y > feetY + ClassifierBands.StepMinTop)
            {
                dy = top.point.y - feetY;
                return CellKind.StepUp;
            }
            // Tall enough to be a wall? Something must also be there at head height (+1.7 to +2.3 m);
            // knee/waist-high clutter is only an obstacle.
            bool tall = Physics.CheckBox(new Vector3(x, feetY + HeadMid, z), new Vector3(h, HeadHalf, h),
                Quaternion.identity, SolidMask, QueryTriggerInteraction.Ignore);
            return tall ? CellKind.Wall : CellKind.Obstacle;
        }

        if (!Physics.Raycast(new Vector3(x, feetY + ClassifierBands.FloorRayFrom, z), Vector3.down, out var hit, ClassifierBands.FloorRayLength, FloorMask, QueryTriggerInteraction.Ignore))
            return CellKind.Void;

        dy = hit.point.y - feetY;
        return CellKind.Floor;
    }

    // ---- despeckled display ------------------------------------------------------------------

    private void MarkDirty(int cx, int cz)
    {
        // A cell's display depends on its 8 neighbours, so edge cells dirty the next chunk too.
        int chx = cx >> 5, chz = cz >> 5, lx = cx & 31, lz = cz & 31;
        MarkChunkDirty(chx, chz);
        if (lx == 0) MarkChunkDirty(chx - 1, chz);
        if (lx == 31) MarkChunkDirty(chx + 1, chz);
        if (lz == 0) MarkChunkDirty(chx, chz - 1);
        if (lz == 31) MarkChunkDirty(chx, chz + 1);
    }

    private void MarkChunkDirty(int chx, int chz)
    {
        var chunk = FindChunk(chx, chz, create: false);
        if (chunk == null || chunk.Dirty) return;
        chunk.Dirty = true;
        _dirty.Add(Key(chx, chz));
    }

    private void RebuildDirty()
    {
        for (int d = 0; d < _dirty.Count; d++)
        {
            long key = _dirty[d];
            if (!_chunks.TryGetValue(key, out var chunk)) continue;
            chunk.Dirty = false;
            int chx = (int)(key >> 32), chz = (int)(uint)key;
            int baseX = chx << 5, baseZ = chz << 5;

            for (int lz = 0; lz < ChunkCells; lz++)
            for (int lx = 0; lx < ChunkCells; lx++)
            {
                int i = (lz << 5) | lx;
                var kind = chunk.Kind[i];
                if (kind == CellKind.Unknown) { chunk.Display[i] = default; continue; }

                // Majority filter (MapCore.Despeckle); a speck takes the raw colour of the first
                // neighbour of the winning kind.
                int cx = baseX + lx, cz = baseZ + lz, n = 0;
                Span<CellKind> neighbours = stackalloc CellKind[8];
                Span<Color32> sample = stackalloc Color32[6];
                Span<bool> sampled = stackalloc bool[6];
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var nChunk = FindChunk((cx + dx) >> 5, (cz + dz) >> 5, create: false);
                    var k = CellKind.Unknown;
                    if (nChunk != null)
                    {
                        int ni = (((cz + dz) & 31) << 5) | ((cx + dx) & 31);
                        k = nChunk.Kind[ni];
                        if (k != CellKind.Unknown && !sampled[(int)k]) { sample[(int)k] = nChunk.Raw[ni]; sampled[(int)k] = true; }
                    }
                    neighbours[n++] = k;
                }

                var shown = Despeckle.Filter(kind, neighbours);
                chunk.Display[i] = shown == kind ? chunk.Raw[i] : sample[(int)shown];
            }
        }
        _dirty.Clear();
    }

    // ---- debug -------------------------------------------------------------------------------

    /// <summary>
    /// Debug: log what each cell looks like along 8 compass directions from the player, both
    /// classified live and as stored for display.
    /// '#' wall, 'o' obstacle, '^' step up, '.' level floor, '+'/'-' floor up/down, 'v' drop, '_' void.
    /// </summary>
    public void DebugRays(Vector3 feet, float length, BepInEx.Logging.ManualLogSource log)
    {
        float refY = float.IsNaN(ReferenceY) ? feet.y : ReferenceY;
        log.LogInfo($"[MapDebug] feet={feet} refY={refY:F2} cell={CellSize}m  legend: # wall, o obstacle, ^ step-up, . level, +/- up/down, v drop, _ void");
        for (int a = 0; a < 360; a += 45)
        {
            var dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
            var live = new System.Text.StringBuilder();
            var shown = new System.Text.StringBuilder();
            for (float d = CellSize; d <= length; d += CellSize)
            {
                var p = feet + dir * d;
                var k = ClassifyKind(p.x, p.z, refY, out float dy);
                live.Append(k switch
                {
                    CellKind.Wall => '#',
                    CellKind.Obstacle => 'o',
                    CellKind.StepUp => '^',
                    CellKind.Void => '_',
                    _ => dy < -1.5f ? 'v' : dy < -0.3f ? '-' : dy > 0.3f ? '+' : '.',
                });
                var col = Get(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
                shown.Append(col.a == 0 ? '?' : col.Equals(MapPalette.Wall) ? '#' : col.Equals(MapPalette.Obstacle) ? 'o'
                    : col.Equals(MapPalette.FloorUp) ? '^' : col.Equals(MapPalette.Void) ? '_' : '.');
            }
            log.LogInfo("[MapDebug] " + a.ToString().PadLeft(3) + $"deg live  |{live}|");
            log.LogInfo($"[MapDebug]        shown |{shown}|");
        }
    }

    // ---- storage -----------------------------------------------------------------------------

    private static long Key(int chx, int chz) => ((long)chx << 32) | (uint)chz;

    private Chunk FindChunk(int chx, int chz, bool create)
    {
        long key = Key(chx, chz);
        if (key == _cachedKey) return _cachedChunk;
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            if (!create) return null;
            chunk = new Chunk();
            _chunks[key] = chunk;
        }
        _cachedKey = key;
        _cachedChunk = chunk;
        return chunk;
    }


    private void BuildOffsets(float radius)
    {
        _offsetsRadius = radius;
        int r = Mathf.CeilToInt(radius / CellSize);
        var list = new List<Vector2Int>();
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            if (dx * dx + dz * dz <= r * r) list.Add(new Vector2Int(dx, dz));
        list.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
        _offsets = list.ToArray();
        _cursor = 0;
    }
}
