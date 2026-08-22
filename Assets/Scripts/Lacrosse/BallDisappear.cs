using UnityEngine;

/// <summary>
/// Detects when the lacrosse ball passes through the 2D goal gate plane.
/// Attach to the same GameObject as BallLauncher and Rigidbody.
///
/// After the ball crosses the gate plane it continues flying freely for
/// <see cref="lingerDuration"/> seconds, then the GameObject is deactivated
/// (hidden and removed from physics).
///
/// Quadrant layout (facing the goal):
///   TopLeft    | TopRight
///   -----------+-----------
///   BottomLeft | BottomRight
/// </summary>
public class BallDisappear : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("Goal Gate")]
    [Tooltip("World-space center of the goal rectangle. Z = the depth of the gate plane.")]
    public Vector3 goalGateCenter = new Vector3(0f, 1f, -3f);

    [Tooltip("Half-extents of the goal rectangle. X = half-width, Y = half-height.")]
    public Vector2 goalGateHalfSize = new Vector2(0.9f, 0.6f);

    [Header("Post-Crossing Behaviour")]
    [Tooltip("How many seconds the ball keeps travelling after it crosses the goal plane " +
             "before the GameObject is deactivated. Set to 0 to disappear immediately.")]
    [Min(0f)]
    public float lingerDuration = 1.5f;

    [Header("UI")]
    [Tooltip("Show a GOAL! overlay when the ball scores.")]
    public bool showOnScreenGoal = true;

    // ── state ─────────────────────────────────────────────────────

    private Rigidbody _rb;
    private bool _active = false;       // true after OnBallLaunched()
    private bool _goalScored = false;
    private bool _crossedPlane = false; // true once the ball has crossed the gate plane (goal or miss)
    private float _lingerTimer = 0f;    // counts down after plane crossing
    private float _displayTimer = 0f;
    private float _prevZ;

    private const float DisplayDuration = 3f;

    // ── lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    void Update()
    {
        // Tick the GOAL! display countdown
        if (_displayTimer > 0f)
            _displayTimer -= Time.deltaTime;

        // ── Linger countdown (runs after any plane crossing) ──────
        if (_crossedPlane)
        {
            _lingerTimer -= Time.deltaTime;
            if (_lingerTimer <= 0f)
            {
                _crossedPlane = false; // prevent re-entry
                gameObject.SetActive(false);
                Debug.Log("[GoalDetector] Ball deactivated after linger period.");
            }
            return; // no need to keep checking the gate plane
        }

        if (!_active) return;

        float currentZ = transform.position.z;

        // Did the ball cross the gate plane this frame?
        bool planeCrossed = (_prevZ > goalGateCenter.z && currentZ <= goalGateCenter.z)
                         || (_prevZ < goalGateCenter.z && currentZ >= goalGateCenter.z);

        if (planeCrossed)
        {
            // Interpolate back to find the exact crossing position
            float t = Mathf.InverseLerp(_prevZ, currentZ, goalGateCenter.z);

#if UNITY_6000_0_OR_NEWER
            Vector3 velocity = _rb.linearVelocity;
#else
            Vector3 velocity = _rb.velocity;
#endif
            Vector3 prevPos = transform.position - velocity * Time.deltaTime;
            Vector3 crossingPos = Vector3.Lerp(prevPos, transform.position, t);

            float dx = Mathf.Abs(crossingPos.x - goalGateCenter.x);
            float dy = Mathf.Abs(crossingPos.y - goalGateCenter.y);

            bool insideGate = dx <= goalGateHalfSize.x && dy <= goalGateHalfSize.y;

            if (insideGate)
            {
                _goalScored = true;
                _displayTimer = DisplayDuration;
                Debug.Log($"[GoalDetector] GOAL! Crossed gate at " +
                          $"({crossingPos.x:F2}, {crossingPos.y:F2}, {goalGateCenter.z:F2})");
            }
            else
            {
                Debug.Log($"[GoalDetector] Miss — ball crossed plane outside gate at " +
                          $"({crossingPos.x:F2}, {crossingPos.y:F2})");
            }

            // Either way, start the linger countdown and let the ball keep flying.
            _crossedPlane = true;
            _lingerTimer = lingerDuration;
        }

        _prevZ = currentZ;
    }

    // ── public API (called by BallLauncher) ───────────────────────

    /// <summary>Call this immediately after the ball is launched.</summary>
    public void OnBallLaunched()
    {
        _active = true;
        _goalScored = false;
        _crossedPlane = false;
        _lingerTimer = 0f;
        _prevZ = transform.position.z;
    }

    /// <summary>Resets all detection state ready for the next shot.</summary>
    public void ResetState()
    {
        _active = false;
        _goalScored = false;
        _crossedPlane = false;
        _lingerTimer = 0f;
        _displayTimer = 0f;

        // Re-activate in case it was deactivated by the linger system.
        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    // ── on-screen UI ──────────────────────────────────────────────

    void OnGUI()
    {
        if (!showOnScreenGoal || _displayTimer <= 0f) return;

        float progress = 1f - (_displayTimer / DisplayDuration);
        float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));

        float scale = Mathf.Lerp(2.0f, 1.0f, eased);
        scale *= 1.0f + 0.05f * Mathf.Sin(eased * Mathf.PI * 4f);

        float alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(progress));

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
        GUI.Label(new Rect(4f, Screen.height * 0.3f + 4f, Screen.width, 120f), "GOAL!", shadow);

        GUIStyle fg = new GUIStyle(baseStyle);
        Color fgColor = Color.yellow;
        fgColor.a = alpha;
        fg.normal.textColor = fgColor;
        GUI.Label(new Rect(0f, Screen.height * 0.3f, Screen.width, 120f), "GOAL!", fg);

        GUI.matrix = oldMatrix;
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(goalGateCenter,
            new Vector3(goalGateHalfSize.x * 2f, goalGateHalfSize.y * 2f, 0.05f));

        Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
        Vector3 c = goalGateCenter;
        Vector2 h = goalGateHalfSize;
        Gizmos.DrawLine(new Vector3(c.x, c.y - h.y, c.z), new Vector3(c.x, c.y + h.y, c.z));
        Gizmos.DrawLine(new Vector3(c.x - h.x, c.y, c.z), new Vector3(c.x + h.x, c.y, c.z));
    }
}