using UnityEngine;

/// <summary>
/// Attach this script directly to the 'LacrosseBall' GameObject.
/// Handles:
///   - Realistic gravity during flight
///   - On collision: bounces the ball with configurable bounciness
///   - Y=0 ground plane detection as a fallback bounce trigger
///   - Anti-tunneling: works correctly against animated/kinematic player objects
///
/// REQUIRED on the same GameObject:
///   - Rigidbody  (useGravity = controlled by this script)
///   - Collider   (SphereCollider recommended)
///
/// RECOMMENDED Rigidbody Inspector settings:
///   - Mass:              0.15  (a lacrosse ball is ~142g)
///   - Drag:              0.05
///   - Angular Drag:      0.05
///   - Collision Detection: Continuous Speculative  ← IMPORTANT CHANGE
///   - Interpolate:       Interpolate
///
/// FOR ANIMATED PLAYER OBJECTS:
///   - Add a Rigidbody to each player (or their collider bones)
///   - Set isKinematic = true on those Rigidbodies
///   - Set their Collision Detection to "Continuous Speculative"
///   - Make sure colliders are NOT triggers
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class LacrossBallPhysics : MonoBehaviour
{
    [Header("Gravity")]
    [Tooltip("Multiplier on top of Unity's Physics.gravity (-9.81). 1 = realistic Earth gravity.")]
    [Range(0.5f, 3f)]
    public float gravityScale = 1f;

    [Header("Bounce Control")]
    [Tooltip("Main bounce slider: 0 = dead ball (no bounce), 1 = super ball (full energy retained).\n" +
             "Realistic lacrosse ball ≈ 0.55–0.65.")]
    [Range(0f, 1f)]
    public float bounciness = 0.6f;

    [Tooltip("Fraction of horizontal (tangential) velocity kept after each bounce.\n" +
             "Lower values make the ball slow down laterally on each hit.")]
    [Range(0f, 1f)]
    public float frictionRetention = 0.75f;

    [Tooltip("Minimum vertical speed (m/s) required to trigger a bounce. " +
             "Below this the ball is considered at rest on the surface.")]
    public float minBounceSpeed = 0.5f;

    [Header("Rest Detection")]
    [Tooltip("Ball is fully settled when overall speed drops below this value (m/s).")]
    public float restSpeedThreshold = 0.15f;

    [Tooltip("Seconds the ball must remain slow before being frozen in place.")]
    public float restDelay = 0.4f;

    [Header("Ground Plane Fallback")]
    [Tooltip("Enable a Y=0 hard-floor check in case the scene has no ground collider.")]
    public bool useGroundPlaneFallback = true;

    [Tooltip("Y coordinate treated as the ground surface.")]
    public float groundY = 0f;

    [Header("Anti-Tunneling (Sweep Test)")]
    [Tooltip("Enable manual sweep-cast each FixedUpdate to catch fast-moving collisions that\n" +
             "Unity's built-in detection might miss (especially vs animated/kinematic objects).")]
    public bool enableSweepTest = true;

    [Tooltip("Layer mask for sweep test. Set this to include Ground, Players, Props — anything the ball should hit.\n" +
             "Leave as 'Everything' to hit all layers (safe default).")]
    public LayerMask sweepLayerMask = ~0;

    [Tooltip("Extra distance added to the sweep to catch near-misses. 0.02–0.05 is usually enough.")]
    [Range(0f, 0.1f)]
    public float sweepSkinWidth = 0.03f;

    [Header("Animated Object Support")]
    [Tooltip("When hitting an animated/kinematic object, factor in the surface's velocity\n" +
             "(estimated from its Rigidbody if present) so the ball reacts correctly.")]
    public bool inheritSurfaceVelocity = true;

    [Header("Debug")]
    public bool showDebugLogs = true;

    // ── internal state ──────────────────────────────────────────────
    private Rigidbody _rb;
    private bool _isResting = false;
    private float _slowTimer = 0f;
    private float _colliderRadius = 0.5f;

    // Track which objects we bounced off this frame to avoid double-bounce
    // (sweep test + OnCollisionEnter could both fire in the same step)
    private Collider _lastSweepHit = null;
    private bool _bouncedViaSweepThisStep = false;

    // ── lifecycle ───────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        // Manual gravity so gravityScale works correctly
        _rb.useGravity = false;

        // ContinuousSpeculative is the best mode for a fast ball because:
        // - Works against BOTH dynamic AND kinematic (animated) colliders
        // - More reliable than ContinuousDynamic at very high speeds
        // - Slight CPU cost is worth it for a single ball object
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        SphereCollider sc = GetComponent<SphereCollider>();
        if (sc != null) _colliderRadius = sc.radius * transform.lossyScale.x;
    }

    void FixedUpdate()
    {
        if (_isResting) return;

        _bouncedViaSweepThisStep = false;
        _lastSweepHit = null;

        // ── Manual gravity ──────────────────────────────────────────
        _rb.AddForce(Physics.gravity * gravityScale, ForceMode.Acceleration);

        // ── Sweep-cast anti-tunneling ───────────────────────────────
        // Fires a sphere cast along the velocity vector before Unity moves the object.
        // This catches cases where the ball would pass through a thin or fast-moving collider.
        if (enableSweepTest)
            DoSweepTest();

        // ── Ground-plane fallback bounce (Y = groundY) ─────────────
        if (useGroundPlaneFallback)
        {
            float floorY = groundY + _colliderRadius;
            if (transform.position.y <= floorY && _rb.linearVelocity.y < 0f)
            {
                Vector3 pos = transform.position;
                pos.y = floorY;
                transform.position = pos;

                ApplyBounce(Vector3.up, null);
            }
        }

        // ── Rest detection ──────────────────────────────────────────
        if (_rb.linearVelocity.magnitude < restSpeedThreshold)
        {
            _slowTimer += Time.fixedDeltaTime;
            if (_slowTimer >= restDelay)
                SettleBall();
        }
        else
        {
            _slowTimer = 0f;
        }
    }

    // ── sweep test ──────────────────────────────────────────────────

    /// <summary>
    /// Casts a sphere along the ball's velocity vector for this physics step.
    /// If we're about to tunnel through something, we handle the bounce ourselves
    /// and reposition the ball on the surface.
    /// </summary>
    private void DoSweepTest()
    {
        Vector3 vel = _rb.linearVelocity;
        float stepDist = vel.magnitude * Time.fixedDeltaTime + sweepSkinWidth;

        if (vel.sqrMagnitude < 0.001f) return; // not moving, skip

        // SphereCast from current position along velocity
        RaycastHit hit;
        bool didHit = Physics.SphereCast(
            transform.position,
            _colliderRadius * 0.99f,    // slightly smaller to avoid self-hit
            vel.normalized,
            out hit,
            stepDist,
            sweepLayerMask,
            QueryTriggerInteraction.Ignore
        );

        if (!didHit) return;
        if (hit.collider == GetComponent<Collider>()) return; // ignore self

        // Move ball to the contact surface to prevent penetration
        Vector3 safePos = transform.position + vel.normalized * (hit.distance - sweepSkinWidth);
        // Only reposition if it's actually ahead of us (not behind due to floating point)
        if (hit.distance > 0.001f)
            transform.position = safePos;

        _lastSweepHit = hit.collider;
        _bouncedViaSweepThisStep = true;

        // Get the moving surface's rigidbody (for animated players)
        Rigidbody surfaceRb = hit.collider.attachedRigidbody;

        if (showDebugLogs)
            Debug.Log($"[LacrossBallPhysics] SweepHit: {hit.collider.gameObject.name} " +
                      $"| speed: {vel.magnitude:F2} m/s | normal: {hit.normal}");

        ApplyBounce(hit.normal, surfaceRb);
    }

    // ── collision handling ──────────────────────────────────────────

    void OnCollisionEnter(Collision collision)
    {
        if (_isResting) return;

        // Skip if the sweep test already handled this same collider this step
        if (_bouncedViaSweepThisStep && collision.collider == _lastSweepHit) return;

        ContactPoint contact = collision.contacts[0];
        Rigidbody surfaceRb = collision.rigidbody; // null for static objects

        if (showDebugLogs)
            Debug.Log($"[LacrossBallPhysics] CollisionEnter: {collision.gameObject.name} " +
                      $"| speed: {_rb.linearVelocity.magnitude:F2} m/s " +
                      $"| normal: {contact.normal}");

        ApplyBounce(contact.normal, surfaceRb);
    }

    // ── bounce math ─────────────────────────────────────────────────

    /// <summary>
    /// Decomposes velocity into normal + tangential components relative to the hit surface.
    /// Optionally accounts for the surface's own velocity (important for moving/animated objects).
    /// </summary>
    /// <param name="surfaceNormal">World-space normal of the surface hit.</param>
    /// <param name="surfaceRb">The Rigidbody of the surface, if any (may be null for static geo).</param>
    private void ApplyBounce(Vector3 surfaceNormal, Rigidbody surfaceRb)
    {
        Vector3 vel = _rb.linearVelocity;

        // If the surface is moving (e.g. an animated player), work in the surface's
        // reference frame so the ball reacts correctly to the impact energy.
        Vector3 surfaceVel = Vector3.zero;
        if (inheritSurfaceVelocity && surfaceRb != null)
            surfaceVel = surfaceRb.linearVelocity;

        Vector3 relVel = vel - surfaceVel; // velocity relative to the surface

        float normalSpeed = Vector3.Dot(relVel, surfaceNormal);
        Vector3 normalComp = normalSpeed * surfaceNormal;
        Vector3 tangentComp = relVel - normalComp;

        // Only bounce if moving into the surface
        if (normalSpeed >= 0f) return;

        if (Mathf.Abs(normalSpeed) < minBounceSpeed)
        {
            // Too slow — kill relative normal velocity and let rest detection finish
            Vector3 slowResult = surfaceVel + new Vector3(
                tangentComp.x * frictionRetention,
                0f,
                tangentComp.z * frictionRetention
            );
            _rb.linearVelocity = slowResult;
            return;
        }

        // Reflect normal component (bounciness), dampen tangential (friction)
        Vector3 bounceRelVel = (-normalComp * bounciness) + (tangentComp * frictionRetention);

        // Add surface velocity back so the ball inherits momentum from a moving player
        _rb.linearVelocity = bounceRelVel + surfaceVel;
        _rb.angularVelocity *= bounciness;
    }

    // ── public API ──────────────────────────────────────────────────

    public void ResetPhysicsState()
    {
        _isResting = false;
        _slowTimer = 0f;
        _bouncedViaSweepThisStep = false;
        _lastSweepHit = null;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.isKinematic = false;

        if (showDebugLogs)
            Debug.Log("[LacrossBallPhysics] Physics state reset.");
    }

    // ── private helpers ─────────────────────────────────────────────

    private void SettleBall()
    {
        _isResting = true;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.isKinematic = true;

        if (showDebugLogs)
            Debug.Log("[LacrossBallPhysics] Ball has come to rest.");
    }
}