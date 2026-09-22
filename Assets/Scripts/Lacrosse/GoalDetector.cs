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
/// It also owns the "save" outcome: GoalieSaveZone (the goalie's stick) calls
/// <see cref="TryRegisterSave"/> when it touches the ball mid-flight, which ends the shot
/// (no plane crossing will follow) and raises <see cref="OnSaved"/>. A shot ends exactly
/// once — as a goal, a miss, or a save.
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
    [Tooltip("Show a GOAL! / SAVE! overlay when the shot ends in a goal or a save.")]
    public bool showOnScreenGoal = true;

    [Tooltip("TextMeshPro label used for the GOAL! / SAVE! popup. Leave unassigned to disable.")]
    public TextMeshProUGUI goalText;

    // ── state ─────────────────────────────────────────────────────

    private Rigidbody _rb;
    private bool _active = false;   // true after OnBallLaunched()
    private bool _goalScored = false;
    private bool _saved = false;
    private bool _resolved = false; // the shot has ended: goal, miss, or save
    private float _displayTimer = 0f;
    private float _prevZ;

    // what the popup currently says (set when a goal or save happens)
    private string _popupText = "GOAL!";
    private Color _popupColor = Color.yellow;

    private const float DisplayDuration = 3f;

    // ── events ────────────────────────────────────────────────────

    /// <summary>Fired whenever the ball crosses the gate plane, whether it's a goal or a miss.</summary>
    public event Action<Vector3> OnPlaneCrossed;

    /// <summary>Fired once when the ball crosses the gate plane inside the goal bounds.</summary>
    public event Action<Vector3> OnGoalScored;

    /// <summary>Fired once when the goalie's stick saves the shot. The payload is where the
    /// ball's straight-line path meets the gate plane (the same meaning as the
    /// <see cref="OnPlaneCrossed"/> payload), not where the stick touched it.</summary>
    public event Action<Vector3> OnSaved;

    /// <summary>True once the current shot has scored.</summary>
    public bool GoalScored => _goalScored;

    /// <summary>True once the current shot has been saved.</summary>
    public bool Saved => _saved;

    /// <summary>True while a shot has been launched and hasn't ended yet (no goal, miss, or
    /// save so far). A save can only be registered while this is true.</summary>
    public bool ShotInFlight => _active && !_resolved;

    /// <summary>World position of the ball when the current shot was saved.</summary>
    public Vector3 SaveContactPosition { get; private set; }

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

        if (!_active || _resolved) return;

        float currentZ = transform.position.z;

        // Did the ball cross the gate plane this frame?
        bool crossedPlane = (_prevZ > goalGateCenter.z && currentZ <= goalGateCenter.z)
                         || (_prevZ < goalGateCenter.z && currentZ >= goalGateCenter.z);

        if (crossedPlane)
        {
            // The first crossing ends the shot (goal or miss), so a later bounce back across
            // the plane can't fire OnPlaneCrossed a second time.
            _resolved = true;

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
                _popupText = "GOAL!";
                _popupColor = Color.yellow;
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
        _saved = false;
        _resolved = false;
        _prevZ = transform.position.z;
    }

    /// <summary>Resets all detection state ready for the next shot.</summary>
    public void ResetState()
    {
        _active = false;
        _goalScored = false;
        _saved = false;
        _resolved = false;
        _displayTimer = 0f;
    }

    /// <summary>
    /// Registers the current shot as saved. Called by GoalieSaveZone when the goalie's stick
    /// touches the ball. Returns false, with no side effects, unless a shot is in flight and
    /// hasn't already ended — so touching the idle ball, or the ball after it crossed the
    /// gate plane, does nothing, and repeated touches on one shot count once.
    /// </summary>
    /// <param name="contactPos">World position of the ball at the touch.</param>
    /// <param name="ballVelocity">The ball's velocity at the touch, used to project where
    /// the shot was headed.</param>
    public bool TryRegisterSave(Vector3 contactPos, Vector3 ballVelocity)
    {
        if (!ShotInFlight) return false;

        _saved = true;
        _resolved = true;
        SaveContactPosition = contactPos;

        _popupText = "SAVE!";
        _popupColor = Color.green;
        _displayTimer = DisplayDuration;

        Vector3 projected = ProjectToGatePlane(contactPos, ballVelocity);

        Debug.Log($"[GoalDetector] SAVE! Touched at ({contactPos.x:F2}, {contactPos.y:F2}, {contactPos.z:F2}) | " +
                  $"was headed to gate plane at ({projected.x:F2}, {projected.y:F2})");

        OnSaved?.Invoke(projected);
        return true;
    }

    /// <summary>The ball flies in a straight line until it crosses the gate plane (custom
    /// gravity only turns on after that), so extend its path from the touch point to the plane.
    /// Falls back to the touch point if the ball isn't moving toward the plane.</summary>
    private Vector3 ProjectToGatePlane(Vector3 pos, Vector3 velocity)
    {
        if (Mathf.Abs(velocity.z) < 0.001f) return pos;

        float t = (goalGateCenter.z - pos.z) / velocity.z;
        return t < 0f ? pos : pos + velocity * t;
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
        goalText.text = _popupText;

        Color color = _popupColor;
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