using UnityEngine;
using System.Collections;

/// <summary>
/// LacrosseBallPhysics
/// Handles realistic lacrosse ball shooting mechanics for XR Goalie Training.
/// Attach to the lacrosse ball prefab.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class LacrosseBallPhysics : MonoBehaviour
{
    // ─────────────────────────────────────────────
    //  Inspector Settings
    // ─────────────────────────────────────────────

    [Header("Shot Parameters")]
    [Tooltip("Base launch speed in m/s (elite shot ~38 m/s / ~85 mph)")]
    [Range(10f, 50f)]
    public float shotSpeed = 28f;

    [Tooltip("Random speed variance applied per shot (± value)")]
    [Range(0f, 8f)]
    public float speedVariance = 3f;

    [Tooltip("Realistic lacrosse ball mass in kg (official: ~0.145 kg)")]
    public float ballMass = 0.145f;

    [Header("Spin / Magnus Effect")]
    [Tooltip("Enable Magnus force (ball curves based on spin)")]
    public bool enableMagnusEffect = true;

    [Tooltip("Angular velocity applied on launch (degrees/sec per axis)")]
    public Vector3 launchSpinDPS = new Vector3(0f, 0f, 1200f);

    // FIX #2: Corrected Magnus coefficient — original value of 0.15 produced
    // ~600 m/s² of lateral acceleration, roughly 1000× too large.
    // Real lacrosse ball Magnus coefficient ≈ 0.00045 (accounts for air density,
    // ball cross-section, and mass). Exposed in Inspector so designers can tune.
    [Tooltip("Magnus force coefficient. Realistic range for a lacrosse ball: 0.0002–0.001")]
    [Range(0f, 0.005f)]
    public float magnusCoefficient = 0.00045f;

    [Header("Drag & Bounce")]
    [Tooltip("Air drag coefficient (default Unity air ~0.01)")]
    [Range(0f, 0.1f)]
    public float airDrag = 0.01f;

    [Tooltip("Physics material used for bounce (set in Inspector or auto-created)")]
    public PhysicMaterial ballPhysicsMaterial;

    [Tooltip("Rubber lacrosse ball bounce coefficient (0–1)")]
    [Range(0f, 1f)]
    public float bounciness = 0.55f;

    [Header("Trail & VFX")]
    public TrailRenderer ballTrail;
    [Tooltip("Particle system played on goal-line impact")]
    public ParticleSystem impactFX;

    [Header("Audio")]
    public AudioClip shotSoundClip;
    public AudioClip bounceClip;
    [Range(0f, 1f)] public float shotVolume = 0.8f;

    // ─────────────────────────────────────────────
    //  Internal State
    // ─────────────────────────────────────────────

    private Rigidbody rb;
    private AudioSource audioSource;
    private bool hasLaunched = false;
    private Vector3 launchDirection;

    // FIX #9: Shared static PhysicMaterial so each ball instance doesn't allocate
    // a new untracked asset in memory on every Awake.
    private static PhysicMaterial sharedBallMaterial;

    // Expose read-only shot data for scoreboard / trainer UI
    public float CurrentSpeedMPS => rb != null ? rb.velocity.magnitude : 0f;
    public float CurrentSpeedMPH => CurrentSpeedMPS * 2.23694f;
    public bool HasLaunched => hasLaunched;

    // ─────────────────────────────────────────────
    //  Unity Lifecycle
    // ─────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ConfigureRigidbody();
        ConfigurePhysicsMaterial();

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1f; // Full 3D audio for XR
        audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        audioSource.maxDistance = 20f;
    }

    private void FixedUpdate()
    {
        if (!hasLaunched) return;

        ApplyMagnusForce();
    }

    // ─────────────────────────────────────────────
    //  Public API – called by LacrosseShooterAnimator
    // ─────────────────────────────────────────────

    /// <summary>
    /// Launch the ball from a given world-space direction toward a target.
    /// </summary>
    /// <param name="shootDirection">Normalised world-space direction.</param>
    /// <param name="targetGoalPosition">Optional target (used for slight aim assist in training).</param>
    public void Launch(Vector3 shootDirection, Vector3? targetGoalPosition = null)
    {
        if (hasLaunched) return;

        launchDirection = shootDirection.normalized;

        // Optional gentle aim assist toward target
        if (targetGoalPosition.HasValue)
        {
            Vector3 toTarget = (targetGoalPosition.Value - transform.position).normalized;
            launchDirection = Vector3.Slerp(launchDirection, toTarget, 0.1f);
        }

        float finalSpeed = shotSpeed + Random.Range(-speedVariance, speedVariance);

        rb.isKinematic = false;
        rb.velocity = launchDirection * finalSpeed;
        rb.angularVelocity = launchSpinDPS * Mathf.Deg2Rad; // Convert to rad/s

        hasLaunched = true;

        if (ballTrail != null)
            ballTrail.emitting = true;

        PlayAudio(shotSoundClip, shotVolume);

        // Auto-despawn after 5 seconds to keep scene clean
        StartCoroutine(DespawnAfterDelay(5f));
    }

    /// <summary>
    /// Reset ball to a new position, ready to be launched again.
    /// </summary>
    public void ResetBall(Vector3 newPosition)
    {
        StopAllCoroutines();
        hasLaunched = false;
        transform.position = newPosition;
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;

        if (ballTrail != null)
        {
            ballTrail.emitting = false;
            ballTrail.Clear();
        }
    }

    // ─────────────────────────────────────────────
    //  Physics Helpers
    // ─────────────────────────────────────────────

    private void ApplyMagnusForce()
    {
        if (!enableMagnusEffect) return;

        // F_magnus = coefficient * (omega × velocity)
        Vector3 magnusForce = magnusCoefficient
                              * Vector3.Cross(rb.angularVelocity, rb.velocity);
        rb.AddForce(magnusForce, ForceMode.Force);
    }

    private void ConfigureRigidbody()
    {
        rb.mass = ballMass;
        rb.drag = airDrag;
        rb.angularDrag = 0.05f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.isKinematic = true; // Kinematic until Launch() is called
    }

    private void ConfigurePhysicsMaterial()
    {
        // FIX #9: Use inspector-assigned material if provided; otherwise use/create
        // a single shared static instance to avoid per-instance memory leaks.
        if (ballPhysicsMaterial != null)
        {
            // Designer-assigned material takes priority
        }
        else if (sharedBallMaterial == null)
        {
            sharedBallMaterial = new PhysicMaterial("LacrosseBall_Shared")
            {
                bounciness = this.bounciness,
                dynamicFriction = 0.4f,
                staticFriction = 0.45f,
                frictionCombine = PhysicMaterialCombine.Average,
                bounceCombine = PhysicMaterialCombine.Multiply
            };
            ballPhysicsMaterial = sharedBallMaterial;
        }
        else
        {
            ballPhysicsMaterial = sharedBallMaterial;
        }

        Collider col = GetComponent<Collider>();
        if (col != null)
            col.material = ballPhysicsMaterial;
    }

    // ─────────────────────────────────────────────
    //  Collision & Events
    // ─────────────────────────────────────────────

    private void OnCollisionEnter(Collision collision)
    {
        // FIX #3: Use collision.relativeVelocity instead of rb.velocity, which has
        // already been modified by Unity's collision resolution by this point.
        float impactSpeed = collision.relativeVelocity.magnitude;
        PlayAudio(bounceClip, 0.6f * (impactSpeed / shotSpeed));

        if (impactFX != null)
            impactFX.Play();

        // Notify external listeners (e.g. ScoreManager, TrainingManager)
        if (collision.gameObject.CompareTag("GoalNet"))
            OnGoalScored(collision.GetContact(0).point);
        else if (collision.gameObject.CompareTag("GoalPost"))
            OnPostHit(collision.GetContact(0).point);
        else if (collision.gameObject.CompareTag("Goalkeeper"))
            OnSavedByGoalkeeper(collision.GetContact(0).point);
    }

    private void OnGoalScored(Vector3 impactPoint)
    {
        Debug.Log($"[Lacrosse] GOAL scored at {impactPoint} | Speed: {CurrentSpeedMPH:F1} mph");
        LacrosseEvents.OnGoalScored?.Invoke(impactPoint, CurrentSpeedMPH);
    }

    private void OnPostHit(Vector3 impactPoint)
    {
        Debug.Log($"[Lacrosse] Post hit at {impactPoint}");
        LacrosseEvents.OnPostHit?.Invoke(impactPoint);
    }

    private void OnSavedByGoalkeeper(Vector3 impactPoint)
    {
        Debug.Log($"[Lacrosse] Save at {impactPoint} | Speed: {CurrentSpeedMPH:F1} mph");
        LacrosseEvents.OnSaved?.Invoke(impactPoint, CurrentSpeedMPH);
    }

    // ─────────────────────────────────────────────
    //  Utilities
    // ─────────────────────────────────────────────

    private void PlayAudio(AudioClip clip, float volume)
    {
        if (clip != null && audioSource != null)
            audioSource.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    private IEnumerator DespawnAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        // FIX #1: Reset hasLaunched BEFORE deactivating so that if the pool
        // reuses this ball immediately after SetActive(false), Launch() won't
        // be silently blocked by a stale hasLaunched = true.
        hasLaunched = false;
        gameObject.SetActive(false);
    }

    // ─────────────────────────────────────────────
    //  Gizmos (Editor Visualisation)
    // ─────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (!hasLaunched) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, launchDirection * 2f);
    }
}