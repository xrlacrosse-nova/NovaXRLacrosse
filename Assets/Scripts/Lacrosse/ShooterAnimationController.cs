using System;
using UnityEngine;

/// <summary>
/// Plays the shooter avatar's shooting animation and reports the release moment, so
/// RandomLauncher can launch the ball when the stick actually lets it go instead of the
/// instant the countdown ends.
///
/// Flow: RandomLauncher calls PlayShot() -> the Shoot state restarts from its first frame ->
/// the first frame its normalized time reaches ReleaseNormalizedTime, OnRelease fires
/// (exactly once per PlayShot) -> RandomLauncher launches the ball.
///
/// The Shoot state is started with Animator.Play, so the Animator Controller needs no
/// parameters or triggers. Expected controller (built in the Editor):
///   - Idle (default): the Shoot clip at Speed 0, which holds its first frame as the stance.
///   - Shoot: the Shoot clip at Speed 1, Loop Time off on the clip.
///   - Shoot -> Idle: Has Exit Time = 1, short blend, so it returns to the stance on its own.
/// Idle is optional: without it the avatar just holds the clip's last frame between shots.
///
/// Setup: attach to the shooter avatar (the GameObject with the Animator), then drag the
/// avatar into RandomLauncher's Shooter Animation field. Use the "Test Shot" context menu in
/// Play mode to tune ReleaseNormalizedTime.
/// </summary>
public class ShooterAnimationController : MonoBehaviour
{
    [Header("References (auto-found if empty)")]
    [Tooltip("The avatar's Animator. Found on this GameObject or its children if left empty.")]
    public Animator animator;

    [Tooltip("Ends the animation on session end. Found in the scene if left empty.")]
    public RandomLauncher launcher;

    [Header("States")]
    [Tooltip("Animator state that plays the shooting clip.")]
    public string shootStateName = "Shoot";

    [Tooltip("Animator state for the between-shots stance. Optional.")]
    public string idleStateName = "Idle";

    [Header("Release Timing")]
    [Tooltip("How far through the Shoot state (0 = start, 1 = end) the ball leaves the stick. " +
             "Tune in Play mode with the Test Shot context menu.")]
    [Range(0f, 1f)]
    public float releaseNormalizedTime = 0.6f;

    [Tooltip("If the Shoot state hasn't started within this many seconds (e.g. the state name " +
             "is wrong), release anyway so a session can never hang waiting on the animation.")]
    [Min(0.1f)]
    public float releaseTimeout = 3f;

    [Tooltip("Blend time (seconds) when returning to Idle on session end.")]
    [Min(0f)]
    public float resetBlendTime = 0.15f;

    [Header("Debug")]
    [Tooltip("Log each shot start and release, including the clip time it released at.")]
    public bool logRelease = false;

    // ── Events ───────────────────────────────────────────────────

    /// <summary>Fired once per PlayShot(), on the frame the clip reaches ReleaseNormalizedTime
    /// (or when the timeout expires).</summary>
    public event Action OnRelease;

    // ── Private ───────────────────────────────────────────────────

    private int _shootHash;
    private int _idleHash;

    private bool _watching = false;
    private int _playFrame;
    private float _elapsed;
    private float _lastNormalizedTime;
    private float _stalled;

    /// <summary>True if there's an Animator with a controller that has the Shoot state.
    /// RandomLauncher falls back to instant launches when this is false.</summary>
    public bool IsAvailable =>
        animator != null &&
        animator.runtimeAnimatorController != null &&
        animator.HasState(0, _shootHash);

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();

        _shootHash = Animator.StringToHash(shootStateName);
        _idleHash = Animator.StringToHash(idleStateName);

        if (!IsAvailable)
            Debug.LogWarning($"[ShooterAnimationController] No Animator with a '{shootStateName}' state " +
                             "found — shots will launch instantly with no animation.", this);
    }

    void OnEnable()
    {
        if (launcher != null)
            launcher.OnSessionEnded += ResetToIdle;
    }

    void OnDisable()
    {
        if (launcher != null)
            launcher.OnSessionEnded -= ResetToIdle;

        _watching = false;
    }

    void Update()
    {
        if (!_watching) return;

        // The Animator applies Play() during its own update after this frame's Update, so the
        // state info still shows the previous state on the frame PlayShot() was called — skip it.
        if (Time.frameCount > _playFrame)
        {
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.shortNameHash == _shootHash)
            {
                if (state.normalizedTime >= releaseNormalizedTime)
                {
                    Release(state.normalizedTime, timedOut: false);
                    return;
                }

                // The clip should be advancing toward the release point. Only give up if its
                // time stops advancing (Animator speed 0, culled, etc.), not on a long windup.
                if (state.normalizedTime > _lastNormalizedTime)
                {
                    _lastNormalizedTime = state.normalizedTime;
                    _stalled = 0f;
                    return;
                }

                _stalled += Time.deltaTime;
                if (_stalled >= releaseTimeout)
                {
                    Debug.LogWarning($"[ShooterAnimationController] '{shootStateName}' stopped advancing for " +
                                     $"{releaseTimeout}s — releasing anyway. Check Animator speed/culling.", this);
                    Release(-1f, timedOut: true);
                }
                return;
            }
        }

        _elapsed += Time.deltaTime;
        if (_elapsed >= releaseTimeout)
        {
            Debug.LogWarning($"[ShooterAnimationController] Shoot state did not start within {releaseTimeout}s " +
                             $"— releasing anyway. Check that the '{shootStateName}' state plays the shooting clip.", this);
            Release(-1f, timedOut: true);
        }
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Restarts the Shoot state from its first frame and starts watching for the
    /// release point. OnRelease fires once, later. Without an available Animator, OnRelease
    /// fires immediately so callers never wait forever.</summary>
    public void PlayShot()
    {
        if (!IsAvailable)
        {
            OnRelease?.Invoke();
            return;
        }

        animator.Play(_shootHash, 0, 0f);

        _watching = true;
        _playFrame = Time.frameCount;
        _elapsed = 0f;
        _lastNormalizedTime = -1f;
        _stalled = 0f;

        if (logRelease)
            Debug.Log("[ShooterAnimationController] Shot started.");
    }

    /// <summary>Cancels any pending release and blends back to the Idle stance (if the
    /// controller has an Idle state).</summary>
    public void ResetToIdle()
    {
        _watching = false;

        if (animator != null && animator.runtimeAnimatorController != null && animator.HasState(0, _idleHash))
            animator.CrossFadeInFixedTime(_idleHash, resetBlendTime, 0);
    }

    // ── Private helpers ───────────────────────────────────────────

    private void Release(float normalizedTime, bool timedOut)
    {
        _watching = false;

        if (logRelease && !timedOut)
            Debug.Log($"[ShooterAnimationController] Release at normalized time {normalizedTime:0.000}.");

        OnRelease?.Invoke();
    }

    [ContextMenu("Test Shot")]
    void TestShot()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ShooterAnimationController] Test Shot only works in Play mode.", this);
            return;
        }

        logRelease = true;
        PlayShot();
    }
}
