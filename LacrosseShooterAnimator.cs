using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// LacrosseShooterAnimator
/// Controls the AI shooter NPC: selects shot type, drives Animator parameters,
/// positions/rotates to target zones, and triggers ball launch at the correct frame.
/// Attach to the Shooter NPC root GameObject.
/// </summary>
public class LacrosseShooterAnimator : MonoBehaviour
{
    // ─────────────────────────────────────────────
    //  Enums
    // ─────────────────────────────────────────────

    public enum ShotType
    {
        Overhand,       // Standard high-power shot
        Sidearm,        // Low, fast, skipping shot
        Underhand,      // Deceptive bounce shot
        QuickStick,     // Fast release, less wind-up
        BehindTheBack,  // Advanced trick shot
    }

    public enum SkillLevel
    {
        Beginner,   // Slow, telegraphed
        Intermediate,
        Advanced,
        Elite       // Fast, minimal tell
    }

    // ─────────────────────────────────────────────
    //  Inspector Settings
    // ─────────────────────────────────────────────

    [Header("References")]
    public Animator shooterAnimator;

    [Tooltip("Ball prefab to instantiate on each shot")]
    public GameObject ballPrefab;

    [Tooltip("Hand/stick tip transform where the ball spawns")]
    public Transform ballReleasePoint;

    [Tooltip("The goal Transform used to calculate shot direction")]
    public Transform goalTarget;

    [Tooltip("Parent for spawned balls (keeps hierarchy clean)")]
    public Transform ballPoolParent;

    [Header("Shot Settings")]
    public ShotType defaultShotType = ShotType.Overhand;

    [Tooltip("Allow random shot type selection each shot")]
    public bool randomiseShotType = true;

    public SkillLevel shooterSkillLevel = SkillLevel.Intermediate;

    [Header("Timing & Cadence")]
    [Tooltip("Seconds between shots")]
    [Range(1f, 8f)]
    public float timeBetweenShots = 3.5f;

    [Tooltip("Random extra delay added per shot (± value)")]
    [Range(0f, 2f)]
    public float timingVariance = 0.8f;

    // FIX #11: Exposed as a serialized field so designers can tune the floor,
    // rather than relying on a hard-coded 1f minimum.
    [Tooltip("Minimum allowed interval between shots regardless of variance")]
    [Range(0.5f, 3f)]
    public float minTimeBetweenShots = 1f;

    [Tooltip("How long before actual release the goalie should react (anticipation window in seconds)")]
    [Range(0f, 1f)]
    public float anticipationWindow = 0.25f;

    [Header("Goal Zone Targeting")]
    [Tooltip("Predefined goal-zone offsets from goalTarget centre (local space)")]
    public List<Vector3> goalZones = new List<Vector3>
    {
        new Vector3(-0.5f,  0.8f, 0f),   // Top-left
        new Vector3( 0.5f,  0.8f, 0f),   // Top-right
        new Vector3(-0.5f,  0.2f, 0f),   // Bottom-left
        new Vector3( 0.5f,  0.2f, 0f),   // Bottom-right
        new Vector3( 0f,    0.5f, 0f),   // Centre
    };

    [Tooltip("Weight per zone (higher = more likely). Must match goalZones count.")]
    public List<float> zoneWeights = new List<float> { 1f, 1f, 1f, 1f, 0.5f };

    [Header("Body / Aim Rotation")]
    [Tooltip("Speed at which shooter rotates to face the selected zone")]
    [Range(1f, 20f)]
    public float rotationSpeed = 6f;

    [Tooltip("Layer mask for shoulder rotation IK (set to Shooter layer)")]
    public AvatarIKGoal aimIKGoal = AvatarIKGoal.RightHand;
    public float aimIKWeight = 0.7f;

    // ─────────────────────────────────────────────
    //  Animator Parameter Hashes (set in Awake)
    // ─────────────────────────────────────────────

    private static readonly int HashShotType = Animator.StringToHash("ShotType");
    private static readonly int HashShotTrigger = Animator.StringToHash("Shoot");
    private static readonly int HashIsAiming = Animator.StringToHash("IsAiming");
    // FIX (style): HashAimWeight and HashMoveSpeed were declared but never used.
    // Keeping them here commented out as stubs for future implementation.
    // private static readonly int HashAimWeight  = Animator.StringToHash("AimWeight");
    // private static readonly int HashMoveSpeed  = Animator.StringToHash("MoveSpeed");
    private static readonly int HashShotSpeed = Animator.StringToHash("ShotSpeed");

    // ─────────────────────────────────────────────
    //  Internal State
    // ─────────────────────────────────────────────

    private Vector3 currentTargetWorldPos;
    private ShotType currentShotType;
    private bool isShooting = false;
    private Coroutine shootRoutine;

    // FIX #8: Track a pending forced zone so ForceNextShotToZone() isn't
    // immediately overwritten by PickWeightedZone() inside ExecuteShot().
    private int? pendingForcedZone = null;

    // FIX #4: Replaced Queue<T> with a List used as a free-list for O(1)-style
    // retrieval. Balls are returned to the free list via a callback on despawn.
    private List<LacrosseBallPhysics> ballPool = new List<LacrosseBallPhysics>();
    private Stack<LacrosseBallPhysics> freeBalls = new Stack<LacrosseBallPhysics>();
    private const int POOL_SIZE = 6;

    // ─────────────────────────────────────────────
    //  Unity Lifecycle
    // ─────────────────────────────────────────────

    private void Awake()
    {
        ValidateReferences();
        InitialiseBallPool();
        ValidateAndPadWeights();
    }

    private void Start()
    {
        StartShooting();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (shooterAnimator == null) return;

        // Drive hand IK toward shot zone during aiming phase
        if (isShooting && goalTarget != null)
        {
            shooterAnimator.SetIKPositionWeight(aimIKGoal, aimIKWeight);
            shooterAnimator.SetIKPosition(aimIKGoal, currentTargetWorldPos);
        }
        else
        {
            shooterAnimator.SetIKPositionWeight(aimIKGoal, 0f);
        }
    }

    // ─────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────

    public void StartShooting()
    {
        if (shootRoutine != null) StopCoroutine(shootRoutine);
        shootRoutine = StartCoroutine(ShootingLoop());
    }

    public void StopShooting()
    {
        if (shootRoutine != null) StopCoroutine(shootRoutine);
        shootRoutine = null;
        isShooting = false;
        shooterAnimator?.SetBool(HashIsAiming, false);
    }

    /// <summary>
    /// Force the next shot to a specific zone index.
    /// Takes effect on the next call to ExecuteShot — not overwritten by PickWeightedZone.
    /// </summary>
    public void ForceNextShotToZone(int zoneIndex)
    {
        if (zoneIndex < 0 || zoneIndex >= goalZones.Count)
        {
            Debug.LogWarning($"[LacrosseShooterAnimator] ForceNextShotToZone: index {zoneIndex} out of range.");
            return;
        }
        // FIX #8: Store as pending rather than writing to currentTargetWorldPos directly,
        // which was immediately overwritten by ExecuteShot.
        pendingForcedZone = zoneIndex;
    }

    /// <summary>
    /// Trigger a single shot immediately (for manual scripting / event systems).
    /// </summary>
    public void TriggerImmediateShot(ShotType type)
    {
        // FIX #10: Log a warning when busy rather than silently dropping the request.
        if (isShooting)
        {
            Debug.LogWarning("[LacrosseShooterAnimator] TriggerImmediateShot ignored — shot already in progress.");
            return;
        }
        StartCoroutine(ExecuteShot(type));
    }

    // ─────────────────────────────────────────────
    //  Core Shooting Loop
    // ─────────────────────────────────────────────

    private IEnumerator ShootingLoop()
    {
        // Brief intro pause
        yield return new WaitForSeconds(1.5f);

        while (true)
        {
            ShotType shotType = randomiseShotType
                ? PickRandomShotType()
                : defaultShotType;

            yield return ExecuteShot(shotType);

            float interval = timeBetweenShots
                             + Random.Range(-timingVariance, timingVariance);
            yield return new WaitForSeconds(Mathf.Max(minTimeBetweenShots, interval));
        }
    }

    private IEnumerator ExecuteShot(ShotType shotType)
    {
        isShooting = true;
        currentShotType = shotType;

        // FIX #8: Honour any pending forced zone before falling back to weighted random.
        if (pendingForcedZone.HasValue)
        {
            currentTargetWorldPos = GetWorldZonePosition(pendingForcedZone.Value);
            pendingForcedZone = null;
        }
        else
        {
            currentTargetWorldPos = PickWeightedZone();
        }

        // ── 1. Aim phase ─────────────────────────────
        shooterAnimator?.SetBool(HashIsAiming, true);
        shooterAnimator?.SetInteger(HashShotType, (int)shotType);
        shooterAnimator?.SetFloat(HashShotSpeed, GetShotSpeedNormalised());

        float aimDuration = GetAimDuration(shotType);
        float elapsed = 0f;

        while (elapsed < aimDuration)
        {
            RotateTowardTarget(currentTargetWorldPos);
            elapsed += Time.deltaTime;
            yield return null;
        }

        // ── 2. Wind-up / telegraph ────────────────────
        float releaseDelay = GetReleaseDelay(shotType);
        LacrosseEvents.OnShotAnticipation?.Invoke(currentTargetWorldPos, releaseDelay);

        yield return new WaitForSeconds(releaseDelay - anticipationWindow);

        // ── 3. Trigger animation ──────────────────────
        shooterAnimator?.SetTrigger(HashShotTrigger);

        yield return new WaitForSeconds(anticipationWindow);

        // ── 4. Release ball ───────────────────────────
        SpawnAndLaunchBall();

        // ── 5. Follow-through ─────────────────────────
        yield return new WaitForSeconds(GetFollowThroughDuration(shotType));

        shooterAnimator?.SetBool(HashIsAiming, false);
        isShooting = false;
    }

    // ─────────────────────────────────────────────
    //  Ball Spawning
    // ─────────────────────────────────────────────

    private void SpawnAndLaunchBall()
    {
        if (ballReleasePoint == null) return;

        LacrosseBallPhysics ball = GetPooledBall();
        if (ball == null) return;

        ball.transform.position = ballReleasePoint.position;
        // NOTE: Random.rotation sets initial orientation only; spin is driven by
        // angularVelocity in LacrosseBallPhysics.Launch(), not by this transform.
        ball.transform.rotation = Random.rotation;
        ball.gameObject.SetActive(true);
        ball.ResetBall(ballReleasePoint.position);

        Vector3 direction = (currentTargetWorldPos - ballReleasePoint.position).normalized;
        direction = ApplySkillScatter(direction);

        ball.Launch(direction, currentTargetWorldPos);

        Debug.Log($"[Shooter] Launched {currentShotType} → Zone {currentTargetWorldPos} | " +
                  $"Speed: {ball.CurrentSpeedMPH:F1} mph");
    }

    // ─────────────────────────────────────────────
    //  Object Pooling
    // ─────────────────────────────────────────────

    private void InitialiseBallPool()
    {
        if (ballPrefab == null) return;

        for (int i = 0; i < POOL_SIZE; i++)
        {
            GameObject obj = Instantiate(ballPrefab, ballPoolParent);
            obj.SetActive(false);
            LacrosseBallPhysics ball = obj.GetComponent<LacrosseBallPhysics>();
            if (ball != null)
            {
                ballPool.Add(ball);
                freeBalls.Push(ball);
            }
        }
    }

    // FIX #4: GetPooledBall now pops from the O(1) free-stack instead of
    // iterating the entire pool on every shot.
    private LacrosseBallPhysics GetPooledBall()
    {
        if (freeBalls.Count > 0)
            return freeBalls.Pop();

        // Expand pool if free list is empty
        if (ballPrefab != null)
        {
            GameObject obj = Instantiate(ballPrefab, ballPoolParent);
            LacrosseBallPhysics ball = obj.GetComponent<LacrosseBallPhysics>();
            if (ball != null)
            {
                ballPool.Add(ball);
                return ball;
            }
        }

        Debug.LogWarning("[Shooter] Ball pool exhausted and no prefab assigned.");
        return null;
    }

    /// <summary>
    /// Return a ball to the free list so it can be reused.
    /// Call this from a ball-despawn callback or OnDisable on the ball.
    /// </summary>
    public void ReturnBallToPool(LacrosseBallPhysics ball)
    {
        if (ball != null && !freeBalls.Contains(ball))
            freeBalls.Push(ball);
    }

    // ─────────────────────────────────────────────
    //  Rotation & IK
    // ─────────────────────────────────────────────

    private void RotateTowardTarget(Vector3 targetWorldPos)
    {
        Vector3 dir = targetWorldPos - transform.position;
        dir.y = 0f; // Keep upright
        if (dir == Vector3.zero) return;

        Quaternion lookRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(
            transform.rotation, lookRot, rotationSpeed * Time.deltaTime);
    }

    // ─────────────────────────────────────────────
    //  Goal Zone Helpers
    // ─────────────────────────────────────────────

    private Vector3 PickWeightedZone()
    {
        float total = 0f;
        foreach (var w in zoneWeights) total += w;

        float roll = Random.Range(0f, total);
        float cumul = 0f;

        for (int i = 0; i < goalZones.Count; i++)
        {
            cumul += (i < zoneWeights.Count ? zoneWeights[i] : 1f);
            if (roll <= cumul)
                return GetWorldZonePosition(i);
        }

        return GetWorldZonePosition(0);
    }

    private Vector3 GetWorldZonePosition(int index)
    {
        if (goalTarget == null) return Vector3.zero;
        return goalTarget.TransformPoint(goalZones[index]);
    }

    // ─────────────────────────────────────────────
    //  Skill Level Helpers
    // ─────────────────────────────────────────────

    private Vector3 ApplySkillScatter(Vector3 direction)
    {
        float scatterDeg = shooterSkillLevel switch
        {
            SkillLevel.Beginner => Random.Range(4f, 10f),
            SkillLevel.Intermediate => Random.Range(2f, 5f),
            SkillLevel.Advanced => Random.Range(0.5f, 2f),
            SkillLevel.Elite => Random.Range(0f, 0.8f),
            _ => 2f
        };

        return Quaternion.Euler(
            Random.Range(-scatterDeg, scatterDeg),
            Random.Range(-scatterDeg, scatterDeg),
            0f) * direction;
    }

    private float GetAimDuration(ShotType type) => type switch
    {
        ShotType.QuickStick => 0.3f,
        ShotType.Overhand => 0.7f,
        ShotType.Sidearm => 0.6f,
        ShotType.Underhand => 0.5f,
        ShotType.BehindTheBack => 0.8f,
        _ => 0.6f
    };

    private float GetReleaseDelay(ShotType type) => type switch
    {
        ShotType.QuickStick => 0.25f,
        ShotType.Overhand => 0.55f,
        ShotType.Sidearm => 0.45f,
        ShotType.Underhand => 0.40f,
        ShotType.BehindTheBack => 0.65f,
        _ => 0.5f
    };

    private float GetFollowThroughDuration(ShotType type) => type switch
    {
        ShotType.QuickStick => 0.3f,
        ShotType.Overhand => 0.7f,
        ShotType.Sidearm => 0.5f,
        ShotType.Underhand => 0.5f,
        ShotType.BehindTheBack => 0.9f,
        _ => 0.6f
    };

    private float GetShotSpeedNormalised() => shooterSkillLevel switch
    {
        SkillLevel.Beginner => 0.4f,
        SkillLevel.Intermediate => 0.65f,
        SkillLevel.Advanced => 0.82f,
        SkillLevel.Elite => 1.0f,
        _ => 0.65f
    };

    // ─────────────────────────────────────────────
    //  Utilities
    // ─────────────────────────────────────────────

    // FIX #7: Non-Elite shooters now have access to all shot types, weighted so
    // Underhand and BehindTheBack appear less frequently at lower skill levels.
    private ShotType PickRandomShotType()
    {
        if (shooterSkillLevel == SkillLevel.Elite)
            return (ShotType)Random.Range(0, System.Enum.GetValues(typeof(ShotType)).Length);

        float roll = Random.value;
        return shooterSkillLevel switch
        {
            SkillLevel.Beginner => roll switch
            {
                < 0.55f => ShotType.Overhand,
                < 0.80f => ShotType.QuickStick,
                < 0.93f => ShotType.Sidearm,
                < 0.98f => ShotType.Underhand,
                _ => ShotType.BehindTheBack,
            },
            SkillLevel.Intermediate => roll switch
            {
                < 0.45f => ShotType.Overhand,
                < 0.70f => ShotType.QuickStick,
                < 0.85f => ShotType.Sidearm,
                < 0.95f => ShotType.Underhand,
                _ => ShotType.BehindTheBack,
            },
            SkillLevel.Advanced => roll switch
            {
                < 0.35f => ShotType.Overhand,
                < 0.60f => ShotType.QuickStick,
                < 0.78f => ShotType.Sidearm,
                < 0.92f => ShotType.Underhand,
                _ => ShotType.BehindTheBack,
            },
            _ => ShotType.Overhand,
        };
    }

    // FIX #6: Validate weights — pad if too short, warn and trim if too long.
    private void ValidateAndPadWeights()
    {
        while (zoneWeights.Count < goalZones.Count)
            zoneWeights.Add(1f);

        if (zoneWeights.Count > goalZones.Count)
        {
            Debug.LogWarning($"[LacrosseShooterAnimator] zoneWeights has {zoneWeights.Count} entries " +
                             $"but goalZones only has {goalZones.Count}. Trimming excess weights.");
            zoneWeights.RemoveRange(goalZones.Count, zoneWeights.Count - goalZones.Count);
        }
    }

    private void ValidateReferences()
    {
        if (shooterAnimator == null)
            shooterAnimator = GetComponentInChildren<Animator>();

        if (ballReleasePoint == null)
            Debug.LogWarning("[LacrosseShooterAnimator] ballReleasePoint not assigned!");

        if (goalTarget == null)
            Debug.LogWarning("[LacrosseShooterAnimator] goalTarget not assigned!");
    }

    // ─────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (goalTarget == null) return;

        for (int i = 0; i < goalZones.Count; i++)
        {
            Vector3 worldPos = goalTarget.TransformPoint(goalZones[i]);
            Gizmos.color = Color.Lerp(Color.green, Color.red,
                (float)i / Mathf.Max(1, goalZones.Count - 1));
            Gizmos.DrawWireSphere(worldPos, 0.08f);
            Gizmos.DrawLine(transform.position, worldPos);
        }

        // Draw active target
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(currentTargetWorldPos, 0.12f);
    }
}