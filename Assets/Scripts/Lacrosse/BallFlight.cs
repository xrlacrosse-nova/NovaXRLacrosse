using System.Collections.Generic;
using UnityEngine;

/// <summary>Real-ball numbers for <see cref="BallFlight"/>. Defaults are an NCAA lacrosse ball
/// in sea-level air (Plans/Current/Ball_Flight_Physics_Plan.md).</summary>
[System.Serializable]
public struct BallFlightSettings
{
    [Tooltip("Apply air drag (grows with speed squared) while the ball is in flight.")]
    public bool useDrag;

    [Tooltip("Drag coefficient of the ball. ~0.5 for a rough rubber sphere; tune between 0.4 " +
             "and 0.55 by eye with RandomLauncher's arrival-speed log.")]
    [Range(0f, 1f)]
    public float dragCoefficient;

    [Tooltip("Ball mass (kg) used for the drag math. NCAA ball: 0.142-0.149.")]
    [Min(0.001f)]
    public float ballMass;

    [Tooltip("Ball diameter (m) used for the drag cross-section. NCAA ball: ~0.0635.")]
    [Min(0.001f)]
    public float ballDiameter;

    [Tooltip("Air density (kg/m^3). ~1.2 at sea level.")]
    [Min(0f)]
    public float airDensity;

    public static BallFlightSettings Default => new BallFlightSettings
    {
        useDrag = true,
        dragCoefficient = 0.5f,
        ballMass = 0.145f,
        ballDiameter = 0.0635f,
        airDensity = 1.2f,
    };
}

/// <summary>
/// Adds the forces Unity's Rigidbody doesn't have to the ball's flight: air drag (and, later,
/// spin). Unity keeps doing gravity — this never adds it, or the ball would get double gravity.
/// See Plans/Current/Ball_Flight_Physics_Plan.md.
///
/// It also predicts flights with the same math, stepped like the physics engine
/// (semi-implicit Euler at Time.fixedDeltaTime, v += a·dt then x += v·dt), so predictions match
/// the real flight: RandomLauncher solves each launch with it so drag doesn't pull the shot off
/// its aim point, and GoalDetector / ShotPathRecorder project a saved shot along it. The
/// predictor adds Physics.gravity when asked to (when the ball flies under Unity gravity).
///
/// Setup: add to the ball, next to RandomLauncher / GoalDetector / FloorBoundary. Don't put it
/// on a ball with LacrossBallPhysics (that has its own gravity and bounce logic).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BallFlight : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    public BallFlightSettings settings = BallFlightSettings.Default;

    // ── Private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private FloorBoundary _floorBoundary;
    private bool _inFlight = false;

    // A prediction stops after this long without reaching the plane.
    private const float MaxPredictTime = 5f;

    // Launch solve: correct the aim until the predicted crossing is this close to the target.
    private const int SolveIterations = 8;
    private const float SolveTolerance = 0.001f;
    // Warn when the best aim is still this far off (the shot visibly misses its aim point).
    private const float SolveWarnDistance = 0.01f;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _floorBoundary = GetComponent<FloorBoundary>();
    }

    void OnEnable()
    {
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += EndFlight;
    }

    void OnDisable()
    {
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= EndFlight;
    }

    /// <summary>Does nothing while the ball is kinematic (hidden, or frozen after a save), so a
    /// saved ball stays put. AddForce here acts on the velocity at the start of the physics
    /// step, the same velocity the predictor uses for its drag.</summary>
    void FixedUpdate()
    {
        if (!_inFlight || _rb.isKinematic) return;

        Vector3 acceleration = ExtraAcceleration(_rb.linearVelocity);
        if (acceleration != Vector3.zero)
            _rb.AddForce(acceleration, ForceMode.Acceleration);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Starts applying drag. RandomLauncher calls this at launch.</summary>
    public void BeginFlight()
    {
        _inFlight = true;
    }

    /// <summary>Stops applying drag. Called automatically when FloorBoundary despawns the ball.</summary>
    public void EndFlight()
    {
        _inFlight = false;
    }

    /// <summary>Acceleration from drag at <paramref name="velocity"/>: k·|v|² against the
    /// direction of travel, with k = ρ·Cd·A / (2m). Excludes gravity.</summary>
    public Vector3 ExtraAcceleration(Vector3 velocity)
    {
        if (!settings.useDrag || settings.ballMass <= 0f) return Vector3.zero;

        float radius = 0.5f * settings.ballDiameter;
        float area = Mathf.PI * radius * radius;
        float k = settings.airDensity * settings.dragCoefficient * area / (2f * settings.ballMass);

        return -k * velocity.magnitude * velocity;
    }

    /// <summary>
    /// Launch velocity at <paramref name="speed"/> that arcs from <paramref name="origin"/> onto
    /// <paramref name="target"/> under Unity gravity plus drag, crossing the plane
    /// z = target.z at the target.
    ///
    /// Starts from the no-drag gravity solve (QuadrantMath.ComputeBallisticLaunchVelocity), then
    /// corrects the aim with the predictor: move the aim point by the miss, keep the speed, repeat.
    /// Starting from that flat shot it always finds the flat, fast shot, never a lob. If it
    /// can't get within 1 cm (speed too low to reach), logs a warning and returns the best try.
    /// </summary>
    public Vector3 SolveLaunchVelocity(Vector3 origin, Vector3 target, float speed)
    {
        Vector3 velocity = QuadrantMath.ComputeBallisticLaunchVelocity(
            origin, target, speed, Physics.gravity, Time.fixedDeltaTime);

        float distance = Vector3.Distance(origin, target);
        if (distance < 0.001f) return velocity;

        // Normally this is speed. If speed can't reach even without drag, the gravity solve
        // (which already warned) returned its faster minimum-speed shot; keep that speed so
        // the best try lands as close as it can.
        float shotSpeed = Mathf.Max(speed, velocity.magnitude);

        // Aim point at the target's distance along the launch direction, so moving it by the
        // miss moves the crossing by about that much.
        Vector3 aim = origin + velocity.normalized * distance;

        Vector3 best = velocity;
        float bestMiss = float.MaxValue;

        for (int i = 0; i < SolveIterations; i++)
        {
            if (!PredictPlaneCrossing(origin, velocity, target.z, true, out Vector3 hit, out _))
                break;

            Vector3 miss = target - hit;
            float missDistance = miss.magnitude;
            if (missDistance < bestMiss)
            {
                bestMiss = missDistance;
                best = velocity;
            }
            if (missDistance < SolveTolerance) break;

            aim += miss;
            velocity = (aim - origin).normalized * shotSpeed;
        }

        if (bestMiss > SolveWarnDistance)
        {
            string missText = bestMiss < float.MaxValue ? $"{bestMiss * 100f:F1} cm" : "never reaches it";
            Debug.LogWarning($"[BallFlight] Couldn't solve a {shotSpeed:F1} m/s shot onto the target " +
                             $"with drag ({missText} off) — speed too low? Using the best try.");
        }

        return best;
    }

    /// <summary>Where a ball at <paramref name="pos"/> with <paramref name="vel"/> crosses the
    /// plane z = <paramref name="planeZ"/>, and how long it takes, under drag (plus
    /// Physics.gravity when <paramref name="withGravity"/>). Returns false if it isn't moving
    /// toward the plane or doesn't reach it within a few seconds.</summary>
    public bool PredictPlaneCrossing(Vector3 pos, Vector3 vel, float planeZ, bool withGravity,
                                     out Vector3 crossing, out float time)
    {
        return Simulate(pos, vel, planeZ, withGravity, null, 0f, 0,
                        out crossing, out time, out _);
    }

    /// <inheritdoc cref="PredictPlaneCrossing(Vector3, Vector3, float, bool, out Vector3, out float)"/>
    /// <param name="arrivalVelocity">The ball's velocity at the crossing.</param>
    public bool PredictPlaneCrossing(Vector3 pos, Vector3 vel, float planeZ, bool withGravity,
                                     out Vector3 crossing, out float time, out Vector3 arrivalVelocity)
    {
        return Simulate(pos, vel, planeZ, withGravity, null, 0f, 0,
                        out crossing, out time, out arrivalVelocity);
    }

    /// <summary>The predicted path from <paramref name="pos"/> to the plane
    /// z = <paramref name="planeZ"/>, with points about <paramref name="spacing"/> apart (at
    /// most <paramref name="maxPoints"/>), ending exactly on the crossing. Just the start point
    /// if it never reaches the plane.</summary>
    public Vector3[] SamplePathToPlane(Vector3 pos, Vector3 vel, float planeZ, float spacing,
                                       int maxPoints, bool withGravity)
    {
        var points = new List<Vector3>();
        Simulate(pos, vel, planeZ, withGravity, points, spacing, Mathf.Max(maxPoints, 2),
                 out _, out _, out _);
        return points.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>Steps a flight the way the physics engine does until it crosses the plane, then
    /// interpolates within the last step for the exact point (as GoalDetector does between
    /// frames). If <paramref name="samples"/> is given, fills it with the path, start to crossing.</summary>
    private bool Simulate(Vector3 pos, Vector3 vel, float planeZ, bool withGravity,
                          List<Vector3> samples, float spacing, int maxPoints,
                          out Vector3 crossing, out float time, out Vector3 arrivalVelocity)
    {
        crossing = pos;
        time = 0f;
        arrivalVelocity = vel;

        samples?.Add(pos);

        // +1 if the plane is toward +z of the ball, -1 if toward -z.
        float side = Mathf.Sign(planeZ - pos.z);
        if (Mathf.Abs(planeZ - pos.z) < 0.0001f) return true;
        if (vel.z * side <= 0f) return false;

        float dt = Time.fixedDeltaTime;
        Vector3 gravity = withGravity ? Physics.gravity : Vector3.zero;
        int maxSteps = Mathf.CeilToInt(MaxPredictTime / dt);
        Vector3 lastSample = pos;

        for (int step = 0; step < maxSteps; step++)
        {
            Vector3 nextVel = vel + (gravity + ExtraAcceleration(vel)) * dt;
            Vector3 nextPos = pos + nextVel * dt;

            if ((planeZ - nextPos.z) * side <= 0f)
            {
                float f = Mathf.InverseLerp(pos.z, nextPos.z, planeZ);
                crossing = Vector3.Lerp(pos, nextPos, f);
                time = (step + f) * dt;
                arrivalVelocity = nextVel;
                if (samples != null)
                {
                    // The crossing always goes in, replacing the last sample if the cap was hit.
                    if (samples.Count >= maxPoints) samples[samples.Count - 1] = crossing;
                    else samples.Add(crossing);
                }
                return true;
            }

            pos = nextPos;
            vel = nextVel;

            // Stalled or turned back (only possible if gravity had a z component).
            if (vel.z * side <= 0f) return false;

            // Leave room for the crossing at the end.
            if (samples != null && samples.Count < maxPoints - 1
                && (pos - lastSample).sqrMagnitude >= spacing * spacing)
            {
                samples.Add(pos);
                lastSample = pos;
            }
        }

        return false;
    }
}
