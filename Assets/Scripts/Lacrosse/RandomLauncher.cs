using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using MagicLeap.Examples;

/// <summary>
/// Runs a simulation-style shooting session: the ball sits idle until the player starts a session
/// (controller trigger, or Space in the Unity Editor), waits through a pre-start countdown, then
/// fires a fixed number of shots at randomized quadrants of the goal gate defined in GoalDetector
/// (must be on the same GameObject), spaced by a randomized interval. Supports two aim modes:
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
///   3. Pick an AimMode and press Play. The ball stays idle until the start trigger fires
///      (controller trigger button, or Space bar in the Editor), then a session of ShotsPerSession
///      shots runs automatically. Alternatively, call LaunchBall() directly from any other script
///      or Unity Event to fire a single shot outside the session flow.
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

    [Header("Session")]
    [Tooltip("Number of shots fired per session, once started.")]
    [Min(1)]
    public int shotsPerSession = 3;

    [Tooltip("Seconds after the start trigger before the first shot fires. Randomized per session.")]
    [Min(0f)]
    public float minPreStartDelay = 10f;
    [Min(0f)]
    public float maxPreStartDelay = 15f;

    [Tooltip("Seconds between shots within a session. Randomized fresh each shot so shots aren't rhythmic.")]
    [Min(0f)]
    public float minShotInterval = 5f;
    [Min(0f)]
    public float maxShotInterval = 10f;

    // ── Private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private GoalDetector _goalDetector;
    private FloorBoundary _floorBoundary;

    // falling state
    private bool _falling = false;

    // session state
    private bool _sessionRunning = false;
    private bool _controllerTriggerSubscribed = false;
    private int _shotsFiredThisSession = 0;

    // pre-start countdown display state
    private bool _showCountdown = false;
    private float _countdownRemaining = 0f;
    private float _goDisplayTimer = 0f;
    private const float GoDisplayDuration = 1f;

    // last target (used by Gizmos to show where the next/last shot went)
    private Vector3 _lastTarget;
    private bool _hasTarget = false;
    private Quadrant _lastQuadrant;

    // ── Events ───────────────────────────────────────────────────

    /// <summary>Fired when a new session begins (right as the pre-start countdown starts).</summary>
    public event System.Action OnSessionStarted;

    /// <summary>Fired once a session completes (all ShotsPerSession shots fired and despawned).</summary>
    public event System.Action OnSessionEnded;

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

        // Don't touch MagicLeapController.Instance in Awake() — it needs an InputActionManager
        // already present in the scene, which may not be true that early. Try here instead, and
        // fail gracefully (e.g. testing in the Editor without a full ML rig set up) so the Space
        // bar fallback below still works.
        try
        {
            MagicLeapController.Instance.TriggerPressed += HandleStartTriggerPressed;
            _controllerTriggerSubscribed = true;
        }
        catch (System.NullReferenceException)
        {
            Debug.LogWarning("[RandomLauncher] No MagicLeapController input available (no InputActionManager " +
                              "in scene) — controller-trigger start is disabled this session; use the Space " +
                              "bar in the Editor instead.");
        }
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;

        if (_controllerTriggerSubscribed)
        {
            MagicLeapController.Instance.TriggerPressed -= HandleStartTriggerPressed;
            _controllerTriggerSubscribed = false;
        }
    }

    void Start()
    {
        if (launchOrigin == null)
        {
            Debug.LogWarning("[RandomLauncher] LaunchOrigin is not set. Using the ball's current position as origin. " +
                             "Assign a Transform in the Inspector for more realistic shots.");
        }

        Debug.Log("[RandomLauncher] Waiting for start trigger (controller trigger"
#if UNITY_EDITOR
                  + ", or Space in the Editor"
#endif
                  + ").");
    }

    void Update()
    {
        if (_goDisplayTimer > 0f)
            _goDisplayTimer -= Time.deltaTime;

#if UNITY_EDITOR
        // Editor-only fallback so the whole start -> countdown -> multi-shot loop can be verified
        // by pressing Play, without a headset/controller connected. Compiled out of device builds.
        if (!_sessionRunning && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            TryStartSession();
#endif
    }

    /// <summary>Called by GoalDetector whenever this ball crosses the gate plane, make or miss,
    /// so the ball always has a defined path (falling to the floor) past the plane.</summary>
    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        if (enableCustomGravityOnGoal)
            BeginFalling();
    }

    /// <summary>Called by FloorBoundary once the ball despawns after coming to rest on the floor.
    /// Continues the session (next shot after a randomized interval) until ShotsPerSession is
    /// reached, then ends the session.</summary>
    private void HandleDespawned()
    {
        if (!_sessionRunning) return;

        if (_shotsFiredThisSession >= shotsPerSession)
        {
            _sessionRunning = false;
            Debug.Log($"[RandomLauncher] Session complete ({shotsPerSession} shots fired).");
            OnSessionEnded?.Invoke();
            return;
        }

        StartCoroutine(AutoLaunchAfterDelay());
    }

    private IEnumerator AutoLaunchAfterDelay()
    {
        float delay = Random.Range(minShotInterval, maxShotInterval);
        yield return new WaitForSeconds(delay);
        FireNextShot();
    }

    void FixedUpdate()
    {
        if (_falling && _rb != null)
        {
            _rb.AddForce(Vector3.down * customGravity, ForceMode.Acceleration);
        }
    }

    // ── Session start ────────────────────────────────────────────

    private void HandleStartTriggerPressed(InputAction.CallbackContext ctx)
    {
        TryStartSession();
    }

    /// <summary>Starts a new session (pre-start countdown, then ShotsPerSession shots) if one
    /// isn't already running. Safe to call from any start-trigger source.</summary>
    private void TryStartSession()
    {
        if (_sessionRunning) return;
        StartCoroutine(RunSession());
    }

    private IEnumerator RunSession()
    {
        _sessionRunning = true;
        _shotsFiredThisSession = 0;
        OnSessionStarted?.Invoke();

        float delay = Random.Range(minPreStartDelay, maxPreStartDelay);
        _countdownRemaining = delay;
        _showCountdown = true;
        Debug.Log($"[RandomLauncher] Session starting — first shot in {delay:F1}s.");

        while (_countdownRemaining > 0f)
        {
            _countdownRemaining -= Time.deltaTime;
            yield return null;
        }

        _showCountdown = false;
        _goDisplayTimer = GoDisplayDuration;
        FireNextShot();
    }

    private void FireNextShot()
    {
        _shotsFiredThisSession++;
        LaunchBall();
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Randomly picks one of the four quadrants, logs it, and launches the ball toward it
    /// using the current <see cref="aimMode"/>. Safe to call from other scripts or Unity Events at
    /// any time — this fires a single shot outside the session flow (session shots call this
    /// internally via FireNextShot()).
    /// </summary>
    public void LaunchBall()
    {
        Quadrant[] quads = (Quadrant[])System.Enum.GetValues(typeof(Quadrant));
        Quadrant quadrant = quads[Random.Range(0, quads.Length)];

        Debug.Log($"[RandomLauncher] Aiming at quadrant: {quadrant}");

        LaunchToward(quadrant);
    }

    /// <summary>
    /// Launches toward a specific quadrant. Useful for scripted sequences or AI-driven shot selection.
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
        _lastQuadrant = quadrant;

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

    // ── On-screen countdown (pre-start only) ───────────────────────

    void OnGUI()
    {
        if (_showCountdown)
        {
            DrawCountdown();
        }
        else if (_goDisplayTimer > 0f)
        {
            float progress = 1f - (_goDisplayTimer / GoDisplayDuration);
            DrawPopLabel("GO!", Color.green, progress, animate: true);
        }
    }

    private void DrawCountdown()
    {
        if (_countdownRemaining > 3f)
        {
            DrawPopLabel("GET READY", Color.white, 0f, animate: false);
            return;
        }

        int digit = Mathf.Clamp(Mathf.CeilToInt(_countdownRemaining), 1, 3);
        // fractionInSecond goes 1 (digit just appeared) -> 0 (digit about to change), so
        // progress goes 0 -> 1 across the digit's one-second window.
        float fractionInSecond = _countdownRemaining - (digit - 1);
        float progress = Mathf.Clamp01(1f - fractionInSecond);
        DrawPopLabel(digit.ToString(), Color.yellow, progress, animate: true);
    }

    /// <summary>Same scale-pop/fade beat as GoalDetector's "GOAL!" overlay — pure OnGUI, no
    /// Canvas/TextMesh/prefab required.</summary>
    private void DrawPopLabel(string text, Color color, float progress, bool animate)
    {
        float scale = 1f;
        float alpha = 1f;

        if (animate)
        {
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));
            scale = Mathf.Lerp(2.0f, 1.0f, eased);
            scale *= 1.0f + 0.05f * Mathf.Sin(eased * Mathf.PI * 4f);
            alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(progress));
        }

        GUIStyle baseStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 80,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        Matrix4x4 oldMatrix = GUI.matrix;
        Vector2 pivot = new Vector2(Screen.width * 0.5f, Screen.height * 0.3f + 60f);
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), pivot);

        GUIStyle shadow = new GUIStyle(baseStyle);
        Color shadowColor = Color.black;
        shadowColor.a = alpha;
        shadow.normal.textColor = shadowColor;
        GUI.Label(new Rect(4f, Screen.height * 0.3f + 4f, Screen.width, 120f), text, shadow);

        GUIStyle fg = new GUIStyle(baseStyle);
        Color fgColor = color;
        fgColor.a = alpha;
        fg.normal.textColor = fgColor;
        GUI.Label(new Rect(0f, Screen.height * 0.3f, Screen.width, 120f), text, fg);

        GUI.matrix = oldMatrix;
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

            // Highlight the most recently launched quadrant with a larger sphere.
            if (_hasTarget)
            {
                Vector3 selected = QuadrantMath.ComputePointInQuadrant(center, half, _lastQuadrant, quadrantDepth);
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(selected, 0.08f);
            }
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
