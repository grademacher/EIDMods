using System.Collections.Generic;
using EIDMods.MapCore.Markers;
using UnityEngine;

namespace EIDMods.Features.Minimap.Markers;

/// <summary>
/// Collects markers from the game. Update runs every frame (providers throttle themselves);
/// Collect appends what should be drawn this frame. Read-only: never interacts with game objects.
/// </summary>
internal interface IMarkerProvider
{
    void Update(float now, Vector3 playerPos);
    void Collect(float now, List<MapMarker> into);
    void Reset();
}
