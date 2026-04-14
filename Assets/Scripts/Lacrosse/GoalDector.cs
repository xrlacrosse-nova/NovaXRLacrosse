using UnityEngine;

/// <summary>
/// Detects when the lacrosse ball passes through the 2D goal gate.
/// Attach to the lacrosse ball GameObject.
/// </summary>
public class GoalDetector : MonoBehaviour
{
    [Header("Goal Gate")]
    [Tooltip("World-space center of the goal rectangle (X = horizontal, Y = height, Z = depth of gate plane).")]
    public Vector3 goalGateCenter = new Vector3(0f, 1f, -3f);

    [Tooltip("Half-extents of the goal rectangle. X = half-width, Y = half-height.")]
    public Vector2 goalGateHalfSize = new Vector2(0.5f, 0.5f);

    [Tooltip("Show a yellow GOAL! message on screen when scored.")]
    public bool showOnScreenGoal = true;

    // ── internal ─────────────────────────────────────────────────
    private Rigidbody _rb;

    private bool _ballLaunched = false;
    private bool _goalScored = false;
    private float _goalDisplayTimer = 0f;
    private float _prevBallZ;

    private const float GoalDisplayDuration = 3f;

    // ── lifecycle ─────────────────────────────────────────────────

    void Start()
    {
        _rb = GetComponent<Rigidbody>();
    }

    void Update()
    {
        if (_goalDisplayTimer > 0f)
            _goalDisplayTimer -= Time.deltaTime;

        if (!_ballLaunched || _goalScored) return;

        float currentZ = transform.position.z;

        bool crossedPlane = (_prevBallZ > goalGateCenter.z && currentZ <= goalGateCenter.z)
                         || (_prevBallZ < goalGateCenter.z && currentZ >= goalGateCenter.z);

        if (crossedPlane)
        {
            float t = Mathf.InverseLerp(_prevBallZ, currentZ, goalGateCenter.z);
            Vector3 prevPos = transform.position - (_rb.linearVelocity * Time.deltaTime);
            Vector3 crossingPos = Vector3.Lerp(prevPos, transform.position, t);

            float dx = Mathf.Abs(crossingPos.x - goalGateCenter.x);
            float dy = Mathf.Abs(crossingPos.y - goalGateCenter.y);

            if (dx <= goalGateHalfSize.x && dy <= goalGateHalfSize.y)
            {
                _goalScored = true;
                _goalDisplayTimer = GoalDisplayDuration;
                Debug.Log($"GOAL! Ball crossed gate at ({crossingPos.x:F2}, {crossingPos.y:F2}, {goalGateCenter.z:F2})");
            }
        }

        _prevBallZ = currentZ;
    }

    // ── called by BallLauncher ────────────────────────────────────

    public void OnBallLaunched()
    {
        _ballLaunched = true;
        _prevBallZ = transform.position.z;
    }

    public void ResetState()
    {
        _ballLaunched = false;
        _goalScored = false;
        _goalDisplayTimer = 0f;
    }

    // ── on-screen UI ──────────────────────────────────────────────

    void OnGUI()
    {
        if (!showOnScreenGoal || _goalDisplayTimer <= 0f) return;

        GUIStyle shadow = new GUIStyle(GUI.skin.label)
        {
            fontSize = 80,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        shadow.normal.textColor = Color.black;

        GUIStyle style = new GUIStyle(shadow);
        style.normal.textColor = Color.yellow;

        GUI.Label(new Rect(4, Screen.height * 0.3f + 4, Screen.width, 120), "GOAL!", shadow);
        GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 120), "GOAL!", style);
    }

    // ── Gizmo preview in Scene view ───────────────────────────────

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Vector3 size = new Vector3(goalGateHalfSize.x * 2f, goalGateHalfSize.y * 2f, 0.05f);
        Gizmos.DrawWireCube(goalGateCenter, size);
    }
}