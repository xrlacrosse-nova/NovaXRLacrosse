using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The goalie's stick head, as far as the simulation is concerned: an invisible trigger volume
/// that registers a save when the ball touches it mid-shot. It only detects — it tells
/// GoalDetector (the single source of truth for shot outcomes) via TryRegisterSave(), and
/// GoalDetector's OnSaved event drives everything else (popup, heatmap dot, despawn, next shot).
///
/// Setup (all in the Unity Editor — see Plans/Saving_Plan.md for the full steps):
///   1. Create an empty GameObject under the ML Rig's Controller, next to (NOT under) the
///      stick visual, and fit a Box Collider around the stick head.
///   2. On the Box Collider, tick Is Trigger.
///   3. Add a Rigidbody: Is Kinematic on, Use Gravity off. (A moving collider without a
///      Rigidbody is treated as static; the ball's own Rigidbody is what makes trigger events fire.)
///   4. Add this component to the same GameObject. Ball can be left empty — it's auto-found.
///
/// A save only counts while a shot is in flight, so waving the stick through the idle ball, or
/// touching it after it has already crossed the goal plane, does nothing.
/// </summary>
public class GoalieSaveZone : MonoBehaviour
{
    [Header("Ball")]
    [Tooltip("The ball's GoalDetector. Leave empty to auto-find the one in the scene.")]
    public GoalDetector ball;

    [Header("Detection")]
    [Tooltip("The ball moves ~0.36 m per physics step at 18 m/s — more than the stick head is thick — " +
             "so a plain trigger can be skipped entirely. With this on, the ball's path between " +
             "physics steps is also checked against the zone. Leave on unless debugging.")]
    public bool useSweepCheck = true;

    [Header("Debug")]
    [Tooltip("Log each registered save. GoalDetector always logs its own SAVE line.")]
    public bool showDebugLogs = true;

    // ── Private ───────────────────────────────────────────────────

    private Collider _zoneCollider;
    private Rigidbody _ballRb;

    // ball position at the previous physics step, for the sweep check
    private Vector3 _prevBallPos;
    private bool _hasPrevBallPos = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _zoneCollider = GetComponent<Collider>();
        ValidateSetup();
    }

    void Start()
    {
        if (ball == null)
            ball = FindFirstObjectByType<GoalDetector>();

        if (ball != null)
            _ballRb = ball.GetComponent<Rigidbody>();

        if (ball == null || _ballRb == null)
        {
            Debug.LogWarning("[GoalieSaveZone] Couldn't find the ball (a GoalDetector with a Rigidbody) — " +
                             "saves are disabled. Assign the Ball field.");
            enabled = false;
        }
    }

    /// <summary>Catches the likely hand-wiring mistakes at Play time.</summary>
    private void ValidateSetup()
    {
        if (_zoneCollider == null)
        {
            Debug.LogWarning("[GoalieSaveZone] No Collider on this GameObject — add a Box Collider " +
                             "(Is Trigger) around the stick head.");
            return;
        }

        if (!_zoneCollider.isTrigger)
            Debug.LogWarning("[GoalieSaveZone] The Collider isn't a trigger — tick Is Trigger, or the zone " +
                             "will physically knock the ball around instead of saving it.");

        Rigidbody zoneRb = GetComponent<Rigidbody>();
        if (zoneRb == null)
            Debug.LogWarning("[GoalieSaveZone] No Rigidbody on this GameObject — add one (Is Kinematic on, " +
                             "Use Gravity off); without it the moving zone isn't reliably detected.");
        else if (!zoneRb.isKinematic)
            Debug.LogWarning("[GoalieSaveZone] The Rigidbody isn't kinematic — tick Is Kinematic, since the " +
                             "controller's tracked pose moves this object, not physics.");
    }

#if UNITY_EDITOR
    void Update()
    {
        // Editor-only: press S to save the shot that's currently in flight, so the whole
        // save -> despawn -> next-shot loop can be verified without a headset or stick.
        // Compiled out of device builds.
        if (Keyboard.current != null && Keyboard.current.sKey.wasPressedThisFrame)
        {
            if (!TryRegister(_ballRb.position, "Editor test key") && showDebugLogs)
                Debug.Log("[GoalieSaveZone] Test key ignored — no shot in flight.");
        }
    }
#endif

    void FixedUpdate()
    {
        if (!useSweepCheck || _zoneCollider == null) return;

        // Only track the ball while a shot is live. Clearing this between shots also stops the
        // teleport back to the launch origin from being read as a fast pass through the zone.
        if (!ball.ShotInFlight)
        {
            _hasPrevBallPos = false;
            return;
        }

        Vector3 pos = _ballRb.position;

        if (_hasPrevBallPos)
        {
            Vector3 segment = pos - _prevBallPos;
            float distance = segment.magnitude;

            if (distance > 0.0001f)
            {
                // The controller is moved by its tracked pose every frame, but Unity only syncs
                // transform changes to the physics scene at the start of a physics step, so
                // without this the query below would test against a stale zone pose.
                Physics.SyncTransforms();

                Ray ray = new Ray(_prevBallPos, segment / distance);
                if (_zoneCollider.Raycast(ray, out RaycastHit hit, distance))
                    TryRegister(hit.point, "sweep");
            }
        }

        _prevBallPos = pos;
        _hasPrevBallPos = true;
    }

    // ── Trigger contact ───────────────────────────────────────────

    // Stay as well as Enter: covers the ball already overlapping the zone when a shot starts.
    // Both are cheap — anything that isn't a live shot is rejected on the first line.
    void OnTriggerEnter(Collider other) => HandleContact(other);
    void OnTriggerStay(Collider other) => HandleContact(other);

    private void HandleContact(Collider other)
    {
        if (!ball.ShotInFlight) return;

        // Identify the ball by its GoalDetector rather than a tag/layer (the project has none),
        // so nothing else in the scene — the shooter's stick, the floor — can register a save.
        if (other.GetComponentInParent<GoalDetector>() != ball) return;

        TryRegister(_ballRb.position, "touch");
    }

    // ── Registering ───────────────────────────────────────────────

    /// <summary>Asks GoalDetector to record a save. It refuses (returns false) unless a shot is
    /// in flight and unresolved, so it's safe to call every physics step. This never despawns
    /// the ball itself: RandomLauncher does that in response to OnSaved, after the heatmap has
    /// recorded the shot, so the last shot's dot isn't lost.</summary>
    private bool TryRegister(Vector3 contactPos, string how)
    {
        bool saved = ball.TryRegisterSave(contactPos, _ballRb.linearVelocity);

        if (saved && showDebugLogs)
            Debug.Log($"[GoalieSaveZone] SAVE via {how} at ({contactPos.x:F2}, {contactPos.y:F2}, {contactPos.z:F2})");

        return saved;
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        Collider col = _zoneCollider != null ? _zoneCollider : GetComponent<Collider>();
        if (col == null) return;

        Color outline = new Color(0f, 1f, 0.4f, 0.9f);
        Color fill = new Color(0f, 1f, 0.4f, 0.12f);

        Matrix4x4 previousMatrix = Gizmos.matrix;

        if (col is BoxCollider box)
        {
            // Draw in local space so the box follows the zone's rotation, like the collider does.
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = fill;
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = outline;
            Gizmos.DrawWireCube(box.center, box.size);
        }
        else
        {
            Gizmos.color = outline;
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
        }

        Gizmos.matrix = previousMatrix;
    }
}
