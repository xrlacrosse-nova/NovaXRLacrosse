using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Puts the world in the right place around the goalie instead of trusting the origin the
/// headset picked at launch (see Current_Plan/Setup_And_Recenter_UX_Plan.md and
/// Current_Plan/Floor_Bug_Plan.md). The headset sometimes sets its origin from a bad pose, and then
/// the goal, crease and lines are all misplaced together.
///
/// Re-placing moves only the ML Rig (this XROrigin), never WorldOrigin, so everything in the world
/// keeps its coordinates and stays lined up with everything else:
///   1. "Please stand in an open space and look straight ahead." (at PromptSpeechRate): waits until
///      the head is tracked, level and still for SteadySeconds. Repeats the line every RepromptSeconds.
///   2. Height: the rig goes to its scene height plus the player's floor nudge, so the headset's own
///      floor (Floor tracking mode) is the virtual floor. The game never judges that floor: no height
///      checks, no fallback. The player sees it and fixes it with the nudge (or by relaunching in an
///      open space; RECENTER keeps the same floor).
///   3. Launch and RECENTER turn the rig around the head (vertical axis only) so the head faces the
///      rig's scene forward. Automatic re-places (tracking reset, deferred to session end) keep the
///      current heading.
///   4. Slides the rig so the head is over the rig's scene position.
///   5. "Ready."
///
/// When: once at startup; again when the headset moves its origin (trackingOriginUpdated); and from
/// Recenter() (the RECENTER gaze target, the only manual way). App resume does nothing. Never during
/// a session: a request mid-session waits until OnSessionEnded, then also waits for post-session
/// speech (the summary) to finish before its line.
/// While re-placing, IsPlacing is true and the gaze targets (START, RECENTER) hide.
///
/// Floor nudge: NudgeFloor() moves the world up/down by hand (settings board), up to
/// ±MaxFloorNudge. Idle only, not saved (0 at launch). RECENTER keeps it (same floor).
///
/// The goalie spot is the rig's scene pose, saved in Awake: its position, its forward (must point
/// at the shooter), and its height (the virtual floor).
///
/// Setup: put on the ML Rig (next to XROrigin). Drag in RandomLauncher and TextToSpeech.
/// </summary>
public class WorldPlacement : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The ML Rig's XROrigin. Empty = the one on this GameObject, or the first in the scene.")]
    public XROrigin xrOrigin;

    [Tooltip("Empty = find in scene. Used to refuse re-placing mid-session.")]
    public RandomLauncher launcher;

    [Tooltip("Speaks the prompts. Empty = find in scene; without one the prompts are only logged.")]
    public TextToSpeech tts;

    [Header("When")]
    [Tooltip("Re-place once when the app starts.")]
    public bool placeOnStartup = true;

    [Tooltip("Re-place when the headset moves its origin (e.g. after tracking loss).")]
    public bool replaceOnOriginChange = true;

    [Tooltip("Ignore origin-change events this many seconds after a re-place finishes.")]
    [Min(0f)]
    public float originChangeCooldown = 3f;

    [Header("Prompts")]
    public string lookAheadPrompt = "Please stand in an open space and look straight ahead.";
    public string readyPrompt = "Ready.";

    [Tooltip("Speech speed for the setup lines only (1 = normal). Other speech keeps " +
             "TextToSpeech.speechRate.")]
    [Range(0.5f, 2f)]
    public float promptSpeechRate = 0.8f;

    [Tooltip("Repeat the prompt if it hasn't been followed after this many seconds (e.g. the headset " +
             "was on a table when it was first said).")]
    [Min(1f)]
    public float repromptSeconds = 8f;

    [Tooltip("After a session ends, wait at least this long (and until speech has finished) before " +
             "the setup line, so the session summary (spoken after its own delay) isn't cut off.")]
    [Min(0f)]
    public float afterSessionSpeechWait = 2f;

    [Header("Head checks")]
    [Tooltip("Head within this many degrees of level counts as looking ahead.")]
    [Range(5f, 60f)]
    public float levelPitchMax = 25f;

    [Tooltip("Head turning slower than this counts as still.")]
    [Min(1f)]
    public float steadyMaxDegreesPerSecond = 15f;

    [Min(0.1f)]
    public float steadySeconds = 1f;

    [Header("Debug")]
    [Tooltip("Log the head and rig pose on every re-place and origin event, and every 0.5 s for " +
             "Startup Log Seconds after launch.")]
    public bool logPlacement = true;

    [Min(0f)]
    public float startupLogSeconds = 20f;

    [Tooltip("Editor only: head checks always pass (each step still waits its hold time).")]
    public bool editorSkipHeadChecks = true;

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Largest floor nudge either way, in metres. A bigger miss is RECENTER's job.</summary>
    public const float MaxFloorNudge = 0.90f;

    /// <summary>True from the start of a re-place until "Ready" (or until a session interrupts it).</summary>
    public bool IsPlacing { get; private set; }

    /// <summary>Fires with true when re-placing starts and false when it ends.</summary>
    public event Action<bool> OnPlacingChanged;

    /// <summary>The player's floor nudge in metres (+ = world up). 0 at launch.</summary>
    public float FloorNudge { get; private set; }

    /// <summary>Fires with the new nudge whenever it changes.</summary>
    public event Action<float> OnFloorNudgeChanged;

    /// <summary>
    /// Starts a re-place with prompts that also turns the world to face the player. Same floor; keeps
    /// the floor nudge. Refused (returns false) mid-session or while one is already running.
    /// </summary>
    public bool Recenter() => Request("manual", deferIfSession: false, turn: true);

    /// <summary>
    /// Moves the world up (+) or down (-) by <paramref name="meters"/>. The total stays within
    /// ±MaxFloorNudge: a step past it moves only up to the limit, and at the limit it does nothing.
    /// Refused (returns false) while placing, mid-session or at the limit. No checks on the floor
    /// itself: the player decides.
    /// </summary>
    public bool NudgeFloor(float meters)
    {
        if (!enabled || IsPlacing || SessionRunning) return false;

        float next = Mathf.Clamp(FloorNudge + meters, -MaxFloorNudge, MaxFloorNudge);
        next = Mathf.Round(next * 100f) / 100f; // whole centimetres, no float drift
        if (Mathf.Approximately(next, FloorNudge)) return false;

        SetFloorNudge(next);
        Debug.Log($"[WorldPlacement] Floor nudge {FloorNudge * 100f:+0;-0;0} cm.");
        return true;
    }

    // ── Private ───────────────────────────────────────────────────

    private const float LogInterval = 0.5f;

    private Transform _rig;
    private Transform _head;
    private Vector3 _designSpot;
    private Vector3 _designForward;
    private float _designFloorY;

    private bool _pending;
    private bool _pendingTurn; // a deferred request that should turn (e.g. startup) keeps that
    private bool _aborted;
    private float _cooldownUntil;
    private Coroutine _routine;

    private Quaternion _prevHeadRotation;
    private float _headTurnSpeed;

    private float _nextLog;
    private readonly List<XRInputSubsystem> _inputSubsystems = new List<XRInputSubsystem>();

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (xrOrigin == null) xrOrigin = GetComponent<XROrigin>();
        if (xrOrigin == null) xrOrigin = FindFirstObjectByType<XROrigin>();
        if (launcher == null) launcher = FindFirstObjectByType<RandomLauncher>();
        if (tts == null) tts = FindFirstObjectByType<TextToSpeech>();

        if (xrOrigin == null || xrOrigin.Camera == null)
        {
            Debug.LogError("[WorldPlacement] No XROrigin with a camera — disabling.");
            enabled = false;
            return;
        }

        _rig = xrOrigin.Origin.transform;
        _head = xrOrigin.Camera.transform;

        _designSpot = _rig.position;
        _designForward = Flat(_rig.forward);
        if (_designForward == Vector3.zero) _designForward = Vector3.forward;
        _designFloorY = _rig.position.y;
        _prevHeadRotation = _head.rotation;
    }

    void OnEnable()
    {
        if (launcher != null)
            launcher.OnSessionEnded += HandleSessionEnded;
    }

    void OnDisable()
    {
        if (launcher != null)
            launcher.OnSessionEnded -= HandleSessionEnded;

        foreach (var subsystem in _inputSubsystems)
            subsystem.trackingOriginUpdated -= HandleOriginUpdated;
        _inputSubsystems.Clear();

        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
            SetPlacing(false);
        }
    }

    void Start()
    {
        StartCoroutine(SubscribeToOriginEvents());

        if (placeOnStartup)
            Request("startup", deferIfSession: true, turn: true);
    }

    void Update()
    {
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        _headTurnSpeed = Quaternion.Angle(_prevHeadRotation, _head.rotation) / dt;
        _prevHeadRotation = _head.rotation;

        if (logPlacement && Time.realtimeSinceStartup <= startupLogSeconds && Time.time >= _nextLog)
        {
            _nextLog = Time.time + LogInterval;
            LogPose("startup");
        }
    }

    // ── Requests ──────────────────────────────────────────────────

    private bool Request(string reason, bool deferIfSession, bool turn, bool afterSession = false)
    {
        if (!enabled) return false;

        if (IsPlacing)
        {
            Debug.Log($"[WorldPlacement] Re-place ({reason}) ignored: already re-placing.");
            return false;
        }

        if (SessionRunning)
        {
            if (deferIfSession)
            {
                _pending = true;
                _pendingTurn |= turn;
                Debug.Log($"[WorldPlacement] Re-place ({reason}) waits until the session ends.");
            }
            else
            {
                Debug.Log($"[WorldPlacement] Re-place ({reason}) refused: session running.");
            }
            return false;
        }

        Debug.Log($"[WorldPlacement] Re-place started ({reason}, {(turn ? "turns" : "keeps heading")}).");
        _routine = StartCoroutine(PlaceRoutine(turn, afterSession));
        return true;
    }

    private bool SessionRunning => launcher != null && launcher.IsSessionRunning;

    private void HandleSessionEnded()
    {
        if (!_pending) return;
        bool turn = _pendingTurn;
        _pending = false;
        _pendingTurn = false;
        Request("after session", deferIfSession: true, turn: turn, afterSession: true);
    }

    private void HandleOriginUpdated(XRInputSubsystem subsystem)
    {
        LogPose("origin updated by headset");
        if (!replaceOnOriginChange || IsPlacing) return; // a running re-place measures fresh anyway
        if (Time.time < _cooldownUntil)
        {
            Debug.Log("[WorldPlacement] Origin update ignored (just re-placed).");
            return;
        }
        Request("origin updated", deferIfSession: true, turn: false);
    }

    private IEnumerator SubscribeToOriginEvents()
    {
        // Input subsystems may come up a little after the scene.
        float giveUpAt = Time.time + 10f;
        while (_inputSubsystems.Count == 0 && Time.time < giveUpAt)
        {
            SubsystemManager.GetSubsystems(_inputSubsystems);
            if (_inputSubsystems.Count == 0)
                yield return new WaitForSeconds(0.5f);
        }

        foreach (var subsystem in _inputSubsystems)
            subsystem.trackingOriginUpdated += HandleOriginUpdated;

        if (_inputSubsystems.Count == 0 && !Application.isEditor)
            Debug.LogWarning("[WorldPlacement] No XRInputSubsystem — origin changes won't re-place.");
    }

    // ── Re-place ──────────────────────────────────────────────────

    private IEnumerator PlaceRoutine(bool turn, bool afterSession)
    {
        SetPlacing(true);
        _aborted = false;

        if (afterSession)
            yield return WaitForSpeechToFinish();

        if (!_aborted)
            yield return WaitForHead(lookAheadPrompt, IsLookingAheadSteady, steadySeconds);

        if (_aborted)
        {
            // Only a session starting stops it; finish once that session ends.
            _pending = true;
            _pendingTurn |= turn;
            Debug.Log("[WorldPlacement] Re-place interrupted by a session; will finish after it.");
        }
        else
        {
            Place(turn);
            Say(readyPrompt);
        }

        _cooldownUntil = Time.time + originChangeCooldown;
        _routine = null;
        SetPlacing(false);
    }

    /// <summary>
    /// After a session: waits afterSessionSpeechWait (the summary starts after its own delay), then
    /// until nothing is being spoken. Sets _aborted if a session starts.
    /// </summary>
    private IEnumerator WaitForSpeechToFinish()
    {
        float earliest = Time.time + afterSessionSpeechWait;
        while (Time.time < earliest || (tts != null && tts.IsSpeaking))
        {
            if (SessionRunning)
            {
                _aborted = true;
                yield break;
            }
            yield return null;
        }
    }

    /// <summary>
    /// Says the prompt and waits until the head check has held for holdSeconds. Repeats the prompt
    /// every RepromptSeconds. Sets _aborted if a session starts.
    /// </summary>
    private IEnumerator WaitForHead(string prompt, Func<bool> check, float holdSeconds)
    {
        Say(prompt);
        float nextPrompt = Time.time + repromptSeconds;
        float heldSince = -1f;

        while (true)
        {
            if (SessionRunning)
            {
                _aborted = true;
                yield break;
            }

            bool ok = (Application.isEditor && editorSkipHeadChecks) || (IsHeadTracked() && check());
            if (ok)
            {
                if (heldSince < 0f) heldSince = Time.time;
                if (Time.time - heldSince >= holdSeconds) yield break;
            }
            else
            {
                heldSince = -1f;
                if (Time.time >= nextPrompt)
                {
                    Say(prompt);
                    nextPrompt = Time.time + repromptSeconds;
                }
            }

            yield return null;
        }
    }

    /// <summary>
    /// Sets the nudge and moves the rig's height to match. The rig goes the opposite way to the
    /// nudge: lowering the rig (and the head with it) makes the world look higher.
    /// </summary>
    private void SetFloorNudge(float nudge)
    {
        if (Mathf.Approximately(nudge, FloorNudge)) return;
        FloorNudge = nudge;
        Vector3 position = _rig.position;
        position.y = RigHeight;
        _rig.position = position;
        OnFloorNudgeChanged?.Invoke(FloorNudge);
    }

    /// <summary>The headset's floor, shifted by the player's nudge. No other height is ever used.</summary>
    private float RigHeight => _designFloorY - FloorNudge;

    /// <summary>
    /// Puts the rig at the headset's floor (plus the player's nudge), optionally turns it around the
    /// head to face the design forward, then slides it so the head is over the design spot.
    /// </summary>
    private void Place(bool turn)
    {
        Vector3 position = _rig.position;
        position.y = RigHeight;
        _rig.position = position;

        if (turn)
        {
            Vector3 facing = Flat(_head.forward);
            if (facing != Vector3.zero)
                xrOrigin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(facing, _designForward, Vector3.up));
        }

        xrOrigin.MoveCameraToWorldLocation(new Vector3(_designSpot.x, _head.position.y, _designSpot.z));

        LogPose($"re-placed ({(turn ? "turned" : "heading kept")}, floor nudge {FloorNudge * 100f:+0;-0;0} cm)");
    }

    private void SetPlacing(bool placing)
    {
        if (IsPlacing == placing) return;
        IsPlacing = placing;
        OnPlacingChanged?.Invoke(placing);
    }

    // ── Head ──────────────────────────────────────────────────────

    private float HeadPitch => Mathf.Asin(Mathf.Clamp(_head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

    private bool IsLookingAheadSteady() =>
        Mathf.Abs(HeadPitch) <= levelPitchMax && _headTurnSpeed <= steadyMaxDegreesPerSecond;

    private float EyeHeight => _head.position.y - _designFloorY;

    private static bool IsHeadTracked()
    {
        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid) return false;
        // Some runtimes don't report isTracked; treat that as tracked.
        return !head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || tracked;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.zero : v.normalized;
    }

    // ── Output ────────────────────────────────────────────────────

    private void Say(string text)
    {
        if (tts != null) tts.Speak(text, promptSpeechRate);
        else Debug.Log($"[WorldPlacement] (no TTS) {text}");
    }

    private void LogPose(string label)
    {
        if (!logPlacement || _rig == null) return;
        Vector3 headLocal = _rig.InverseTransformPoint(_head.position);
        Debug.Log($"[WorldPlacement] {label}: mode {xrOrigin.CurrentTrackingOriginMode}, " +
                  $"eye height {EyeHeight:F2} m, nudge {FloorNudge * 100f:+0;-0;0} cm, head local {headLocal:F2}, head yaw {_head.eulerAngles.y:F0}, " +
                  $"head pitch {HeadPitch:F0}, rig {_rig.position:F2} yaw {_rig.eulerAngles.y:F0}");
    }
}
