using System;
using TMPro;
using UnityEngine;

/// <summary>
/// Single source of truth for detecting when the lacrosse ball passes through the 2D
/// goal gate plane. Attach to the same GameObject as the launcher and Rigidbody.
///
/// Other components (e.g. BallDisappear, ball launchers) should subscribe to
/// <see cref="OnPlaneCrossed"/> / <see cref="OnGoalScored"/> instead of re-implementing
/// plane-crossing detection.
///
/// Quadrant layout (facing the goal):
///   TopLeft    | TopRight
///   -----------+-----------
///   BottomLeft | BottomRight
/// </summary>
public class GoalDetector : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("Goal Gate")]
    [Tooltip("World-space center of the goal rectangle. Z = the depth of the gate plane.")]
    public Vector3 goalGateCenter = new Vector3(0f, 1f, -3f);

    [Tooltip("Half-extents of the goal rectangle. X = half-width, Y = half-height.")]
    public Vector2 goalGateHalfSize = new Vector2(0.9f, 0.6f);

    [Header("UI")]
    [Tooltip("Show a GOAL! overlay when the ball scores.")]
    public bool showOnScreenGoal = true;

    [Tooltip("TextMeshPro label used for the GOAL! popup. Leave unassigned to disable.")]
    public TextMeshProUGUI goalText;

    // ── state ─────────────────────────────────────────────────────

    private Rigidbody _rb;
    private bool _active = false;   // true after OnBallLaunched()
    private bool _goalScored = false;
    private float _displayTimer = 0f;
    private float _prevZ;

    private const float DisplayDuration = 3f;

    // ── events ────────────────────────────────────────────────────

    /// <summary>Fired whenever the ball crosses the gate plane, whether it's a goal or a miss.</summary>
    public event Action<Vector3> OnPlaneCrossed;

    /// <summary>Fired once when the ball crosses the gate plane inside the goal bounds.</summary>
    public event Action<Vector3> OnGoalScored;

    /// <summary>True once the current shot has scored.</summary>
    public bool GoalScored => _goalScored;

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

        UpdateGoalText();

        if (!_active || _goalScored) return;

        float currentZ = transform.position.z;

        // Did the ball cross the gate plane this frame?
        bool crossedPlane = (_prevZ > goalGateCenter.z && currentZ <= goalGateCenter.z)
                         || (_prevZ < goalGateCenter.z && currentZ >= goalGateCenter.z);

        if (crossedPlane)
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
                OnGoalScored?.Invoke(crossingPos);
            }
            else
            {
                Debug.Log($"[GoalDetector] Miss — ball crossed plane outside gate at " +
                          $"({crossingPos.x:F2}, {crossingPos.y:F2})");
            }

            OnPlaneCrossed?.Invoke(crossingPos);
        }

        _prevZ = currentZ;
    }

    // ── public API (called by BallLauncher) ───────────────────────

    /// <summary>Call this immediately after the ball is launched.</summary>
    public void OnBallLaunched()
    {
        _active = true;
        _goalScored = false;
        _prevZ = transform.position.z;
    }

    /// <summary>Resets all detection state ready for the next shot.</summary>
    public void ResetState()
    {
        _active = false;
        _goalScored = false;
        _displayTimer = 0f;
    }

    // ── on-screen UI ──────────────────────────────────────────────

    /// <summary>Same scale-pop/fade beat as the old OnGUI overlay, now applied to a
    /// world-space TextMeshPro label instead of an IMGUI Rect.</summary>
    private void UpdateGoalText()
    {
        if (goalText == null) return;

        if (!showOnScreenGoal || _displayTimer <= 0f)
        {
            goalText.gameObject.SetActive(false);
            return;
        }

        // progress goes 0 -> 1 over DisplayDuration
        float progress = 1f - (_displayTimer / DisplayDuration);
        float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));

        // scale anim: pop from larger to normal
        float scale = Mathf.Lerp(2.0f, 1.0f, eased);
        // small subtle oscillation to make it feel lively
        scale *= 1.0f + 0.05f * Mathf.Sin(eased * Mathf.PI * 4f);

        // fade out over time
        float alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(progress));

        goalText.gameObject.SetActive(true);
        goalText.text = "GOAL!";

        Color color = Color.yellow;
        color.a = alpha;
        goalText.color = color;
        goalText.rectTransform.localScale = Vector3.one * scale;
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        // Gate outline
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(goalGateCenter,
            new Vector3(goalGateHalfSize.x * 2f, goalGateHalfSize.y * 2f, 0.05f));

        // Centre crosshair
        Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
        Vector3 c = goalGateCenter;
        Vector2 h = goalGateHalfSize;
        Gizmos.DrawLine(new Vector3(c.x, c.y - h.y, c.z), new Vector3(c.x, c.y + h.y, c.z));
        Gizmos.DrawLine(new Vector3(c.x - h.x, c.y, c.z), new Vector3(c.x + h.x, c.y, c.z));
    }
}