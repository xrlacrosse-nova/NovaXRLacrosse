using UnityEngine;

/// <summary>
/// Launches the lacrosse ball toward a chosen quadrant of the goal gate defined
/// in GoalDetector (must be on the same GameObject).
///
/// Quadrant layout (facing the goal):
///   TopLeft    | TopRight
///   -----------+-----------
///   BottomLeft | BottomRight
///
/// Setup:
///   1. Attach this script to the ball GameObject that also has GoalDetector + Rigidbody.
///   2. Set LaunchOrigin to a Transform positioned where shots come from (e.g. an empty at player position).
///   3. Pick a Quadrant and press Play – the ball will launch automatically.
///      Alternatively, call LaunchBall() from any other script or Unity Event.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(GoalDetector))]
public class NewestBallLauncher : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("Shot Setup")]
    [Tooltip("Where the ball is shot FROM. Assign a Transform in the scene (e.g. an empty at player position).")]
    public Transform launchOrigin;

    [Tooltip("Which quadrant of the goal to aim at.")]
    public Quadrant targetQuadrant = Quadrant.TopLeft;

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

    [Header("Post-Goal Falling")]
    [Tooltip("If true, when the ball scores the launcher will apply custom gravity to make the ball fall to the floor. " +
             "Use when Unity's global gravity is turned off.")]
    public bool enableCustomGravityOnGoal = true;

    [Tooltip("Downward acceleration (m/s^2) applied while the ball is falling. ~9.81 mimics Earth gravity.")]
    public float customGravity = 9.81f;

    // ── Private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private GoalDetector _goalDetector;
    private float _launchTimer;
    private bool _pendingLaunch;

    // falling state
    private bool _falling = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _goalDetector = GetComponent<GoalDetector>();
    }

    void OnEnable()
    {
        _goalDetector.OnGoalScored += HandleGoalScored;
    }

    void OnDisable()
    {
        _goalDetector.OnGoalScored -= HandleGoalScored;
    }

    void Start()
    {
        // Validate launch origin
        if (launchOrigin == null)
        {
            Debug.LogWarning("[BallLauncher] LaunchOrigin is not set. Using the ball's current position as origin. " +
                             "Assign a Transform in the Inspector for more realistic shots.");
        }

        // Schedule the automatic launch
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

    /// <summary>Called by GoalDetector when this ball crosses the plane inside the gate.</summary>
    private void HandleGoalScored(Vector3 crossingPos)
    {
        if (enableCustomGravityOnGoal)
            BeginFalling();
    }

    void FixedUpdate()
    {
        // Apply custom gravity while falling (independent of Unity's global gravity).
        if (_falling && _rb != null)
        {
            _rb.AddForce(Vector3.down * customGravity, ForceMode.Acceleration);
        }
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Launches the ball toward <see cref="targetQuadrant"/> immediately.
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

            // Ensure rigidbody participates in physics and isn't using Unity gravity.
            _rb.isKinematic = false;
            _rb.useGravity = false;

#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector3.zero;
#else
            _rb.velocity = Vector3.zero;
#endif
            _rb.angularVelocity = Vector3.zero;

            transform.position = origin;

            // reset internal state
            _falling = false;
        }

        // ── 3. Compute target point inside the chosen quadrant ───
        Vector3 target = ComputeQuadrantTarget(quadrant);

        // ── 4. Compute launch velocity ───────────────────────────
        Vector3 launchVelocity = QuadrantMath.ComputeLaunchVelocity(origin, target, launchSpeed);

        // Ensure physics participation (cover case where other scripts toggle kinematic)
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

        Debug.Log($"[BallLauncher] Shot fired → {quadrant} | " +
                  $"target ({target.x:F2}, {target.y:F2}, {target.z:F2}) | " +
                  $"speed {launchVelocity.magnitude:F1} m/s");
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>
    /// Returns the world-space aim point inside the requested quadrant of the goal gate.
    /// </summary>
    Vector3 ComputeQuadrantTarget(Quadrant quadrant)
    {
        return QuadrantMath.ComputePointInQuadrant(
            _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, quadrant, quadrantDepth);
    }

    /// <summary>
    /// Enable falling by letting the Rigidbody participate in physics and applying custom gravity
    /// inside FixedUpdate. This preserves the current velocity so the ball continues through the net
    /// and then falls down when custom gravity is active.
    /// </summary>
    private void BeginFalling()
    {
        if (_rb == null) return;

        _rb.isKinematic = false;
        _rb.useGravity = false; // leave Unity gravity off as requested
        _falling = true;

        Debug.Log("[BallLauncher] Ball will fall to the floor (custom gravity active).");
    }

    // ── Gizmos ───────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (_goalDetector == null)
            _goalDetector = GetComponent<GoalDetector>();
        if (_goalDetector == null) return;

        Vector3 origin = launchOrigin != null ? launchOrigin.position : transform.position;

        // Draw aim point for each quadrant
        Quadrant[] quads = (Quadrant[])System.Enum.GetValues(typeof(Quadrant));
        Color[] colors = { Color.red, Color.green, Color.blue, Color.yellow };

        for (int i = 0; i < quads.Length; i++)
        {
            Vector3 t = ComputeQuadrantTarget(quads[i]);
            Gizmos.color = colors[i];
            Gizmos.DrawSphere(t, 0.04f);

            // Dim line to target
            Gizmos.color = new Color(colors[i].r, colors[i].g, colors[i].b, 0.25f);
            Gizmos.DrawLine(origin, t);
        }

        // Highlight the currently selected quadrant with a larger sphere
        Vector3 selected = ComputeQuadrantTarget(targetQuadrant);
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(selected, 0.08f);

        // Launch origin marker
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(origin, 0.06f);
    }
}