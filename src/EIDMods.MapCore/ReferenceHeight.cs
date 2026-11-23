using System;

namespace EIDMods.MapCore;

/// <summary>
/// The height cells are classified against. It follows the player's feet once they've moved more
/// than 0.5 m up or down, so small bobs/undulations don't re-classify the map (classifying against
/// the raw feet height made borderline cells flicker). The body band starts at +0.45 m
/// (ClassifierBands.BodyLow) so that even at the maximum 0.5 m lag above the feet, a 1 m railing
/// still blocks it. With the earlier 1 m lag a railing on a landing you'd walked down to drew as floor.
/// </summary>
public sealed class ReferenceHeight
{
    public const float FollowDown = 0.5f, FollowUp = 0.5f;

    public float Value { get; private set; } = float.NaN;

    /// <summary>Returns true when the reference moved (new floor).</summary>
    public bool Update(float feetY)
    {
        if (!float.IsNaN(Value))
        {
            float d = feetY - Value;
            if (d <= FollowUp && d >= -FollowDown) return false;
        }
        Value = feetY;
        return true;
    }

    public void Reset() => Value = float.NaN;
}
