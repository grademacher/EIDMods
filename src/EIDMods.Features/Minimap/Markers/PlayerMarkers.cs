using System;
using System.Collections.Generic;
using EIDMods.MapCore.Markers;
using UnityEngine;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>
/// Teammates from AG.Actors.Player.Players (minus the local player), read every frame. Green dot;
/// orange when downed (current state is DownedState, found with the F4 probe 2026-11-23).
/// </summary>
internal sealed class PlayerMarkers : IMarkerProvider
{
    private readonly MarkerConfig _cfg;
    private readonly List<MapMarker> _current = new();

    public PlayerMarkers(MarkerConfig cfg) => _cfg = cfg;

    public void Update(float now, Vector3 playerPos)
    {
        _current.Clear();
        if (!_cfg.ShowPlayers.Value) return;

        var local = AG.Actors.Player.Local;
        var players = AG.Actors.Player.Players;
        if (players == null) return;
        int n = players.Count;
        for (int i = 0; i < n; i++)
        {
            var p = players[i];
            if (p == null || (local != null && p.Pointer == local.Pointer)) continue;
            if (!p.IsAliveAndConnected) continue;
            var pos = p.transform.position;
            _current.Add(new MapMarker(pos.x, pos.y, pos.z, IsDowned(p) ? MarkerKind.TeammateDowned : MarkerKind.Teammate));
        }
    }

    public void Collect(float now, List<MapMarker> into) => into.AddRange(_current);

    public void Reset() => _current.Clear();

    private static bool IsDowned(AG.Actors.Player p)
    {
        try
        {
            var current = p.stateMachineComponent?.StateMachine?.Current;
            return current != null && current.TryCast<AG.StateMachine.Players.DownedState>() != null;
        }
        catch (Exception) { return false; }
    }
}
