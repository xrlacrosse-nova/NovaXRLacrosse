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

    /// <summary>
    /// A gate-plane world point as a normalized gate position: each axis in [-1, 1], 0 at the
    /// gate center, ±1 at the edges (clamped, so shots wide of the cage sit on the edge).
    /// Used by ShotHeatmapRecorder and ShotPathRecorder, so their quadrants always agree.
    /// </summary>
    public static Vector2 NormalizeToGate(Vector3 center, Vector2 half, Vector3 gatePlanePos)
    {
        return new Vector2(
            Mathf.Clamp((gatePlanePos.x - center.x) / half.x, -1f, 1f),
            Mathf.Clamp((gatePlanePos.y - center.y) / half.y, -1f, 1f));
    }

    /// <summary>
    /// Which quadrant a normalized gate-plane point (each axis in [-1, 1], as recorded by
    /// ShotHeatmapRecorder) falls into. The inverse of the sign convention SignX/SignY use for aiming.
    /// </summary>
    public static Quadrant BucketFromNormalized(Vector2 normalized)
    {
        bool left = normalized.x < 0f;
        bool top = normalized.y >= 0f;

        if (top) return left ? Quadrant.TopLeft : Quadrant.TopRight;
        return left ? Quadrant.BottomLeft : Quadrant.BottomRight;
    }

    private static float SignX(Quadrant quadrant) =>
        (quadrant == Quadrant.TopLeft || quadrant == Quadrant.BottomLeft) ? -1f : 1f;

    private static float SignY(Quadrant quadrant) =>
        (quadrant == Quadrant.TopLeft || quadrant == Quadrant.TopRight) ? 1f : -1f;

    /// <summary>
    /// Launch velocity that sends a gravity-free shot from <paramref name="origin"/> straight
    /// through <paramref name="target"/> at <paramref name="launchSpeed"/>.
    ///
    /// Only correct when no gravity acts mid-flight, so it always aims directly at the target.
    /// Any vertical bias layered on top of that direction (e.g. for a cosmetic arc) would aim
    /// the ball away from target with nothing left to curve it back onto that line. For a shot
    /// that flies under gravity the whole way, use <see cref="ComputeBallisticLaunchVelocity"/>.
    /// </summary>
    public static Vector3 ComputeLaunchVelocity(Vector3 origin, Vector3 target, float launchSpeed)
    {
        Vector3 delta = target - origin;

        if (delta.sqrMagnitude < 0.001f * 0.001f)
            return Vector3.forward * launchSpeed;

        return delta.normalized * launchSpeed;
    }

    private const float MinFlightTime = 0.01f;
    private const int BisectionSteps = 20;

    /// <summary>
    /// Launch velocity that arcs a shot from <paramref name="origin"/> onto
    /// <paramref name="target"/> under constant <paramref name="gravity"/> (no drag), at about
    /// <paramref name="launchSpeed"/>. See Plans/Future/Reviewed/Unity_Gravity_Plan.md.
    ///
    /// For flight time T: v0 = (Δ - ½·g·T² - ½·g·dt·T) / T. The ½·g·dt·T term matches how the
    /// physics engine steps (gravity is added to velocity before each move), which drops a
    /// stepped flight slightly more than the textbook parabola. Speed is large for tiny T,
    /// falls to a minimum, then rises, so the flat, fast shot is the smaller T where
    /// |v0(T)| = launchSpeed, found by bisection. If launchSpeed can't reach the target, the
    /// minimum-speed shot is used instead and a warning is logged.
    /// </summary>
    public static Vector3 ComputeBallisticLaunchVelocity(
        Vector3 origin, Vector3 target, float launchSpeed, Vector3 gravity, float dt)
    {
        return ComputeBallisticLaunchVelocity(origin, target, launchSpeed, gravity, dt, out _);
    }

    /// <inheritdoc cref="ComputeBallisticLaunchVelocity(Vector3, Vector3, float, Vector3, float)"/>
    /// <param name="flightTime">Solved time from origin to target, in seconds.</param>
    public static Vector3 ComputeBallisticLaunchVelocity(
        Vector3 origin, Vector3 target, float launchSpeed, Vector3 gravity, float dt, out float flightTime)
    {
        Vector3 delta = target - origin;
        float g = gravity.magnitude;

        // No gravity or no distance: the straight shot is already exact.
        if (g < 0.001f || delta.sqrMagnitude < 0.001f * 0.001f)
        {
            Vector3 straight = ComputeLaunchVelocity(origin, target, launchSpeed);
            flightTime = delta.magnitude / Mathf.Max(launchSpeed, 0.001f);
            return straight;
        }

        // Minimum-speed flight time of the textbook parabola. The dt term shifts it only
        // slightly, which doesn't matter for picking the bisection bracket.
        float tLow = MinFlightTime;
        float tMinSpeed = Mathf.Max(Mathf.Sqrt(2f * delta.magnitude / g), tLow);

        if (BallisticVelocity(delta, gravity, dt, tMinSpeed).magnitude > launchSpeed)
        {
            Debug.LogWarning($"[QuadrantMath] launchSpeed {launchSpeed:F1} m/s can't reach the target " +
                             $"under gravity. Using the minimum-speed shot " +
                             $"({BallisticVelocity(delta, gravity, dt, tMinSpeed).magnitude:F1} m/s).");
            flightTime = tMinSpeed;
            return BallisticVelocity(delta, gravity, dt, tMinSpeed);
        }

        // Even the shortest allowed flight is slower than launchSpeed: take that flight.
        if (BallisticVelocity(delta, gravity, dt, tLow).magnitude <= launchSpeed)
        {
            flightTime = tLow;
            return BallisticVelocity(delta, gravity, dt, tLow);
        }

        // |v0(T)| falls from above launchSpeed at tLow to at most launchSpeed at tHigh.
        float tHigh = tMinSpeed;
        for (int i = 0; i < BisectionSteps; i++)
        {
            float tMid = 0.5f * (tLow + tHigh);
            if (BallisticVelocity(delta, gravity, dt, tMid).magnitude > launchSpeed)
                tLow = tMid;
            else
                tHigh = tMid;
        }

        flightTime = tHigh;
        return BallisticVelocity(delta, gravity, dt, tHigh);
    }

    // v0 that covers delta in flightTime under gravity, stepped at dt.
    private static Vector3 BallisticVelocity(Vector3 delta, Vector3 gravity, float dt, float flightTime)
    {
        return delta / flightTime - 0.5f * gravity * (flightTime + dt);
    }

    /// <summary>
    /// Where a ball at <paramref name="pos"/> with <paramref name="vel"/> under constant
    /// <paramref name="gravity"/> crosses the plane z = <paramref name="planeZ"/>, and how long
    /// it takes. Returns false if it isn't moving toward the plane.
    ///
    /// Assumes gravity has no z component (it points straight down), so z moves at constant
    /// speed and the crossing time is linear.
    /// </summary>
    public static bool BallisticPlaneCrossing(Vector3 pos, Vector3 vel, Vector3 gravity,
                                              float planeZ, out Vector3 crossing, out float time)
    {
        crossing = pos;
        time = 0f;

        if (Mathf.Abs(vel.z) < 0.0001f)
            return false;

        time = (planeZ - pos.z) / vel.z;
        if (time < 0f)
            return false;

        crossing = pos + vel * time + 0.5f * gravity * time * time;
        return true;
    }
}
