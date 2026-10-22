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

    [Tooltip("Apply Magnus lift from spin (phase 2). Only used with Unity gravity on. Shots " +
             "still land on their aim point; spin only bends the path on the way.")]
    public bool useSpin;

    [Tooltip("Spin rate (rev/s). A guess: ~20-25 rev/s (1,200-1,500 rpm) for a ball rolling " +
             "out of the pocket. Treat it as a tuning value.")]
    [Min(0f)]
    public float spinRate;

    [Tooltip("Spin axis in shot space, seen from the shooter: X = shooter's right, Y = up, " +
             "Z = toward the goal. (1,0,0) = topspin (dips), (-1,0,0) = backspin (floats), " +
             "(0,1,0) = curves to the shooter's right, (0,-1,0) = to the left. Tilt it for a " +
             "sidearm shot, e.g. (1,1,0) curves and dips. Z does nothing.")]
    public Vector3 spinAxis;

    public static BallFlightSettings Default => new BallFlightSettings
    {
        useDrag = true,
        dragCoefficient = 0.5f,
        ballMass = 0.145f,
        ballDiameter = 0.0635f,
        airDensity = 1.2f,
        useSpin = false,
        spinRate = 22f,
        spinAxis = new Vector3(1f, 0f, 0f),
    };
}

/// <summary>
/// Adds the forces Unity's Rigidbody doesn't have to the ball's flight: air drag and, optionally,
/// spin (Magnus lift). Unity keeps doing gravity — this never adds it, or the ball would get
/// double gravity. See Plans/Current/Ball_Flight_Physics_Plan.md.
///
/// It also predicts flights with the same math, stepped like the physics engine
/// (semi-implicit Euler at Time.fixedDeltaTime, v += a·dt then x += v·dt), so predictions match
/// the real flight: RandomLauncher solves each launch with it so drag and spin don't pull the
/// shot off its aim point, and GoalDetector / ShotPathRecorder project a saved shot along it.
/// The predictor adds Physics.gravity when asked to (when the ball flies under Unity gravity).
///
/// Spin is fixed per shot: the solve picks it (see <see cref="SolveLaunchVelocity"/>) and
/// <see cref="BeginFlight"/> applies that same spin, so solve and flight always agree.
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
    private Vector3 _flightSpin;   // world-space spin of the current shot (axis × rad/s)

    // A prediction stops after this long without reaching the plane.
    private const float MaxPredictTime = 5f;

    // Launch solve: correct the aim until the predicted crossing is this close to the target.
    private const int SolveIterations = 8;
    private const float SolveTolerance = 0.001f;
    // Warn when the best aim is still this far off (the shot visibly misses its aim point).
    private const float SolveWarnDistance = 0.01f;

    // Lift coefficient ≈ spin ratio (r·ω / v), which only holds up to about this.
    private const float MaxLiftCoefficient = 0.3f;

    // Spin tried for a shot, as a fraction of the full setting: less if a shot would dip to
    // the floor (a bounce shot) or can't be aimed onto the target; none always works.
    private static readonly float[] SpinScales = { 1f, 0.5f, 0f };

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
    /// step, the same velocity the predictor uses.</summary>
    void FixedUpdate()
    {
        if (!_inFlight || _rb.isKinematic) return;

        Vector3 acceleration = ExtraAcceleration(_rb.linearVelocity, _flightSpin);
        if (acceleration != Vector3.zero)
            _rb.AddForce(acceleration, ForceMode.Acceleration);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Starts applying drag and <paramref name="spin"/> (world space, from
    /// <see cref="SolveLaunchVelocity"/>; zero for none). RandomLauncher calls this at launch.</summary>
    public void BeginFlight(Vector3 spin)
    {
        _inFlight = true;
        _flightSpin = spin;
    }

    /// <summary>Stops applying forces. Called automatically when FloorBoundary despawns the ball.</summary>
    public void EndFlight()
    {
        _inFlight = false;
    }

    /// <summary>Acceleration from drag and spin at <paramref name="velocity"/>. Excludes gravity.
    ///   Drag: k·|v|² against the direction of travel, k = ρ·Cd·A / (2m).
    ///   Lift: (ρ·A·C_L / 2m)·|v|² along spin × v̂, with C_L = r·ω / |v| (capped).</summary>
    public Vector3 ExtraAcceleration(Vector3 velocity, Vector3 spin)
    {
        if (settings.ballMass <= 0f) return Vector3.zero;

        float radius = 0.5f * settings.ballDiameter;
        float area = Mathf.PI * radius * radius;
        float k = settings.airDensity * area / (2f * settings.ballMass);
        float speed = velocity.magnitude;
        Vector3 acceleration = Vector3.zero;

        if (settings.useDrag)
            acceleration -= k * settings.dragCoefficient * speed * velocity;

        if (spin != Vector3.zero && speed > 0.01f)
        {
            float liftCoefficient = Mathf.Min(radius * spin.magnitude / speed, MaxLiftCoefficient);
            Vector3 liftDirection = Vector3.Cross(spin, velocity).normalized;
            acceleration += k * liftCoefficient * speed * speed * liftDirection;
        }

        return acceleration;
    }

    /// <summary>
    /// Launch velocity at <paramref name="speed"/> that arcs from <paramref name="origin"/> onto
    /// <paramref name="target"/> under Unity gravity plus drag and spin, crossing the plane
    /// z = target.z at the target. <paramref name="spin"/> is the spin it was solved with — pass
    /// it to <see cref="BeginFlight"/>.
    ///
    /// Starts from the no-drag gravity solve (QuadrantMath.ComputeBallisticLaunchVelocity), then
    /// corrects the aim with the predictor: move the aim point by the miss, keep the speed, repeat.
    /// Starting from that flat shot it always finds the flat, fast shot, never a lob.
    ///
    /// With spin on, a shot whose path would dip to the floor before the plane (it'd become a
    /// bounce shot), or that can't be aimed within 1 cm, is retried with half spin, then none.
    /// Without spin the path can't reach the floor. If even that can't get within 1 cm (speed
    /// too low to reach), logs a warning and returns the best try.
    /// </summary>
    public Vector3 SolveLaunchVelocity(Vector3 origin, Vector3 target, float speed, out Vector3 spin)
    {
        Vector3 firstGuess = QuadrantMath.ComputeBallisticLaunchVelocity(
            origin, target, speed, Physics.gravity, Time.fixedDeltaTime);

        Vector3 fullSpin = settings.useSpin ? ShotSpin(firstGuess) : Vector3.zero;
        float floorY = (_floorBoundary != null ? _floorBoundary.floorY : 0f) + 0.5f * settings.ballDiameter;

        for (int i = 0; i < SpinScales.Length; i++)
        {
            Vector3 trySpin = fullSpin * SpinScales[i];
            Vector3 velocity = SolveAim(origin, target, speed, firstGuess, trySpin,
                                        out float miss, out float minY);

            bool lastTry = trySpin == Vector3.zero;
            bool good = miss <= SolveWarnDistance && minY >= floorY;
            if (!good && !lastTry) continue;

            if (i > 0 && fullSpin != Vector3.zero)
            {
                Debug.Log($"[BallFlight] Full spin would {(minY < floorY ? "dip to the floor" : "miss the target")} " +
                          $"on this shot — using {SpinScales[i] * 100f:F0}% spin.");
            }
            if (miss > SolveWarnDistance)
            {
                string missText = miss < float.MaxValue ? $"{miss * 100f:F1} cm" : "never reaches it";
                Debug.LogWarning($"[BallFlight] Couldn't solve a {speed:F1} m/s shot onto the target " +
                                 $"({missText} off) — speed too low? Using the best try.");
            }

            spin = trySpin;
            return velocity;
        }

        // Unreachable: the last scale is 0, which always returns above.
        spin = Vector3.zero;
        return firstGuess;
    }

    /// <summary>Where a ball at <paramref name="pos"/> with <paramref name="vel"/> crosses the
    /// plane z = <paramref name="planeZ"/>, and how long it takes, under drag and the current
    /// shot's spin (plus Physics.gravity when <paramref name="withGravity"/>). Returns false if
    /// it isn't moving toward the plane or doesn't reach it within a few seconds.</summary>
    public bool PredictPlaneCrossing(Vector3 pos, Vector3 vel, float planeZ, bool withGravity,
                                     out Vector3 crossing, out float time)
    {
        return Simulate(pos, vel, _flightSpin, planeZ, withGravity, null, 0f, 0,
                        out crossing, out time, out _, out _);
    }

    /// <inheritdoc cref="PredictPlaneCrossing(Vector3, Vector3, float, bool, out Vector3, out float)"/>
    /// <param name="arrivalVelocity">The ball's velocity at the crossing.</param>
    public bool PredictPlaneCrossing(Vector3 pos, Vector3 vel, float planeZ, bool withGravity,
                                     out Vector3 crossing, out float time, out Vector3 arrivalVelocity)
    {
        return Simulate(pos, vel, _flightSpin, planeZ, withGravity, null, 0f, 0,
                        out crossing, out time, out arrivalVelocity, out _);
    }

    /// <summary>The predicted path (current shot's spin) from <paramref name="pos"/> to the
    /// plane z = <paramref name="planeZ"/>, with points about <paramref name="spacing"/> apart
    /// (at most <paramref name="maxPoints"/>), ending exactly on the crossing. Just the start
    /// point if it never reaches the plane.</summary>
    public Vector3[] SamplePathToPlane(Vector3 pos, Vector3 vel, float planeZ, float spacing,
                                       int maxPoints, bool withGravity)
    {
        var points = new List<Vector3>();
        Simulate(pos, vel, _flightSpin, planeZ, withGravity, points, spacing, Mathf.Max(maxPoints, 2),
                 out _, out _, out _, out _);
        return points.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>World-space spin for a shot launched at <paramref name="launchVelocity"/>:
    /// settings.spinAxis turned from shot space (right / up / toward goal, by the launch's
    /// horizontal direction) into world space, times the spin rate in rad/s.</summary>
    private Vector3 ShotSpin(Vector3 launchVelocity)
    {
        if (settings.spinRate <= 0f || settings.spinAxis == Vector3.zero) return Vector3.zero;

        Vector3 forward = new Vector3(launchVelocity.x, 0f, launchVelocity.z);
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector3 axis = settings.spinAxis.x * right
                     + settings.spinAxis.y * Vector3.up
                     + settings.spinAxis.z * forward;

        return axis.normalized * (settings.spinRate * 2f * Mathf.PI);
    }

    /// <summary>The aim correction loop for one spin. Returns the best velocity found, its miss
    /// distance, and the lowest point of its path before the plane.</summary>
    private Vector3 SolveAim(Vector3 origin, Vector3 target, float speed, Vector3 firstGuess,
                             Vector3 spin, out float bestMiss, out float bestMinY)
    {
        Vector3 velocity = firstGuess;
        Vector3 best = velocity;
        bestMiss = float.MaxValue;
        bestMinY = Mathf.Min(origin.y, target.y);

        float distance = Vector3.Distance(origin, target);
        if (distance < 0.001f)
        {
            bestMiss = 0f;
            return velocity;
        }

        // Normally this is speed. If speed can't reach even without drag, the gravity solve
        // (which already warned) returned its faster minimum-speed shot; keep that speed so
        // the best try lands as close as it can.
        float shotSpeed = Mathf.Max(speed, velocity.magnitude);

        // Aim point at the target's distance along the launch direction, so moving it by the
        // miss moves the crossing by about that much.
        Vector3 aim = origin + velocity.normalized * distance;

        for (int i = 0; i < SolveIterations; i++)
        {
            if (!Simulate(origin, velocity, spin, target.z, true, null, 0f, 0,
                          out Vector3 hit, out _, out _, out float minY))
                break;

            Vector3 miss = target - hit;
            float missDistance = miss.magnitude;
            if (missDistance < bestMiss)
            {
                bestMiss = missDistance;
                bestMinY = minY;
                best = velocity;
            }
            if (missDistance < SolveTolerance) break;

            aim += miss;
            velocity = (aim - origin).normalized * shotSpeed;
        }

        return best;
    }

    /// <summary>Steps a flight the way the physics engine does until it crosses the plane, then
    /// interpolates within the last step for the exact point (as GoalDetector does between
    /// frames). If <paramref name="samples"/> is given, fills it with the path, start to crossing.
    /// <paramref name="minY"/> is the lowest height on the way.</summary>
    private bool Simulate(Vector3 pos, Vector3 vel, Vector3 spin, float planeZ, bool withGravity,
                          List<Vector3> samples, float spacing, int maxPoints,
                          out Vector3 crossing, out float time, out Vector3 arrivalVelocity, out float minY)
    {
        crossing = pos;
        time = 0f;
        arrivalVelocity = vel;
        minY = pos.y;

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
            Vector3 nextVel = vel + (gravity + ExtraAcceleration(vel, spin)) * dt;
            Vector3 nextPos = pos + nextVel * dt;

            if ((planeZ - nextPos.z) * side <= 0f)
            {
                float f = Mathf.InverseLerp(pos.z, nextPos.z, planeZ);
                crossing = Vector3.Lerp(pos, nextPos, f);
                time = (step + f) * dt;
                arrivalVelocity = nextVel;
                minY = Mathf.Min(minY, crossing.y);
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
            minY = Mathf.Min(minY, pos.y);

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
