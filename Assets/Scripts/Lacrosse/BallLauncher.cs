using UnityEngine;

public enum Quadrant { TopLeft, TopRight, BottomLeft, BottomRight }

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(GoalDetector))]
public class BallLauncher : MonoBehaviour
{
    [Header("Launch")]
    public Transform launchOrigin;

    [Range(0f, 1f)]
    public float quadrantInset = 0.5f;

    public float launchSpeed = 15f;
    public float speedVariance = 0.5f;

    [Header("Bounce")]
    public float backOfNetZ = -3.3f;
    public float groundY = 0f;

    [Range(0f, 1f)]
    public float bounciness = 0.6f;
    public float bounceDrag = 1.5f;
    public float bounceGravityScale = 1f;

    [Header("Auto Fire")]
    public bool autoFireOnStart = true;
    public Quadrant targetQuadrant = Quadrant.TopLeft;

    // ── private ───────────────────────────────────────────────────

    private Rigidbody _rb;
    private GoalDetector _goalDetector;
    private Vector3 _spawnPosition;
    private bool _launched = false;
    private bool _bouncing = false;

    // ── lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _goalDetector = GetComponent<GoalDetector>();
    }

    void Start()
    {
        _spawnPosition = launchOrigin != null ? launchOrigin.position : transform.position;

        if (autoFireOnStart)
            LaunchIntoQuadrant(targetQuadrant);
    }

    void Update()
    {
        if (!_launched || _bouncing) return;

        if (transform.position.z <= backOfNetZ)
            BeginBouncing();
    }

    void FixedUpdate()
    {
        if (!_bouncing) return;

        _rb.AddForce(Physics.gravity * bounceGravityScale, ForceMode.Acceleration);

        if (transform.position.y <= groundY)
        {
            Vector3 pos = transform.position;
            pos.y = groundY;
            transform.position = pos;

            Vector3 vel = _rb.linearVelocity;
            if (vel.y < 0f)
                vel.y = -vel.y * bounciness;
            _rb.linearVelocity = vel;
        }
    }

    // ── public API ────────────────────────────────────────────────

    public void LaunchIntoQuadrant(Quadrant quadrant)
    {
        // 1. Fully wake the Rigidbody
        _rb.isKinematic = false;
        _rb.useGravity = false;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.linearDamping = 0f;

        // 2. Snap to spawn position
        transform.position = launchOrigin != null ? launchOrigin.position : _spawnPosition;
        transform.rotation = Quaternion.identity;

        // 3. Aim directly at the quadrant point in full 3D —
        //    do NOT flatten Y. The goal is in -Z so we need the
        //    full direction vector to get there.
        targetQuadrant = quadrant;
        Vector3 aimPoint = ComputeAimPoint(quadrant);
        Vector3 direction = (aimPoint - transform.position).normalized;

        // 4. Fallback: if somehow origin == aimPoint, shoot straight at -Z
        if (direction == Vector3.zero)
            direction = Vector3.back;

        float speed = launchSpeed + Random.Range(-speedVariance, speedVariance);
        Vector3 launchVelocity = direction * speed;

        // 5. Apply velocity then re-enable gravity
        _rb.linearVelocity = launchVelocity;
        _rb.useGravity = true;

        _launched = true;
        _bouncing = false;
        _goalDetector.OnBallLaunched();

        Debug.Log($"[BallLauncher] FIRED -> {quadrant} | origin={transform.position} | aimPoint={aimPoint} | dir={direction} | speed={speed:F2}");
    }

    public void ResetBall()
    {
        _launched = false;
        _bouncing = false;

        _rb.isKinematic = false;
        _rb.useGravity = false;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.linearDamping = 0f;

        transform.position = launchOrigin != null ? launchOrigin.position : _spawnPosition;
        transform.rotation = Quaternion.identity;

        _goalDetector.ResetState();
    }

    // ── helpers ───────────────────────────────────────────────────

    private Vector3 ComputeAimPoint(Quadrant quadrant)
    {
        Vector3 center = _goalDetector.goalGateCenter;
        Vector2 halfSize = _goalDetector.goalGateHalfSize;

        float xOff = halfSize.x * (1f - quadrantInset);
        float yOff = halfSize.y * (1f - quadrantInset);

        float xSign = (quadrant == Quadrant.TopLeft || quadrant == Quadrant.BottomLeft) ? -1f : 1f;
        float ySign = (quadrant == Quadrant.TopLeft || quadrant == Quadrant.TopRight) ? 1f : -1f;

        return new Vector3(
            center.x + xSign * xOff,
            center.y + ySign * yOff,
            center.z
        );
    }

    private void BeginBouncing()
    {
        _bouncing = true;
        _rb.useGravity = false;
        _rb.linearDamping = bounceDrag;

        Vector3 vel = _rb.linearVelocity;
        vel.z *= 0.1f;
        _rb.linearVelocity = vel;

        Debug.Log("[BallLauncher] Bounce mode active.");
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (_goalDetector == null)
            _goalDetector = GetComponent<GoalDetector>();
        if (_goalDetector == null) return;

        Vector3 center = _goalDetector.goalGateCenter;
        Vector2 halfSize = _goalDetector.goalGateHalfSize;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(
            new Vector3(center.x, center.y - halfSize.y, center.z),
            new Vector3(center.x, center.y + halfSize.y, center.z));
        Gizmos.DrawLine(
            new Vector3(center.x - halfSize.x, center.y, center.z),
            new Vector3(center.x + halfSize.x, center.y, center.z));

        Gizmos.color = new Color(1f, 0f, 1f, 0.35f);
        Vector3 qCenter = QuadrantGizmoCenter(targetQuadrant, center, halfSize);
        Gizmos.DrawCube(qCenter, new Vector3(halfSize.x, halfSize.y, 0.02f));

        if (launchOrigin != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(launchOrigin.position, 0.06f);
            Gizmos.DrawLine(launchOrigin.position, qCenter);
        }

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawWireCube(new Vector3(center.x, center.y, backOfNetZ),
            new Vector3(halfSize.x * 2f, halfSize.y * 2f, 0.02f));
    }

    private Vector3 QuadrantGizmoCenter(Quadrant q, Vector3 center, Vector2 halfSize)
    {
        float hx = halfSize.x * 0.5f;
        float hy = halfSize.y * 0.5f;
        switch (q)
        {
            case Quadrant.TopLeft: return new Vector3(center.x - hx, center.y + hy, center.z);
            case Quadrant.TopRight: return new Vector3(center.x + hx, center.y + hy, center.z);
            case Quadrant.BottomLeft: return new Vector3(center.x - hx, center.y - hy, center.z);
            default: return new Vector3(center.x + hx, center.y - hy, center.z);
        }
    }
}