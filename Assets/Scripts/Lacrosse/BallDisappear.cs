using UnityEngine;

/// <summary>
/// Thin add-on for GoalDetector: after the ball crosses the goal gate plane (goal or miss),
/// lets it keep flying freely for <see cref="lingerDuration"/> seconds, then deactivates
/// the GameObject. All plane-crossing detection and the GOAL! overlay are owned by
/// GoalDetector — this script only reacts to <see cref="GoalDetector.OnPlaneCrossed"/>.
///
/// Attach to the same GameObject as GoalDetector.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class BallDisappear : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("Post-Crossing Behaviour")]
    [Tooltip("How many seconds the ball keeps travelling after it crosses the goal plane " +
             "before the GameObject is deactivated. Set to 0 to disappear immediately.")]
    [Min(0f)]
    public float lingerDuration = 1.5f;

    // ── state ─────────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private bool _lingering = false;
    private float _lingerTimer = 0f;

    // ── lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
    }

    void OnEnable()
    {
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
    }

    void Update()
    {
        if (!_lingering) return;

        _lingerTimer -= Time.deltaTime;
        if (_lingerTimer <= 0f)
        {
            _lingering = false;
            gameObject.SetActive(false);
            Debug.Log("[BallDisappear] Ball deactivated after linger period.");
        }
    }

    // ── event handler ─────────────────────────────────────────────

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        _lingering = true;
        _lingerTimer = lingerDuration;
    }

    // ── public API ────────────────────────────────────────────────

    /// <summary>Resets linger state ready for the next shot. Reactivates the GameObject if needed.</summary>
    public void ResetState()
    {
        _lingering = false;
        _lingerTimer = 0f;

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }
}
