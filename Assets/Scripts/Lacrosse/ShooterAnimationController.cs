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
/// The Shoot state is started with Animator.Play. Expected controller (built in the Editor):
///   - Mirror: a Bool parameter, Shoot's Mirror Parameter (not its plain Mirror box).
///   - Idle (default): the Shoot clip at Speed 0, which holds its first frame as the stance.
///   - IdleRight: a copy of Idle with its plain Mirror box ticked, the right-handed stance.
///   - Shoot: the Shoot clip at Speed 1, Loop Time off on the clip.
///   - No Shoot -> Idle transition: once Shoot ends, this script blends into the idle of the
///     NEXT shot's hand, which a fixed transition can't do.
/// Idle is optional: without it the avatar just holds the clip's last frame between shots.
/// Without Mirror every shot is left-handed; without IdleRight the hand snaps at the windup.
///
/// Handedness (Plans/Current/Mirrored_Shot_Plan.md): each shot's hand is rolled between
/// shots (at session start and right after each release), and the stance blends to that hand's
/// idle during the shot interval with the stick hidden, so the goalie can read the hands before
/// the windup. The stick's LacrosseStickGrip is switched to the matching grip set.
/// Mirroring also mirrors the body's facing, so the right-handed stance turns the avatar by
/// Right Handed Yaw Offset (during the same blend). Mirror Stick Grip To Right Hand derives
/// the right-handed grip from the tuned left-handed one, so the stick needn't be posed by hand.
///
/// Setup: attach to the shooter avatar (the GameObject with the Animator), then drag the
/// avatar into RandomLauncher's Shooter Animation field. Use the "Test Shot" context menu in
/// Play mode to tune ReleaseNormalizedTime.
/// </summary>
// Runs after LacrosseStickGrip (default order 0), so on the release frame the stick is already
// posed for this frame's animation and the ball spawns where the stick head actually is.
[DefaultExecutionOrder(100)]
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

    [Tooltip("Animator state for the right-handed stance (Idle with Mirror ticked). Optional.")]
    public string rightIdleStateName = "IdleRight";

    [Tooltip("Bool parameter the Shoot state's Mirror Parameter reads. True = right-handed shot.")]
    public string mirrorParameterName = "Mirror";

    /// <summary>Which hand the shooter shoots with.</summary>
    public enum Handedness { LeftOnly, RightOnly, Random }

    [Header("Handedness")]
    [Tooltip("Left Only / Right Only: every shot with that hand. Random: each shot rolled on its " +
             "own (Right Handed Chance), mixed in the same session.")]
    public Handedness handedness = Handedness.Random;

    [Tooltip("Chance a shot is right-handed, used by Random.")]
    [Range(0f, 1f)]
    public float rightHandedChance = 0.5f;

    [Tooltip("Blend time (seconds) from the end of a shot, or at session start, into the next " +
             "shot's idle stance.")]
    [Min(0f)]
    public float idleBlendTime = 0.3f;

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

    [Header("Stick Hiding")]
    [Tooltip("The shooter's stick. Its renderers are hidden from shortly after release until " +
             "the avatar is back in Idle, so the follow-through can't clip it through the torso. " +
             "Leave empty to never hide it. Only renderers are turned off, so the grip and " +
             "BallReleasePoint keep working.")]
    public GameObject stick;

    [Tooltip("The stick's grip, switched to the shot's hand. Found on Stick if left empty.")]
    public LacrosseStickGrip stickGrip;

    [Tooltip("Seconds after release before the stick is hidden.")]
    [Min(0f)]
    public float hideStickDelay = 0.1f;

    [Header("Right-Handed Facing")]
    [Tooltip("Transform turned for the right-handed stance. Leave empty for this GameObject.")]
    public Transform bodyRoot;

    [Tooltip("Degrees around Y added to the avatar's scene rotation for the right-handed stance " +
             "(mirroring flips the clip's facing). E.g. scene Y 120, right-handed should face 240 " +
             "-> 120. Drag it live in Play mode while in the right-handed stance.")]
    [Range(-180f, 180f)]
    public float rightHandedYawOffset = 0f;

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
    private int _rightIdleHash;
    private int _mirrorHash;
    private bool _hasIdle;
    private bool _hasRightIdle;
    private bool _canMirror;

    private bool _shotRightHanded;    // hand of the shot last started by PlayShot
    private bool _nextRightHanded;    // hand of the next shot, rolled between shots
    private bool _stanceRightHanded;  // hand the avatar is posed in (or blending to)
    private bool _returnPending;      // released; blend to the next shot's idle once Shoot ends

    private Quaternion _leftBodyRotation;   // bodyRoot's scene rotation = the left-handed facing
    private Quaternion _turnFrom, _turnTo;
    private float _turnElapsed, _turnDuration;
    private bool _turning;
    private bool _initialized;

    private bool _watching = false;
    private int _playFrame;
    private float _elapsed;
    private float _lastNormalizedTime;
    private float _stalled;

    private Renderer[] _stickRenderers;
    private float _hideStickTimer = -1f;   // counting down to hide; < 0 = not pending
    private bool _stickHidden = false;

    /// <summary>True if there's an Animator with a controller that has the Shoot state.
    /// RandomLauncher falls back to instant launches when this is false.</summary>
    public bool IsAvailable =>
        animator != null &&
        animator.runtimeAnimatorController != null &&
        animator.HasState(0, _shootHash);

    /// <summary>Hand of the shot most recently started (true = right-handed). Read at launch by
    /// SessionLogger.</summary>
    public bool CurrentShotRightHanded => _shotRightHanded;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();

        _shootHash = Animator.StringToHash(shootStateName);
        _idleHash = Animator.StringToHash(idleStateName);
        _rightIdleHash = Animator.StringToHash(rightIdleStateName);
        _mirrorHash = Animator.StringToHash(mirrorParameterName);

        if (bodyRoot == null)
            bodyRoot = transform;
        _leftBodyRotation = bodyRoot.localRotation;
        _initialized = true;

        if (stick != null)
        {
            _stickRenderers = stick.GetComponentsInChildren<Renderer>(true);
            if (stickGrip == null)
                stickGrip = stick.GetComponentInChildren<LacrosseStickGrip>(true);
        }

        if (!IsAvailable)
        {
            Debug.LogWarning($"[ShooterAnimationController] No Animator with a '{shootStateName}' state " +
                             "found — shots will launch instantly with no animation.", this);
            return;
        }

        _hasIdle = animator.HasState(0, _idleHash);
        _hasRightIdle = animator.HasState(0, _rightIdleHash);
        foreach (AnimatorControllerParameter p in animator.parameters)
            if (p.nameHash == _mirrorHash && p.type == AnimatorControllerParameterType.Bool)
                _canMirror = true;

        if (!_canMirror && handedness != Handedness.LeftOnly)
            Debug.LogWarning($"[ShooterAnimationController] No '{mirrorParameterName}' Bool parameter in the " +
                             "Animator Controller — every shot will be left-handed.", this);
        else if (_canMirror && !_hasRightIdle && handedness != Handedness.LeftOnly)
            Debug.LogWarning($"[ShooterAnimationController] No '{rightIdleStateName}' state — right-handed " +
                             "shots will snap to the other hand at the windup.", this);

        SetGrip(false);
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += ResetToIdle;
        }
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= ResetToIdle;
        }

        _watching = false;
        _returnPending = false;
        _hideStickTimer = -1f;
        SetStickVisible(true);
    }

    // Update, before the Animator and the stick grip, so the stick is solved from this frame's
    // facing.
    void Update()
    {
        if (!_turning) return;

        _turnElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(_turnElapsed / _turnDuration);
        bodyRoot.localRotation = Quaternion.Slerp(_turnFrom, _turnTo, Mathf.SmoothStep(0f, 1f, t));
        if (t >= 1f)
            _turning = false;
    }

    // Right Handed Yaw Offset edits in Play mode show right away, even while paused.
    void OnValidate()
    {
        if (Application.isPlaying && _initialized && !_turning && bodyRoot != null)
            bodyRoot.localRotation = BodyRotationFor(_stanceRightHanded);
    }

    /// <summary>LateUpdate, not Update: the Animator has evaluated this frame and the stick grip
    /// has posed the stick, so the release check and the launch point both match what's on
    /// screen. In Update both were a frame old, and at the fast part of the swing that put the
    /// ball well off the stick head.</summary>
    void LateUpdate()
    {
        UpdateReturnToIdle();
        UpdateStickVisibility();

        if (!_watching) return;

        // Skip the frame PlayShot() was called: if it was called after the Animator's update,
        // the state info still shows the previous state (e.g. Shoot held on its last frame,
        // which would read as already past the release point).
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

    /// <summary>Restarts the Shoot state from its first frame, in the hand already rolled for
    /// this shot, and starts watching for the release point. OnRelease fires once, later.
    /// Without an available Animator, OnRelease fires immediately so callers never wait forever.</summary>
    public void PlayShot()
    {
        PlayShot(_nextRightHanded);
    }

    private void PlayShot(bool rightHanded)
    {
        if (!IsAvailable)
        {
            OnRelease?.Invoke();
            return;
        }

        rightHanded &= _canMirror;
        _shotRightHanded = rightHanded;
        _stanceRightHanded = rightHanded;
        _returnPending = false;

        // Set before Play so the windup's first frame is already in this hand. If the stance
        // didn't get to blend over (no IdleRight, or a shot interval shorter than the blend),
        // this is where the hand snaps.
        if (_canMirror)
            animator.SetBool(_mirrorHash, rightHanded);
        SetGrip(rightHanded);
        TurnBody(rightHanded, 0f);

        animator.Play(_shootHash, 0, 0f);

        // A new windup always starts with the stick in hand.
        _hideStickTimer = -1f;
        SetStickVisible(true);

        _watching = true;
        _playFrame = Time.frameCount;
        _elapsed = 0f;
        _lastNormalizedTime = -1f;
        _stalled = 0f;

        if (logRelease)
            Debug.Log($"[ShooterAnimationController] Shot started ({HandName(rightHanded)}).");
    }

    /// <summary>Cancels any pending release and blends back to the idle stance of the hand the
    /// avatar is in (if the controller has an Idle state).</summary>
    public void ResetToIdle()
    {
        _watching = false;

        if (IsAvailable)
            BlendToStance(_stanceRightHanded, resetBlendTime);
    }

    // ── Private helpers ───────────────────────────────────────────

    private void Release(float normalizedTime, bool timedOut)
    {
        _watching = false;

        if (logRelease && !timedOut)
            Debug.Log($"[ShooterAnimationController] Release at normalized time {normalizedTime:0.000} " +
                      $"({HandName(_shotRightHanded)}).");

        // A long frame can carry the clip well past the release point before it's seen, so the
        // ball leaves the stick later in the swing than tuned. Always worth knowing about.
        if (!timedOut && normalizedTime - releaseNormalizedTime > 0.05f)
            Debug.LogWarning($"[ShooterAnimationController] Release fired late, at normalized time " +
                             $"{normalizedTime:0.000} (target {releaseNormalizedTime:0.000}) — a frame hitch " +
                             "carried the clip past the release point.", this);

        if (_stickRenderers != null)
            _hideStickTimer = hideStickDelay;

        OnRelease?.Invoke();

        // Roll the next shot's hand now, after the launch has read this shot's hand; the stance
        // blends to it once Shoot finishes (UpdateReturnToIdle).
        _nextRightHanded = RollHand();
        _returnPending = true;
    }

    /// <summary>Session start: roll the first shot's hand, and if it isn't the current stance,
    /// blend over during the countdown with the stick hidden.</summary>
    private void HandleSessionStarted()
    {
        if (!IsAvailable) return;

        _nextRightHanded = RollHand();
        if (StanceFor(_nextRightHanded) != _stanceRightHanded)
            BlendToStance(_nextRightHanded, idleBlendTime);
    }

    /// <summary>After a release: once Shoot has reached its end, blend into the next shot's idle.
    /// The return to the stance and the hand switch are one blend.</summary>
    private void UpdateReturnToIdle()
    {
        if (!_returnPending || _watching || animator == null || animator.IsInTransition(0)) return;

        // An old Shoot -> Idle transition left in the controller may already have taken it to
        // Idle; blending from there to the right idle still works.
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        if (state.shortNameHash == _shootHash && state.normalizedTime < 1f) return;

        BlendToStance(_nextRightHanded, idleBlendTime);
    }

    /// <summary>Cross-fades to the idle of the given hand (Idle if there's no IdleRight) and
    /// switches the grip to it. Hides the stick if the hand changes, so the hands crossing over
    /// don't drag it through the body; it reappears once the idle has settled.</summary>
    private void BlendToStance(bool rightHanded, float blendTime)
    {
        _returnPending = false;
        if (!_hasIdle) return;

        bool stance = StanceFor(rightHanded);
        if (stance != _stanceRightHanded)
        {
            _hideStickTimer = -1f;
            SetStickVisible(false);
        }
        _stanceRightHanded = stance;
        SetGrip(stance);
        TurnBody(stance, blendTime);

        animator.CrossFadeInFixedTime(stance ? _rightIdleHash : _idleHash, blendTime, 0);
    }

    // The stance a hand can actually be shown in: right only with an IdleRight state.
    private bool StanceFor(bool rightHanded) => rightHanded && _hasRightIdle;

    private bool RollHand()
    {
        if (!_canMirror) return false;

        switch (handedness)
        {
            case Handedness.LeftOnly: return false;
            case Handedness.RightOnly: return true;
            default: return UnityEngine.Random.value < rightHandedChance;
        }
    }

    private void SetGrip(bool rightHanded)
    {
        if (stickGrip != null)
            stickGrip.rightHanded = rightHanded;
    }

    private Quaternion BodyRotationFor(bool rightHanded) =>
        rightHanded ? Quaternion.Euler(0f, rightHandedYawOffset, 0f) * _leftBodyRotation : _leftBodyRotation;

    /// <summary>Turns the avatar to the given hand's facing over the given time (0 = now).</summary>
    private void TurnBody(bool rightHanded, float duration)
    {
        if (bodyRoot == null) return;

        _turnFrom = bodyRoot.localRotation;
        _turnTo = BodyRotationFor(rightHanded);
        _turnElapsed = 0f;
        _turnDuration = duration;
        _turning = duration > 0f;
        if (!_turning)
            bodyRoot.localRotation = _turnTo;
    }

    private static string HandName(bool rightHanded) => rightHanded ? "right-handed" : "left-handed";

    /// <summary>Counts down to hiding the stick after a release, then shows it again once the
    /// avatar has settled in the idle of its stance (or, without an Idle state, once Shoot has
    /// finished).</summary>
    private void UpdateStickVisibility()
    {
        if (_hideStickTimer >= 0f)
        {
            _hideStickTimer -= Time.deltaTime;
            if (_hideStickTimer < 0f)
                SetStickVisible(false);
            return;
        }

        if (!_stickHidden || _returnPending || animator == null || animator.IsInTransition(0)) return;

        // Only the stance's own idle counts, so the stick stays hidden on the frame a blend is
        // started (still in Shoot or the other idle) and through the blend itself.
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        bool settled = _hasIdle
            ? state.shortNameHash == (_stanceRightHanded ? _rightIdleHash : _idleHash)
            : state.shortNameHash == _shootHash && state.normalizedTime >= 1f;
        if (settled)
            SetStickVisible(true);
    }

    private void SetStickVisible(bool visible)
    {
        if (_stickRenderers == null) return;

        foreach (Renderer r in _stickRenderers)
            if (r != null) r.enabled = visible;
        _stickHidden = !visible;
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

    [ContextMenu("Test Shot (Left)")]
    void TestShotLeft() => TestShotWithHand(false);

    [ContextMenu("Test Shot (Right)")]
    void TestShotRight() => TestShotWithHand(true);

    /// <summary>
    /// Works out the right-handed grip from the tuned left-handed one: poses Idle, solves the
    /// left-handed stick, mirrors it across the avatar's center plane, poses IdleRight and
    /// captures that as Right Handed Grip (top hand fix included). Leaves the avatar in the
    /// right-handed stance so the result shows, even while paused. Play mode only; Copy
    /// Component on the stick before leaving Play mode.
    /// </summary>
    [ContextMenu("Mirror Stick Grip To Right Hand")]
    void MirrorStickGripToRightHand()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ShooterAnimationController] Mirror Stick Grip only works in Play mode.", this);
            return;
        }
        if (!IsAvailable || !_hasIdle || !_hasRightIdle || stickGrip == null)
        {
            Debug.LogWarning($"[ShooterAnimationController] Mirror Stick Grip needs the '{idleStateName}' and " +
                             $"'{rightIdleStateName}' states and a Stick Grip.", this);
            return;
        }

        _watching = false;
        _returnPending = false;
        _hideStickTimer = -1f;
        SetStickVisible(true);

        // Both poses are sampled at the same facing, so the mirror plane is the same for both.
        TurnBody(true, 0f);
        bool ok = stickGrip.MirrorLeftGripOntoRight(animator.transform,
            () => PoseNow(_idleHash), () => PoseNow(_rightIdleHash));

        _stanceRightHanded = ok;
        if (!ok)
        {
            TurnBody(false, 0f);
            PoseNow(_idleHash);
        }
    }

    // Poses the avatar in a state's first frame right now (works while paused).
    private void PoseNow(int stateHash)
    {
        animator.Play(stateHash, 0, 0f);
        animator.Update(0f);
    }

    // Plays the chosen hand straight from the current stance, so it may snap at the windup.
    private void TestShotWithHand(bool rightHanded)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ShooterAnimationController] Test Shot only works in Play mode.", this);
            return;
        }
        if (rightHanded && !_canMirror)
            Debug.LogWarning($"[ShooterAnimationController] No '{mirrorParameterName}' parameter — " +
                             "playing left-handed.", this);

        logRelease = true;
        PlayShot(rightHanded);
    }
}
