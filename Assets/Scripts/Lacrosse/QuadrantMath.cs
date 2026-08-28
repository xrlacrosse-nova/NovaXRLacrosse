using UnityEngine;

/// <summary>
/// Shared math for subdividing the goal gate into quadrants and picking aim points
/// within them. Used by the ball launchers so quadrant logic stays consistent and
/// only needs fixing in one place.
/// </summary>
public static class QuadrantMath
{
    /// <summary>
    /// Deterministic aim point inside <paramref name="quadrant"/>, interpolated from the
    /// quadrant's center toward its outer corner by <paramref name="depth"/> (0 = center, 1 = corner).
    /// </summary>
    public static Vector3 ComputePointInQuadrant(Vector3 center, Vector2 half, Quadrant quadrant, float depth)
    {
        float hx = half.x * 0.5f;
        float hy = half.y * 0.5f;

        float signX = SignX(quadrant);
        float signY = SignY(quadrant);

        Vector3 quadrantCenter = center + new Vector3(signX * hx, signY * hy, 0f);
        Vector3 quadrantCorner = center + new Vector3(signX * half.x, signY * half.y, 0f);

        return Vector3.Lerp(quadrantCenter, quadrantCorner, depth);
    }

    /// <summary>
    /// Uniformly-random point inside <paramref name="quadrant"/>, inset from the quadrant's
    /// edges by <paramref name="padding"/> (0-1 fraction of the quadrant's half-size).
    /// </summary>
    public static Vector3 ComputeRandomPointInQuadrant(Vector3 center, Vector2 half, Quadrant quadrant, float padding)
    {
        float hx = half.x * 0.5f;
        float hy = half.y * 0.5f;

        Vector3 quadCenter = center + new Vector3(SignX(quadrant) * hx, SignY(quadrant) * hy, 0f);

        float usableHx = hx * (1f - padding);
        float usableHy = hy * (1f - padding);

        float randomX = Random.Range(-usableHx, usableHx);
        float randomY = Random.Range(-usableHy, usableHy);

        return quadCenter + new Vector3(randomX, randomY, 0f);
    }

    /// <summary>
    /// World-space center of <paramref name="quadrant"/> (half the gate's width/height from the gate center).
    /// </summary>
    public static Vector3 QuadrantCenter(Vector3 center, Vector2 half, Quadrant quadrant)
    {
        return center + new Vector3(SignX(quadrant) * half.x * 0.5f, SignY(quadrant) * half.y * 0.5f, 0f);
    }

    private static float SignX(Quadrant quadrant) =>
        (quadrant == Quadrant.TopLeft || quadrant == Quadrant.BottomLeft) ? -1f : 1f;

    private static float SignY(Quadrant quadrant) =>
        (quadrant == Quadrant.TopLeft || quadrant == Quadrant.TopRight) ? 1f : -1f;

    /// <summary>
    /// Launch velocity that sends a gravity-free shot from <paramref name="origin"/> straight
    /// through <paramref name="target"/> at <paramref name="launchSpeed"/>.
    ///
    /// The ball launchers fly with no gravity applied mid-flight, so this always aims directly
    /// at the target — any vertical bias layered on top of that direction (e.g. for a cosmetic
    /// arc) would aim the ball away from target with nothing left to curve it back onto that
    /// line, which is exactly the bug this replaced. A real arc needs gravity active for the
    /// whole flight and a velocity solved to pass through target under it (see
    /// Plans/BallLauncher_Plan.md, Option B) — not implemented here.
    /// </summary>
    public static Vector3 ComputeLaunchVelocity(Vector3 origin, Vector3 target, float launchSpeed)
    {
        Vector3 delta = target - origin;

        if (delta.sqrMagnitude < 0.001f * 0.001f)
            return Vector3.forward * launchSpeed;

        return delta.normalized * launchSpeed;
    }
}
