using System;
using System.Collections.Generic;
using EIDMods.MapCore.Markers;
using UnityEngine;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>
/// Enemies: live AG.Enemies.Pawn actors from AG.Actors.Actor.All, sampled at 10 Hz and interpolated
/// between the last two samples when drawn. Dead or destroyed pawns disappear at the next sample.
/// EnemyMode: near (EnemyRadius, through walls), lineOfSight (near + a Linecast every 0.25 s),
/// all, off.
/// </summary>
internal sealed class EnemyMarkers : IMarkerProvider
{
    private const float SampleInterval = 0.1f, LosInterval = 0.25f;
    private const int MaxLosChecks = 30;

    private sealed class Track
    {
        public Vector3 Prev, Cur;
        public float CurTime;
        public bool Visible = true;
        public float NextLos;
        public int Seen;
    }

    private readonly MarkerConfig _cfg;
    private readonly Dictionary<IntPtr, Track> _tracks = new();
    private readonly List<IntPtr> _gone = new();
    private float _nextSample;
    private int _sampleId;

    public EnemyMarkers(MarkerConfig cfg) => _cfg = cfg;

    public void Update(float now, Vector3 playerPos)
    {
        string mode = _cfg.EnemyMode.Value;
        if (!_cfg.ShowEnemies.Value || mode == "off") { _tracks.Clear(); return; }
        if (now < _nextSample) return;
        _nextSample = now + SampleInterval;
        _sampleId++;

        float r2 = _cfg.EnemyRadius.Value * _cfg.EnemyRadius.Value;
        bool all = mode == "all", los = mode == "lineOfSight";
        int losChecks = 0;
        var head = playerPos + Vector3.up * 1.6f;

        var actors = AG.Actors.Actor.All;
        int n = actors?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            var pawn = a.TryCast<AG.Enemies.Pawn>();
            if (pawn == null) continue;
            if (!pawn.IsAlive) { _tracks.Remove(pawn.Pointer); continue; }

            var pos = pawn.transform.position;
            if (!all && (pos - playerPos).sqrMagnitude > r2) continue;

            if (!_tracks.TryGetValue(pawn.Pointer, out var t))
            {
                t = new Track { Prev = pos, Cur = pos };
                _tracks[pawn.Pointer] = t;
            }
            else t.Prev = t.Cur;
            t.Cur = pos;
            t.CurTime = now;
            t.Seen = _sampleId;

            if (!los) t.Visible = true;
            else if (now >= t.NextLos && losChecks < MaxLosChecks)
            {
                losChecks++;
                t.NextLos = now + LosInterval;
                t.Visible = !Physics.Linecast(head, pos + Vector3.up * 1.0f, MapGrid.SolidMask, QueryTriggerInteraction.Ignore);
            }
        }

        // Dead, destroyed or out of range since the last sample: drop immediately.
        _gone.Clear();
        foreach (var kv in _tracks) if (kv.Value.Seen != _sampleId) _gone.Add(kv.Key);
        foreach (var k in _gone) _tracks.Remove(k);
    }

    public void Collect(float now, List<MapMarker> into)
    {
        foreach (var t in _tracks.Values)
        {
            if (!t.Visible) continue;
            float k = Math.Clamp((now - t.CurTime) / SampleInterval, 0f, 1f);
            // Draw one sample behind, moving from Prev to Cur over the sample interval.
            var p = t.Prev + (t.Cur - t.Prev) * k;
            into.Add(new MapMarker(p.x, p.y, p.z, MarkerKind.Enemy));
        }
    }

    public void Reset() => _tracks.Clear();
}
