using System.Collections;
using UnityEngine;

/// <summary>
/// Launches the lacrosse ball at a chosen goal quadrant from a fixed release position.
/// No manual angle or speed inputs — the launcher solves projectile-motion equations
/// (accounting for gravity and gravityScale from LacrossBallPhysics) to compute
/// exactly what velocity is needed to reach the target quadrant.
///
/// HOW TO USE
/// ──────────
/// 1. Assign lacrosseBall and goalDetector in the Inspector.
/// 2. Set fixedReleasePosition to the world-space point the ball always spawns from.
/// 3. Call LaunchAtQuadrant(QuadrantTarget q) from any external script, OR
///    press 1/2/3/4 in Play mode to test each quadrant:
///      1 = Top Left       2 = Top Right
///      3 = Bottom Left    4 = Bottom Right
///
/// PHYSICS SOLVER
/// ──────────────
/// Given:
///   D  = horizontal distance from release to target
///   H  = vertical rise  (release.y → target.y)
///   g  = |Physics.gravity.y| × gravityScale  (from LacrossBallPhysics)
///
/// Standard ballistic formula solved for launch speed v at angle θ:
///   v² = (g · D²) / (2 · cos²θ · (D·tanθ − H))
///
/// The solver tries angles from preferredLaunchAngle outward in both directions
/// and picks the first angle whose resulting speed is in [minSolverSpeed, maxSolverSpeed].
/// </summary>
public class BallLauncher : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("References")]
    public GameObject lacrosseBall;
    public GoalDetector goalDetector;

    [Header("Fixed Release Position")]
    [Tooltip("World-space point the ball always spawns from before every launch.")]
    public Vector3 fixedReleasePosition = new Vector3(0f, 1f, 5f);

    [Header("Quadrant Target")]
    [Tooltip("Which quadrant to aim at. Change at runtime, or call LaunchAtQuadrant().")]
    public QuadrantTarget targetQuadrant = QuadrantTarget.TopLeft;

    [Tooltip("0 = quadrant centre, 1 = quadrant corner. 0.7 is a good realistic value.")]
    [Range(0f, 1f)]
    public float quadrantInsetFactor = 0.7f;

    [Header("Solver Constraints")]
    [Tooltip("Preferred launch angle (degrees above horizontal). " +
             "Solver starts here and searches nearby angles if speed is out of range.")]
    [Range(5f, 80f)]
    public float preferredLaunchAngle = 20f;

    [Tooltip("Minimum acceptable launch speed (m/s).")]
    public float minSolverSpeed = 4f;

    [Tooltip("Maximum acceptable launch speed (m/s).")]
    public float maxSolverSpeed = 35f;

    [Header("UI")]
    public bool showOnScreenUI = true;

    // ── Quadrant enum ─────────────────────────────────────────────

    public enum QuadrantTarget { TopLeft, TopRight, BottomLeft, BottomRight }

    // ── private state ─────────────────────────────────────────────

    private Rigidbody _rb;
    private LacrossBallPhysics _ballPhysics;
    private GoalDetector _goalDetector;

    private bool _readyToLaunch = true;
    private string _lastQuadrantName = "";
    private float _labelTimer = 0f;
    private const float LabelDuration = 2.5f;

    // ── lifecycle ─────────────────────────────────────────────────

    void Start()
    {
        _rb = lacrosseBall.GetComponent<Rigidbody>();
        _ballPhysics = lacrosseBall.GetComponent<LacrossBallPhysics>();
        _goalDetector = goalDetector != null
            ? goalDetector
            : lacrosseBall.GetComponent<GoalDetector>();
    }

    void Update()
    {
        if (_labelTimer > 0f) _labelTimer -= Time.deltaTime;
        if (!_readyToLaunch) return;

        // Number-key shortcuts for play-mode testing
        if (Input.GetKeyDown(KeyCode.Alpha1)) Launch(QuadrantTarget.TopLeft);
        else if (Input.GetKeyDown(KeyCode.Alpha2)) Launch(QuadrantTarget.TopRight);
        else if (Input.GetKeyDown(KeyCode.Alpha3)) Launch(QuadrantTarget.BottomLeft);
        else if (Input.GetKeyDown(KeyCode.Alpha4)) Launch(QuadrantTarget.BottomRight);
    }

    // ── public API ────────────────────────────────────────────────

    /// <summary>Fire at a specific quadrant. Safe to call from any external script.</summary>
    public void LaunchAtQuadrant(QuadrantTarget quadrant)
    {
        if (!_readyToLaunch) return;
        Launch(quadrant);
    }

    /// <summary>Fire at whichever quadrant is currently set in the Inspector field.</summary>
    public void LaunchAtCurrentQuadrant() => LaunchAtQuadrant(targetQuadrant);

    // ── internal launch flow ──────────────────────────────────────

    private void Launch(QuadrantTarget quadrant)
    {
        targetQuadrant = quadrant;
        _readyToLaunch = false;
        StartCoroutine(LaunchRoutine(quadrant));
    }

    private System.Collections.IEnumerator LaunchRoutine(QuadrantTarget quadrant)
    {
        // Reset physics & goal state
        _ballPhysics?.ResetPhysicsState();
        _goalDetector?.ResetState();

        // Snap ball to fixed release point
        lacrosseBall.transform.position = fixedReleasePosition;
        lacrosseBall.transform.rotation = Quaternion.identity;

        yield return new WaitForFixedUpdate(); // let physics settle one step

        // Resolve target world position
        Vector3 target = GetQuadrantWorldPos(quadrant, out string quadName);
        _lastQuadrantName = quadName;
        _labelTimer = LabelDuration;

        // Solve trajectory
        bool solved = TrySolveTrajectory(fixedReleasePosition, target, out Vector3 launchVel);

        if (!solved)
        {
            Debug.LogWarning($"[BallLauncher] Solver found no valid speed in " +
                             $"[{minSolverSpeed}, {maxSolverSpeed}] m/s for '{quadName}'. " +
                             $"Using direct-aim fallback.");
            launchVel = (target - fixedReleasePosition).normalized * minSolverSpeed;
        }

        _rb.linearVelocity = launchVel;
        _rb.angularVelocity = Vector3.zero;

        _goalDetector?.OnBallLaunched();
        _readyToLaunch = true;

        Debug.Log($"[BallLauncher] Fired → {quadName} | " +
                  $"target {target} | speed {launchVel.magnitude:F1} m/s | " +
                  $"vel {launchVel}");
    }

    // ── quadrant geometry ─────────────────────────────────────────

    private Vector3 GetQuadrantWorldPos(QuadrantTarget q, out string label)
    {
        Vector3 c = _goalDetector.goalGateCenter;
        Vector2 h = _goalDetector.goalGateHalfSize;
        float f = quadrantInsetFactor;

        switch (q)
        {
            case QuadrantTarget.TopLeft:
                label = "Top Left";
                return new Vector3(c.x - h.x * f, c.y + h.y * f, c.z);

            case QuadrantTarget.TopRight:
                label = "Top Right";
                return new Vector3(c.x + h.x * f, c.y + h.y * f, c.z);

            case QuadrantTarget.BottomLeft:
                label = "Bottom Left";
                return new Vector3(c.x - h.x * f, c.y - h.y * f, c.z);

            default: // BottomRight
                label = "Bottom Right";
                return new Vector3(c.x + h.x * f, c.y - h.y * f, c.z);
        }
    }

    // ── projectile-motion solver ──────────────────────────────────

    /// <summary>
    /// Finds a launch velocity that carries the ball from <paramref name="from"/>
    /// to <paramref name="to"/> under the effective gravity used by LacrossBallPhysics.
    ///
    /// The horizontal direction is always toward the target (XZ plane).
    /// The vertical component is solved analytically per candidate launch angle.
    /// </summary>
    private bool TrySolveTrajectory(Vector3 from, Vector3 to, out Vector3 velocity)
    {
        velocity = Vector3.zero;

        float gScale = (_ballPhysics != null) ? _ballPhysics.gravityScale : 1f;
        float g = Mathf.Abs(Physics.gravity.y) * gScale; // positive magnitude

        Vector3 delta = to - from;
        float H = delta.y;
        Vector3 horizDelta = new Vector3(delta.x, 0f, delta.z);
        float D = horizDelta.magnitude;

        if (D < 0.001f)
        {
            // Directly above/below: shoot straight up
            float upSpeed = Mathf.Sqrt(Mathf.Max(0f, 2f * g * H));
            velocity = Vector3.up * upSpeed;
            return upSpeed >= minSolverSpeed && upSpeed <= maxSolverSpeed;
        }

        Vector3 horizDir = horizDelta.normalized;

        foreach (float angleDeg in AngleCandidates(preferredLaunchAngle))
        {
            float theta = angleDeg * Mathf.Deg2Rad;
            float cosT = Mathf.Cos(theta);
            float tanT = Mathf.Tan(theta);
            float cos2T = cosT * cosT;

            // Derived from:  H = D·tanθ − (g·D²)/(2·v²·cos²θ)
            // ⟹  v² = (g·D²) / (2·cos²θ·(D·tanθ − H))
            float denom = 2f * cos2T * (D * tanT - H);
            if (denom <= 0f) continue;

            float v2 = (g * D * D) / denom;
            if (v2 <= 0f) continue;

            float v = Mathf.Sqrt(v2);
            if (v < minSolverSpeed || v > maxSolverSpeed) continue;

            // Success — build the 3-D velocity vector
            velocity = horizDir * (v * cosT) + Vector3.up * (v * Mathf.Sin(theta));
            return true;
        }

        return false; // no valid solution found
    }

    /// <summary>
    /// Generates launch angle candidates starting at <paramref name="preferred"/>,
    /// then alternating ±5° steps outward up to the valid [5°, 80°] range.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<float> AngleCandidates(float preferred)
    {
        yield return preferred;
        for (float d = 5f; d <= 75f; d += 5f)
        {
            float hi = preferred + d;
            float lo = preferred - d;
            if (hi <= 80f) yield return hi;
            if (lo >= 5f) yield return lo;
        }
    }

    // ── on-screen UI ──────────────────────────────────────────────

    void OnGUI()
    {
        if (!showOnScreenUI) return;

        // Fading "fired at" label
        if (_labelTimer > 0f && _lastQuadrantName.Length > 0)
        {
            float a = Mathf.Clamp01(_labelTimer / LabelDuration);
            DrawCenteredLabel($"► {_lastQuadrantName}", Screen.height * 0.15f, 40,
                              new Color(1f, 0.85f, 0.1f, a));
        }

        // Static key-hint at the bottom
        DrawCenteredLabel("1 = Top Left   2 = Top Right   3 = Bottom Left   4 = Bottom Right",
                          Screen.height - 40f, 15, new Color(1f, 1f, 1f, 0.55f));
    }

    private static void DrawCenteredLabel(string text, float y, int size, Color col)
    {
        GUIStyle s = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        // Shadow
        s.normal.textColor = new Color(0f, 0f, 0f, col.a * 0.75f);
        GUI.Label(new Rect(3, y + 3, Screen.width, size + 10), text, s);

        // Foreground
        s.normal.textColor = col;
        GUI.Label(new Rect(0, y, Screen.width, size + 10), text, s);
    }
}