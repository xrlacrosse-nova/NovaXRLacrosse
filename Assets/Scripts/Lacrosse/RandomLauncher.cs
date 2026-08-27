using System.Collections;
using UnityEngine;

/// <summary>
/// Launches the lacrosse ball toward a chosen quadrant of the goal gate defined in
/// GoalDetector (must be on the same GameObject). Supports two aim modes:
///   - RandomInQuadrant: a uniformly-random point inside the quadrant (no two shots alike).
///   - FixedPoint: a deterministic point, interpolated from the quadrant's center toward its
///     outer corner by QuadrantDepth (0 = center, 1 = corner).
///
/// Quadrant layout (facing the goal):
///   TopLeft    | TopRight
///   -----------+-----------
///   BottomLeft | BottomRight
///
/// Setup:
///   1. Attach this script to the ball GameObject that also has GoalDetector + Rigidbody.
///   2. Set LaunchOrigin to a Transform positioned where shots come from (e.g. an empty at player position).
///   3. Pick an AimMode, a Quadrant, and press Play – the ball will launch automatically.
///      Alternatively, call LaunchBall() from any other script or Unity Event.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(GoalDetector))]
public class RandomLauncher : MonoBehaviour
{
    public enum AimMode
    {
        RandomInQuadrant,
        FixedPoint
    }

    // ── Inspector ─────────────────────────────────────────────────

    [Header("Shot Setup")]
    [Tooltip("Where the ball is shot FROM. Assign a Transform in the scene (e.g. an empty at player position).")]
    public Transform launchOrigin;

    [Tooltip("Which quadrant of the goal to aim at.")]
    public Quadrant targetQuadrant = Quadrant.TopLeft;

    [Tooltip("RandomInQuadrant = a different random point inside the quadrant every shot. " +
             "FixedPoint = the same deterministic point every shot (see QuadrantDepth).")]
    public AimMode aimMode = AimMode.RandomInQuadrant;

    [Header("Random Aim (used when AimMode = RandomInQuadrant)")]
    [Tooltip("Inset from each quadrant edge as a fraction of the quadrant's half-size. " +
             "Increase this to keep shots away from the very edges (e.g. 0.05 = 5% padding).")]
    [Range(0f, 0.45f)]
    public float edgePadding = 0.05f;

    [Header("Fixed-Point Aim (used when AimMode = FixedPoint)")]
    [Tooltip("0 = dead-center of the quadrant. 1 = touching the quadrant boundary. " +
             "Use ~0.6-0.8 for realistic tight-corner shots.")]
    [Range(0f, 1f)]
    public float quadrantDepth = 0.65f;

    [Header("Launch Physics")]
    [Tooltip("Overall launch speed (m/s). Actual velocity is computed to hit the target; " +
             "this acts as a minimum — the ball will never be slower than this.")]
    public float launchSpeed = 18f;

    [Tooltip("How many seconds after Play() the ball launches automatically. Set to 0 to launch immediately.")]
    [Min(0f)]
    public float launchDelay = 0.5f;

    [Header("Ball Reset")]
    [Tooltip("If true, the ball teleports back to LaunchOrigin and resets GoalDetector state " +
             "each time LaunchBall() is called, so you can test repeatedly in the editor.")]
    public bool autoResetOnLaunch = true;

    [Header("Post-Crossing Falling")]
    [Tooltip("If true, once the ball crosses the goal plane (make or miss) the launcher will apply " +
             "custom gravity to make it fall to the floor. Use when Unity's global gravity is turned off.")]
    public bool enableCustomGravityOnGoal = true;

    [Tooltip("Downward acceleration (m/s^2) applied while the ball is falling. ~9.81 mimics Earth gravity.")]
    public float customGravity = 9.81f;

    [Header("Auto Loop")]
    [Tooltip("If true, automatically launches another shot (toward the same quadrant) after the ball despawns.")]
    public bool autoLoop = true;

    [Tooltip("Seconds to wait after the ball despawns before launching the next shot, simulating time for the user to reset/get ready.")]
    [Min(0f)]
    public float resetDelay = 2f;

    // ── Private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private GoalDetector _goalDetector;
    private FloorBoundary _floorBoundary;
    private float _launchTimer;
    private bool _pendingLaunch;

    // falling state
    private bool _falling = false;

    // last target (used by Gizmos to show where the next/last shot went)
    private Vector3 _lastTarget;
    private bool _hasTarget = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _goalDetector = GetComponent<GoalDetector>();
        _floorBoundary = GetComponent<FloorBoundary>();
    }

    void OnEnable()
    {
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += HandleDespawned;
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;
    }

    void Start()
    {
        if (launchOrigin == null)
        {
            Debug.LogWarning("[RandomLauncher] LaunchOrigin is not set. Using the ball's current position as origin. " +
                             "Assign a Transform in the Inspector for more realistic shots.");
        }

        if (launchDelay <= 0f)
        {
            LaunchBall();
        }
        else
        {
            _launchTimer = launchDelay;
            _pendingLaunch = true;
        }
    }

    void Update()
    {
        if (_pendingLaunch)
        {
            _launchTimer -= Time.deltaTime;
            if (_launchTimer <= 0f)
            {
                _pendingLaunch = false;
                LaunchBall();
            }
        }
    }

    /// <summary>Called by GoalDetector whenever this ball crosses the gate plane, make or miss,
    /// so the ball always has a defined path (falling to the floor) past the plane.</summary>
    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        if (enableCustomGravityOnGoal)
            BeginFalling();
    }

    /// <summary>Called by FloorBoundary once the ball despawns after coming to rest on the floor.
    /// Kicks off the next shot (toward the same quadrant) after <see cref="resetDelay"/>.</summary>
    private void HandleDespawned()
    {
        if (autoLoop)
            StartCoroutine(AutoLaunchAfterDelay());
    }

    private IEnumerator AutoLaunchAfterDelay()
    {
        yield return new WaitForSeconds(resetDelay);
        LaunchBall();
    }

    void FixedUpdate()
    {
        if (_falling && _rb != null)
        {
            _rb.AddForce(Vector3.down * customGravity, ForceMode.Acceleration);
        }
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Launches the ball toward <see cref="targetQuadrant"/> using the current <see cref="aimMode"/>.
    /// Safe to call from other scripts or Unity Events at any time.
    /// </summary>
    public void LaunchBall()
    {
        LaunchToward(targetQuadrant);
    }

    /// <summary>
    /// Launches toward a specific quadrant, overriding the Inspector selection.
    /// Useful for scripted sequences or AI-driven shot selection.
    /// </summary>
    public void LaunchToward(Quadrant quadrant)
    {
        // ── 1. Determine launch origin position ──────────────────
        Vector3 origin = launchOrigin != null ? launchOrigin.position : transform.position;

        // ── 2. Reset the ball position & physics ─────────────────
        if (autoResetOnLaunch)
        {
            _goalDetector.ResetState();
            _floorBoundary?.CancelDespawn();

            _rb.isKinematic = false;
            _rb.useGravity = false;

#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector3.zero;
#else
            _rb.velocity = Vector3.zero;
#endif
            _rb.angularVelocity = Vector3.zero;

            transform.position = origin;

            _falling = false;
        }

        // ── 3. Pick a target inside the chosen quadrant ──────────
        Vector3 target = ComputeTarget(quadrant);
        _lastTarget = target;
        _hasTarget = true;

        // ── 4. Compute launch velocity ───────────────────────────
        Vector3 launchVelocity = QuadrantMath.ComputeLaunchVelocity(origin, target, launchSpeed);

        _rb.isKinematic = false;
        _rb.useGravity = false;

        // ── 5. Apply and notify GoalDetector ─────────────────────
#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity = launchVelocity;
#else
        _rb.velocity = launchVelocity;
#endif

        _falling = false;

        _goalDetector.OnBallLaunched();

        Debug.Log($"[RandomLauncher] Shot fired → {quadrant} ({aimMode}) | " +
                  $"target ({target.x:F2}, {target.y:F2}, {target.z:F2}) | " +
                  $"speed {launchVelocity.magnitude:F1} m/s");
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>
    /// Returns the world-space aim point inside the requested quadrant, per the current AimMode.
    /// </summary>
    Vector3 ComputeTarget(Quadrant quadrant)
    {
        return aimMode == AimMode.RandomInQuadrant
            ? QuadrantMath.ComputeRandomPointInQuadrant(
                _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, quadrant, edgePadding)
            : QuadrantMath.ComputePointInQuadrant(
                _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, quadrant, quadrantDepth);
    }

    /// <summary>
    /// Enables falling by applying custom gravity inside FixedUpdate.
    /// </summary>
    private void BeginFalling()
    {
        if (_rb == null) return;

        _rb.isKinematic = false;
        _rb.useGravity = false;
        _falling = true;

        Debug.Log("[RandomLauncher] Ball will fall to the floor (custom gravity active).");
    }

    // ── Gizmos ───────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (_goalDetector == null)
            _goalDetector = GetComponent<GoalDetector>();
        if (_goalDetector == null) return;

        Vector3 origin = launchOrigin != null ? launchOrigin.position : transform.position;
        Vector3 center = _goalDetector.goalGateCenter;
        Vector2 half = _goalDetector.goalGateHalfSize;

        Quadrant[] quads = (Quadrant[])System.Enum.GetValues(typeof(Quadrant));
        Color[] colors = { Color.red, Color.green, Color.blue, Color.yellow };

        if (aimMode == AimMode.RandomInQuadrant)
        {
            // Draw the usable (padded) bounds for each quadrant.
            for (int i = 0; i < quads.Length; i++)
            {
                float hx = half.x * 0.5f;
                float hy = half.y * 0.5f;

                Vector3 quadCenter = QuadrantMath.QuadrantCenter(center, half, quads[i]);
                float usableHx = hx * (1f - edgePadding);
                float usableHy = hy * (1f - edgePadding);

                Gizmos.color = new Color(colors[i].r, colors[i].g, colors[i].b, 0.4f);
                Vector3 tl = quadCenter + new Vector3(-usableHx, usableHy, 0f);
                Vector3 tr = quadCenter + new Vector3(usableHx, usableHy, 0f);
                Vector3 bl = quadCenter + new Vector3(-usableHx, -usableHy, 0f);
                Vector3 br = quadCenter + new Vector3(usableHx, -usableHy, 0f);
                Gizmos.DrawLine(tl, tr);
                Gizmos.DrawLine(tr, br);
                Gizmos.DrawLine(br, bl);
                Gizmos.DrawLine(bl, tl);

                Gizmos.color = colors[i];
                Gizmos.DrawSphere(quadCenter, 0.03f);
            }
        }
        else
        {
            // Draw the fixed aim point for each quadrant.
            for (int i = 0; i < quads.Length; i++)
            {
                Vector3 t = QuadrantMath.ComputePointInQuadrant(center, half, quads[i], quadrantDepth);
                Gizmos.color = colors[i];
                Gizmos.DrawSphere(t, 0.04f);

                Gizmos.color = new Color(colors[i].r, colors[i].g, colors[i].b, 0.25f);
                Gizmos.DrawLine(origin, t);
            }

            // Highlight the currently selected quadrant with a larger sphere.
            Vector3 selected = QuadrantMath.ComputePointInQuadrant(center, half, targetQuadrant, quadrantDepth);
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(selected, 0.08f);
        }

        // Show the last actual launch target (bright white sphere + line from origin).
        if (_hasTarget)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(_lastTarget, 0.08f);
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawLine(origin, _lastTarget);
        }

        // Launch origin marker.
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(origin, 0.06f);
    }
}
