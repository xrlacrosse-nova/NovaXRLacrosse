using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Runs a simulation-style shooting session: the ball sits idle until the player starts a session
/// (staring at the START target — GazeStartTarget — or Space in the Unity Editor), waits through a pre-start countdown, then
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
///   3. Pick an AimMode and press Play. The ball stays idle until a session is started
///      (GazeStartTarget calls TryStartSession(), or Space bar in the Editor), then a session of ShotsPerSession
///      shots runs automatically. Alternatively, call LaunchBall() directly from any other script
///      or Unity Event to fire a single shot outside the session flow.
///   4. Optional: drag the shooter avatar (with ShooterAnimationController) into Shooter Animation.
///      Session shots then play the shooting animation and launch on its release frame, and
///      LaunchOrigin should be an empty parented to the shooter's stick head.
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
    [Tooltip("Launch speed (m/s) off the stick. The direction is solved to hit the target. " +
             "The ball reaches the goal slower than this once drag (BallFlight) acts — see the " +
             "arrival speed in the 'Shot fired' log — and may launch faster only if this is too " +
             "slow to reach the target at all.")]
    public float launchSpeed = 18f;

    [Tooltip("Each shot's speed is Launch Speed ± this much (m/s), picked at random, so the goalie " +
             "can't time a fixed speed. Capped at half of Launch Speed. 0 = every shot at Launch " +
             "Speed. Set by DifficultySelector.")]
    [Min(0f)]
    public float launchSpeedVariance = 0f;

    [Tooltip("Use Unity's Rigidbody gravity (Physics.gravity) for the whole flight, with the " +
             "launch solved to arc onto the target. Off = straight, gravity-free flight " +
             "with custom gravity only after the goal plane (old behavior).")]
    public bool useUnityGravity = true;

    [Header("Ball Reset")]
    [Tooltip("If true, the ball teleports back to LaunchOrigin and resets GoalDetector state " +
             "each time LaunchBall() is called, so you can test repeatedly in the editor.")]
    public bool autoResetOnLaunch = true;

    [Header("Post-Crossing Falling (only when Use Unity Gravity is off)")]
    [Tooltip("Only used when Use Unity Gravity is off. If true, once the ball crosses the goal plane " +
             "(make or miss) the launcher will apply custom gravity to make it fall to the floor.")]
    public bool enableCustomGravityOnGoal = true;

    [Tooltip("Only used when Use Unity Gravity is off. Downward acceleration (m/s^2) applied while " +
             "the ball is falling. ~9.81 mimics Earth gravity.")]
    public float customGravity = 9.81f;

    [Header("Save")]
    [Tooltip("When the goalie saves a shot, the ball freezes where it was touched and vanishes after " +
             "this many seconds — a brief 'caught it' beat. 0 = vanishes next frame.")]
    [Min(0f)]
    public float saveDespawnDelay = 0.15f;

    [Header("Session")]
    [Tooltip("Number of shots fired per session, once started.")]
    [Min(1)]
    public int shotsPerSession = 3;

    [Tooltip("Seconds after the session starts before the first shot fires. Randomized per session.")]
    [Min(0f)]
    public float minPreStartDelay = 10f;
    [Min(0f)]
    public float maxPreStartDelay = 15f;

    [Tooltip("Seconds between shots within a session. Randomized fresh each shot so shots aren't rhythmic.")]
    [Min(0f)]
    public float minShotInterval = 5f;
    [Min(0f)]
    public float maxShotInterval = 10f;

    [Header("Shooter Animation")]
    [Tooltip("Optional. If assigned, each session shot plays the shooter's shooting animation and the " +
             "ball launches on its release frame. Leave empty to launch instantly.")]
    public ShooterAnimationController shooterAnimation;

    [Header("UI (World Space Canvas)")]
    [Tooltip("TextMeshPro label used for GET READY / countdown digits / GO!. Leave unassigned to disable.")]
    public TextMeshProUGUI countdownText;

    [Tooltip("Shown smaller on a second line under GET READY in the countdown, e.g. 'College'. " +
             "Empty = plain GET READY. Set by DifficultySelector.")]
    public string readyLabel = "";

    // ── Private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private GoalDetector _goalDetector;
    private FloorBoundary _floorBoundary;
    private BallFlight _ballFlight;   // optional: air drag + drag-aware aim

    // falling state (custom gravity after the plane; only when useUnityGravity is off)
    private bool _falling = false;

    // session state
    private bool _sessionRunning = false;
    private int _shotsFiredThisSession = 0;

    // shot waiting on the shooter animation's release frame
    private bool _awaitingRelease = false;
    private Coroutine _releaseWatchdog;

    [Tooltip("If the shooter animation hasn't released within this many seconds, launch the shot " +
             "anyway so a session can't hang.")]
    [Min(0.5f)]
    public float releaseWatchdogSeconds = 5f;
    private Quadrant _pendingQuadrant;

    // pre-start countdown display state
    private bool _showCountdown = false;
    private float _countdownRemaining = 0f;
    private float _goDisplayTimer = 0f;
    // Short so "GO!" is gone before the first windup reaches release.
    private const float GoDisplayDuration = 0.5f;

    // last target (used by Gizmos to show where the next/last shot went)
    private Vector3 _lastTarget;
    private bool _hasTarget = false;
    private Quadrant _lastQuadrant;

    // ── Events ───────────────────────────────────────────────────

    /// <summary>True from the start of the pre-start countdown until the session ends.</summary>
    public bool IsSessionRunning => _sessionRunning;

    /// <summary>Quadrant the most recent shot was aimed at. Set in LaunchToward before
    /// GoalDetector.OnShotLaunched fires, so a listener to that event reads this shot's aim.</summary>
    public Quadrant LastAimQuadrant => _lastQuadrant;

    /// <summary>Where the most recent shot was aimed, normalized to the gate ([-1, 1],
    /// QuadrantMath.NormalizeToGate). Set at the same time as LastAimQuadrant.</summary>
    public Vector2 LastAimNormalized { get; private set; }

    /// <summary>Release speed (m/s) of the most recent shot, after the per-shot variance.</summary>
    public float LastLaunchSpeed { get; private set; }

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
        _ballFlight = GetComponent<BallFlight>();

        // The ballistic aim assumes no damping (BallFlight adds real drag itself). Interpolation smooths the arc between physics steps.
#if UNITY_6000_0_OR_NEWER
        _rb.linearDamping = 0f;
#else
        _rb.drag = 0f;
#endif
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (useUnityGravity && Physics.gravity.sqrMagnitude < 0.001f)
        {
            Debug.LogWarning("[RandomLauncher] Use Unity Gravity is on but Physics.gravity is ~zero — shots " +
                             "will fly straight and the arc aim will be wrong. Check Project Settings → Physics.");
        }
    }

    void OnEnable()
    {
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;
        _goalDetector.OnSaved += HandleSaved;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += HandleDespawned;
        if (shooterAnimation != null)
            shooterAnimation.OnRelease += HandleShooterRelease;
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        _goalDetector.OnSaved -= HandleSaved;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;
        if (shooterAnimation != null)
            shooterAnimation.OnRelease -= HandleShooterRelease;
    }

    void Start()
    {
        if (launchOrigin == null)
        {
            Debug.LogWarning("[RandomLauncher] LaunchOrigin is not set. Using the ball's current position as origin. " +
                             "Assign a Transform in the Inspector for more realistic shots.");
        }

        // The ball only appears once it's shot — LaunchToward() shows it again.
        if (_floorBoundary != null)
            _floorBoundary.Hide();

        // The trigger no longer starts a session, so on device the START target is the only way in.
        if (FindFirstObjectByType<GazeStartTarget>() == null)
            Debug.LogWarning("[RandomLauncher] No GazeStartTarget in the scene — on device there's no way to " +
                             "start a session. Add a StartTarget (see Plans/Current/Input_Styles_Plan.md).");

        Debug.Log("[RandomLauncher] Waiting for session start (gaze at START"
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

        UpdateCountdownText();
    }

    /// <summary>Called by GoalDetector whenever this ball crosses the gate plane, make or miss,
    /// so the ball always has a defined path (falling to the floor) past the plane. With Unity
    /// gravity on, it's already falling; adding custom gravity would double it.</summary>
    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        if (!useUnityGravity && enableCustomGravityOnGoal)
            BeginFalling();
    }

    /// <summary>Called by GoalDetector when the goalie's stick saves the shot. The ball never
    /// crosses the gate plane, so it doesn't fall — it freezes where it was touched and
    /// despawns. That despawn raises FloorBoundary.OnDespawned, which continues the session
    /// exactly as a floor landing does.</summary>
    private void HandleSaved(Vector3 projectedPos)
    {
        if (_floorBoundary == null)
        {
            Debug.LogWarning("[RandomLauncher] Shot was saved but there's no FloorBoundary on the ball to " +
                             "despawn it — the session can't continue. Add a FloorBoundary component.");
            return;
        }

        _floorBoundary.DespawnAfter(saveDespawnDelay, freezeInPlace: true);
    }

    /// <summary>Called by FloorBoundary once the ball despawns (after landing on the floor, or
    /// after a save). Continues the session (next shot after a randomized interval) until ShotsPerSession is
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

    /// <summary>Starts a new session (pre-start countdown, then ShotsPerSession shots) if one
    /// isn't already running. Safe to call from any start source (GazeStartTarget, Space in the
    /// Editor). The controller trigger no longer starts a session: it only clicks the settings
    /// page, so a stray pull can't start one before the goalie is ready.</summary>
    public void TryStartSession()
    {
        if (_sessionRunning) return;
        StartCoroutine(RunSession());
    }

    private IEnumerator RunSession()
    {
        _sessionRunning = true;
        _shotsFiredThisSession = 0;
        _awaitingRelease = false;

        // The ball stays out of sight until it's shot — LaunchToward() shows it again.
        if (_floorBoundary != null)
            _floorBoundary.Hide();

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

        if (shooterAnimation == null || !shooterAnimation.IsAvailable)
        {
            LaunchBall();
            return;
        }

        // Pick the quadrant now, but launch on the animation's release frame
        // (HandleShooterRelease), so the ball leaves the stick when the avatar shoots.
        _pendingQuadrant = PickRandomQuadrant();
        _awaitingRelease = true;
        if (_releaseWatchdog != null) StopCoroutine(_releaseWatchdog);
        _releaseWatchdog = StartCoroutine(ReleaseWatchdog());
        shooterAnimation.PlayShot();
    }

    /// <summary>Launches the pending shot anyway if the shooter's release never arrives
    /// (animation disabled or stalled), so a session can never hang waiting on it.</summary>
    private IEnumerator ReleaseWatchdog()
    {
        yield return new WaitForSeconds(releaseWatchdogSeconds);
        _releaseWatchdog = null;

        if (!_awaitingRelease) yield break;

        Debug.LogWarning($"[RandomLauncher] Shooter release didn't arrive within {releaseWatchdogSeconds}s " +
                         "— launching anyway.");
        _awaitingRelease = false;
        LaunchToward(_pendingQuadrant);
    }

    /// <summary>Called by ShooterAnimationController on the shooting clip's release frame.
    /// Ignores releases no session shot is waiting on (e.g. its Test Shot context menu).</summary>
    private void HandleShooterRelease()
    {
        if (!_awaitingRelease) return;

        _awaitingRelease = false;
        if (_releaseWatchdog != null)
        {
            StopCoroutine(_releaseWatchdog);
            _releaseWatchdog = null;
        }
        LaunchToward(_pendingQuadrant);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Randomly picks one of the four quadrants, logs it, and launches the ball toward it
    /// using the current <see cref="aimMode"/>. Safe to call from other scripts or Unity Events at
    /// any time — this fires a single shot immediately, outside the session flow and without the
    /// shooter animation (session shots go through FireNextShot()).
    /// </summary>
    public void LaunchBall()
    {
        LaunchToward(PickRandomQuadrant());
    }

    /// <summary>
    /// Launches toward a specific quadrant. Useful for scripted sequences or AI-driven shot selection.
    /// </summary>
    public void LaunchToward(Quadrant quadrant)
    {
        // ── 1. Determine launch origin position ──────────────────
        Vector3 origin = launchOrigin != null ? launchOrigin.position : transform.position;

        // ── 2. Reset the ball position & physics ─────────────────
        // Always un-hide the ball (it's hidden until shot), even if autoResetOnLaunch is off.
        _floorBoundary?.CancelDespawn();

        if (autoResetOnLaunch)
        {
            _goalDetector.ResetState();

            _rb.isKinematic = false;
            _rb.useGravity = false;

#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector3.zero;
#else
            _rb.velocity = Vector3.zero;
#endif
            _rb.angularVelocity = Vector3.zero;

            // Teleport through the Rigidbody too. With interpolation on (set in Awake) and
            // Auto Sync Transforms off, a transform-only move can be overwritten by the
            // interpolated pose before physics reads it, so the ball would launch from where
            // the last shot ended (floor, or frozen at the goalie's stick) instead of here.
            // Setting the transform as well lets OnBallLaunched() read the origin right away.
            _rb.position = origin;
            transform.position = origin;

            _falling = false;
        }

        // ── 3. Pick a target inside the chosen quadrant ──────────
        Vector3 target = ComputeTarget(quadrant);
        _lastTarget = target;
        _hasTarget = true;
        _lastQuadrant = quadrant;
        LastAimNormalized = QuadrantMath.NormalizeToGate(
            _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, target);

        // ── 4. Compute launch velocity ───────────────────────────
        // Gravity turns on only here, with the launch velocity, so the ball can't pick up a
        // frame of fall while it's being moved to the origin above.
        // With BallFlight, the aim is corrected for drag and spin (Plans/Past/Ball_Flight_Physics_Plan.md).
        // Without gravity there's no spin, and drag only slows the straight shot, so the straight
        // aim stays exact.
        // Speed varies per shot (launchSpeedVariance) so the goalie can't time a fixed speed.
        // The spread is capped at half of launchSpeed, so a big variance on a slow base speed
        // can't roll a shot too slow to reach the goal.
        float spread = Mathf.Min(launchSpeedVariance, launchSpeed * 0.5f);
        float shotSpeed = Mathf.Max(1f, launchSpeed + Random.Range(-spread, spread));

        Vector3 launchVelocity;
        Vector3 spin = Vector3.zero;
        float flightTime;
        if (useUnityGravity && _ballFlight != null)
        {
            launchVelocity = _ballFlight.SolveLaunchVelocity(origin, target, shotSpeed, out spin);
            flightTime = 0f;   // set from the prediction below
        }
        else if (useUnityGravity)
        {
            launchVelocity = QuadrantMath.ComputeBallisticLaunchVelocity(
                origin, target, shotSpeed, Physics.gravity, Time.fixedDeltaTime, out flightTime);
        }
        else
        {
            launchVelocity = QuadrantMath.ComputeLaunchVelocity(origin, target, shotSpeed);
            flightTime = Vector3.Distance(origin, target) / Mathf.Max(launchVelocity.magnitude, 0.001f);
        }

        _rb.isKinematic = false;
        _rb.useGravity = useUnityGravity;

        // ── 5. Apply and notify GoalDetector ─────────────────────
#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity = launchVelocity;
#else
        _rb.velocity = launchVelocity;
#endif

        _falling = false;
        LastLaunchSpeed = launchVelocity.magnitude;

        _ballFlight?.BeginFlight(spin);
        _goalDetector.OnBallLaunched();

        float speed = launchVelocity.magnitude;
        float angle = speed > 0.001f ? Mathf.Asin(Mathf.Clamp(launchVelocity.y / speed, -1f, 1f)) * Mathf.Rad2Deg : 0f;

        // Arrival speed is what the goalie actually faces after drag.
        string speedText = $"{speed:F1} m/s";
        if (_ballFlight != null &&
            _ballFlight.PredictPlaneCrossing(origin, launchVelocity, target.z, useUnityGravity,
                                             out _, out float predictedTime, out Vector3 arrivalVelocity))
        {
            flightTime = predictedTime;
            speedText = $"{speed:F1} → {arrivalVelocity.magnitude:F1} m/s";
        }
        if (spin != Vector3.zero)
            speedText += $" | spin {spin.magnitude / (2f * Mathf.PI):F0} rev/s";

        Debug.Log($"[RandomLauncher] Shot fired → {quadrant} ({aimMode}) | " +
                  $"target ({target.x:F2}, {target.y:F2}, {target.z:F2}) | " +
                  $"speed {speedText} | {angle:+0.0;-0.0}° | {flightTime:F2} s");
    }

    // ── Private helpers ───────────────────────────────────────────

    private Quadrant PickRandomQuadrant()
    {
        Quadrant[] quads = (Quadrant[])System.Enum.GetValues(typeof(Quadrant));
        Quadrant quadrant = quads[Random.Range(0, quads.Length)];

        Debug.Log($"[RandomLauncher] Aiming at quadrant: {quadrant}");
        return quadrant;
    }

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
    /// Enables falling by applying custom gravity inside FixedUpdate. Only used when
    /// useUnityGravity is off.
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

    private void UpdateCountdownText()
    {
        if (countdownText == null) return;

        if (_showCountdown)
        {
            DrawCountdown();
        }
        else if (_goDisplayTimer > 0f)
        {
            float progress = 1f - (_goDisplayTimer / GoDisplayDuration);
            SetLabel("GO!", UiPalette.Chalk, progress, animate: true);
        }
        else
        {
            countdownText.gameObject.SetActive(false);
        }
    }

    private void DrawCountdown()
    {
        if (_countdownRemaining > 3f)
        {
            // Level on its own smaller line: no wider than GET READY, and no '·' glyph that the
            // countdown font might lack.
            string ready = string.IsNullOrEmpty(readyLabel)
                ? "GET READY"
                : $"GET READY\n<size=60%>{readyLabel}</size>";
            SetLabel(ready, UiPalette.Chalk, 0f, animate: false);
            return;
        }

        int digit = Mathf.Clamp(Mathf.CeilToInt(_countdownRemaining), 1, 3);
        // fractionInSecond goes 1 (digit just appeared) -> 0 (digit about to change), so
        // progress goes 0 -> 1 across the digit's one-second window.
        float fractionInSecond = _countdownRemaining - (digit - 1);
        float progress = Mathf.Clamp01(1f - fractionInSecond);
        SetLabel(digit.ToString(), UiPalette.Chalk, progress, animate: true);
    }

    /// <summary>Same scale-pop/fade beat as the old OnGUI overlay, now applied to a
    /// world-space TextMeshPro label instead of an IMGUI Rect.</summary>
    private void SetLabel(string text, Color color, float progress, bool animate)
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

        countdownText.gameObject.SetActive(true);
        countdownText.text = text;

        color.a = alpha;
        countdownText.color = color;
        countdownText.rectTransform.localScale = Vector3.one * scale;
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
