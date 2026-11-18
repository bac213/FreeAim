using UnityEngine;

namespace FreeAim.Client;

internal static class MotionLimits
{
    internal static Vector2 AngleDelta(Vector2 a, Vector2 b) =>
        new(Mathf.DeltaAngle(a.x, b.x), Mathf.DeltaAngle(a.y, b.y));

    internal static bool CameraCut(Vector2 acceptedInput, ref Vector2 measured)
    {
        Vector2 external = AngleDelta(acceptedInput, measured);
        if (external.sqrMagnitude > 45f * 45f) return true;
        measured = acceptedInput + external; // preserve direction if a long frame crossed +/-180
        return false;
    }

    // Positive exit distance along a unit direction. A native pose already past the
    // preferred radius can retract or cross the sphere, but cannot extend any farther.
    internal static float ReachTravel(Vector3 from, Vector3 direction, float length)
    {
        float along = Vector3.Dot(from, direction);
        float radius = 0.96f * length;
        float room = Mathf.Max(0f, radius * radius - from.sqrMagnitude);
        return Mathf.Sqrt(along * along + room) - along;
    }

    // A rectangle with quarter-elliptic corners. The central 65% of each edge is
    // unchanged. Return the outward unit normal in degrees, not in normalized space.
    internal static Vector2 RoundReach(Vector2 offset, Vector2 reach, out Vector2 normal)
    {
        const float flat = 0.65f, corner = 1f - flat;
        Vector2 p = new(Mathf.Abs(offset.x) / reach.x, Mathf.Abs(offset.y) / reach.y);
        Vector2 center = Vector2.Min(p, new Vector2(flat, flat));
        Vector2 excess = p - center;
        normal = Vector2.zero;
        if (excess.sqrMagnitude <= corner * corner) return offset;
        Vector2 radial = excess.normalized;
        Vector2 clipped = center + radial * corner;
        float sx = Mathf.Sign(offset.x), sy = Mathf.Sign(offset.y);
        normal = new Vector2(sx * radial.x / reach.x, sy * radial.y / reach.y).normalized;
        return new Vector2(sx * reach.x * clipped.x, sy * reach.y * clipped.y);
    }
}
