using System;
using System.Collections;
using System.Collections.Generic;
using MagicLeap.Android;
using MagicLeap.Examples;
using MagicLeap.OpenXR.Features.Planes;
using MagicLeap.OpenXR.Subsystems;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Puts the world in the right place around the goalie instead of trusting the origin the
/// headset picked at launch (see Plans/Current/World_Recenter_Plan.md). The headset sometimes
/// sets its origin from a bad pose (on a table, turned away, floor not seen), and then the goal,
/// crease and lines are all misplaced together.
///
/// Re-placing moves only the ML Rig (this XROrigin), never WorldOrigin, so everything in the world
/// keeps its coordinates and stays lined up with everything else:
///   1. "Look down at the floor": waits until the head has pitched down for LookDownSeconds, so
///      the headset sees the floor (and floor planes can be found).
///   2. "Look at the shooter": waits until the head is tracked, level and still.
///   3. Turns the rig around the head (vertical axis only) so the head faces the rig's scene
///      forward, slides it so the head is over the rig's scene position, and, if a steady floor
///      plane was found, sets the rig's height so that plane is the virtual floor.
///   4. Checks the head's height above the floor; if it isn't a standing height, says
///      "Floor not found" and goes back to step 1 (FloorRetries times). Then "Ready".
///
/// When: once at startup; again when the headset moves its origin (trackingOriginUpdated) or the
/// app resumes; from Recenter() (the RECENTER gaze target); and from holding the controller Menu
/// button. Never during a session: a request mid-session waits until OnSessionEnded.
/// While re-placing, IsPlacing is true and the gaze targets (START, RECENTER) hide.
///
/// The goalie spot is the rig's scene pose, saved in Awake: its position, its forward (must point
/// at the shooter), and its height (the virtual floor).
///
/// Setup: put on the ML Rig (next to XROrigin). Drag in RandomLauncher and TextToSpeech. Optional:
/// an ARPlaneManager on the ML Rig (Horizontal) for measured floor height; without it the
/// headset's own floor is kept and only checked.
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

    [Tooltip("Optional. ARPlaneManager (Horizontal) on the ML Rig, used to measure the real floor.")]
    public ARPlaneManager planeManager;

    [Header("When")]
    [Tooltip("Re-place once when the app starts.")]
    public bool placeOnStartup = true;

    [Tooltip("Re-place when the headset moves its origin or the app resumes (headset put back on).")]
    public bool replaceOnOriginChange = true;

    [Tooltip("Ignore origin-change events this many seconds after a re-place finishes.")]
    [Min(0f)]
    public float originChangeCooldown = 3f;

    [Tooltip("Holding the controller Menu button re-places (idle only).")]
    public bool menuHoldRecenter = true;

    [Min(0.1f)]
    public float menuHoldSeconds = 1f;

    [Header("Prompts")]
    public string lookDownPrompt = "Look down at the floor";
    public string lookAheadPrompt = "Look at the shooter";
    public string readyPrompt = "Ready";
    public string floorNotFoundPrompt = "Floor not found. Look at the floor.";

    [Tooltip("Repeat a prompt if it hasn't been followed after this many seconds (e.g. the headset " +
             "was on a table when it was first said).")]
    [Min(1f)]
    public float repromptSeconds = 8f;

    [Header("Head checks")]
    [Tooltip("Degrees below level that count as looking at the floor.")]
    [Range(10f, 80f)]
    public float lookDownPitch = 35f;

    [Min(0.1f)]
    public float lookDownSeconds = 1f;

    [Tooltip("Head within this many degrees of level counts as looking at the shooter.")]
    [Range(5f, 60f)]
    public float levelPitchMax = 25f;

    [Tooltip("Head turning slower than this counts as still.")]
    [Min(1f)]
    public float steadyMaxDegreesPerSecond = 15f;

    [Min(0.1f)]
    public float steadySeconds = 1f;

    [Header("Floor")]
    [Tooltip("Use the ARPlaneManager's floor planes to set the height (needs Spatial Mapping permission).")]
    public bool useFloorPlanes = true;

    [Tooltip("Only planes Magic Leap classifies as Floor. Off = any upward horizontal plane.")]
    public bool requireFloorClassification = true;

    [Min(0.1f)]
    public float minFloorArea = 1f;

    [Tooltip("A floor plane's height must stay within Tolerance for this long before it's used.")]
    [Min(0.1f)]
    public float floorStableSeconds = 1f;

    [Min(0.001f)]
    public float floorStableTolerance = 0.02f;

    [Tooltip("Head heights above the floor (m) that count as a standing player.")]
    public float minEyeHeight = 0.9f;
    public float maxEyeHeight = 2.2f;

    [Tooltip("Times to retry after \"Floor not found\" before accepting the result anyway.")]
    [Min(0)]
    public int floorRetries = 2;

    [Header("Debug")]
    [Tooltip("Log the head and rig pose on every re-place and origin event, and every 0.5 s for " +
             "Startup Log Seconds after launch.")]
    public bool logPlacement = true;

    [Min(0f)]
    public float startupLogSeconds = 20f;

    [Tooltip("Editor only: head checks always pass (each step still waits its hold time).")]
    public bool editorSkipHeadChecks = true;

    // ── Public API ────────────────────────────────────────────────

    /// <summary>True from the first prompt until "Ready" (or until a session interrupts it).</summary>
    public bool IsPlacing { get; private set; }

    /// <summary>Fires with true when re-placing starts and false when it ends.</summary>
    public event Action<bool> OnPlacingChanged;

    /// <summary>
    /// Starts a re-place with prompts. Refused (returns false) mid-session or while one is
    /// already running.
    /// </summary>
    public bool Recenter() => Request("manual", deferIfSession: false);

    // ── Private ───────────────────────────────────────────────────

    private const float LogInterval = 0.5f;
    private const float ConnectRetrySeconds = 2f;

    private Transform _rig;
    private Transform _head;
    private Vector3 _designSpot;
    private Vector3 _designForward;
    private float _designFloorY;

    private bool _pending;
    private bool _aborted;
    private float _cooldownUntil;
    private Coroutine _routine;

    private Quaternion _prevHeadRotation;
    private float _headTurnSpeed;

    private bool _planesAllowed;
    private bool _hasCandidate;
    private float _candidateY;
    private float _candidateSince;
    private bool _hasStableFloor;
    private float _stableFloorLocalY; // relative to the rig, so moving the rig doesn't change it

    private MagicLeapController _controller;
    private float _nextConnectAttempt;
    private bool _warnedNoController;
    private float _menuHeldSince = -1f;
    private bool _menuFired;

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
        SetUpFloorPlanes();
        StartCoroutine(SubscribeToOriginEvents());

        if (placeOnStartup)
            Request("startup", deferIfSession: true);
    }

    void Update()
    {
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        _headTurnSpeed = Quaternion.Angle(_prevHeadRotation, _head.rotation) / dt;
        _prevHeadRotation = _head.rotation;

        UpdateFloorPlane();
        UpdateMenuHold();

        if (logPlacement && Time.realtimeSinceStartup <= startupLogSeconds && Time.time >= _nextLog)
        {
            _nextLog = Time.time + LogInterval;
            LogPose("startup");
        }
    }

    void OnApplicationPause(bool paused)
    {
        if (!paused && replaceOnOriginChange && _rig != null)
        {
            LogPose("app resumed");
            Request("app resumed", deferIfSession: true);
        }
    }

    // ── Requests ──────────────────────────────────────────────────

    private bool Request(string reason, bool deferIfSession)
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
                Debug.Log($"[WorldPlacement] Re-place ({reason}) waits until the session ends.");
            }
            else
            {
                Debug.Log($"[WorldPlacement] Re-place ({reason}) refused: session running.");
            }
            return false;
        }

        Debug.Log($"[WorldPlacement] Re-place started ({reason}).");
        _routine = StartCoroutine(PlaceRoutine());
        return true;
    }

    private bool SessionRunning => launcher != null && launcher.IsSessionRunning;

    private void HandleSessionEnded()
    {
        if (!_pending) return;
        _pending = false;
        Request("after session", deferIfSession: true);
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
        Request("origin updated", deferIfSession: true);
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

    private IEnumerator PlaceRoutine()
    {
        SetPlacing(true);
        _aborted = false;

        for (int attempt = 0; ; attempt++)
        {
            yield return WaitForHead(lookDownPrompt, IsLookingDown, lookDownSeconds);
            if (_aborted) break;

            yield return WaitForHead(lookAheadPrompt, IsLookingAheadSteady, steadySeconds);
            if (_aborted) break;

            ApplyDirectionAndPosition();
            bool measured = ApplyFloor();

            float eye = EyeHeight;
            bool plausible = (Application.isEditor && editorSkipHeadChecks) ||
                             (eye >= minEyeHeight && eye <= maxEyeHeight);
            LogPose($"re-placed (floor {(measured ? "measured from plane" : "headset's own")}, " +
                    $"eye height {eye:F2} m{(plausible ? "" : ", NOT plausible")})");

            if (plausible || attempt >= floorRetries)
                break;

            Say(floorNotFoundPrompt);
            yield return new WaitForSeconds(2f);
        }

        if (_aborted)
        {
            // Only a session starting stops it; finish once that session ends.
            _pending = true;
            Debug.Log("[WorldPlacement] Re-place interrupted by a session; will finish after it.");
        }
        else
        {
            Say(readyPrompt);
        }

        _cooldownUntil = Time.time + originChangeCooldown;
        _routine = null;
        SetPlacing(false);
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

    /// <summary>Turns the rig around the head so it faces the design forward, then slides it so
    /// the head is over the design spot. Height untouched.</summary>
    private void ApplyDirectionAndPosition()
    {
        Vector3 facing = Flat(_head.forward);
        if (facing != Vector3.zero)
            xrOrigin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(facing, _designForward, Vector3.up));

        xrOrigin.MoveCameraToWorldLocation(new Vector3(_designSpot.x, _head.position.y, _designSpot.z));
    }

    /// <summary>Sets the rig's height so the steady floor plane is the virtual floor. Uses the
    /// plane's height relative to the rig, so applying it again gives the same result.</summary>
    private bool ApplyFloor()
    {
        if (!_hasStableFloor) return false;

        Vector3 position = _rig.position;
        position.y = _designFloorY - _stableFloorLocalY;
        _rig.position = position;
        return true;
    }

    private void SetPlacing(bool placing)
    {
        if (IsPlacing == placing) return;
        IsPlacing = placing;
        OnPlacingChanged?.Invoke(placing);
    }

    // ── Head ──────────────────────────────────────────────────────

    private float HeadPitch => Mathf.Asin(Mathf.Clamp(_head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

    private bool IsLookingDown() => HeadPitch <= -lookDownPitch;

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

    // ── Floor planes ──────────────────────────────────────────────

    private void SetUpFloorPlanes()
    {
        if (!useFloorPlanes || planeManager == null) return;

        // No planes in the Editor; on device, wait for the permission before scanning.
        planeManager.enabled = false;
        if (Application.isEditor) return;

        Permissions.RequestPermission(Permissions.SpatialMapping, OnPlanesPermissionGranted,
                                      OnPlanesPermissionDenied, OnPlanesPermissionDenied);
    }

    private void OnPlanesPermissionGranted(string permission)
    {
        _planesAllowed = true;
        planeManager.enabled = true;
    }

    private void OnPlanesPermissionDenied(string permission)
    {
        Debug.LogWarning("[WorldPlacement] Spatial Mapping permission denied — keeping the headset's own " +
                         "floor (checked, not measured).");
    }

    private void UpdateFloorPlane()
    {
        if (!_planesAllowed || planeManager == null || !planeManager.enabled) return;

        // Magic Leap only returns plane classifications when asked for them (as in PlaneExample).
        MLXrPlaneSubsystem.Query = new MLXrPlaneSubsystem.PlanesQuery
        {
            Flags = planeManager.requestedDetectionMode.ToMLXrQueryFlags() |
                    MLXrPlaneSubsystem.MLPlanesQueryFlags.SemanticAll,
            BoundsCenter = _head.position,
            BoundsRotation = _head.rotation,
            BoundsExtents = Vector3.one * 10f,
            MaxResults = 50,
            MinPlaneArea = minFloorArea
        };

        float headLocalY = _rig.InverseTransformPoint(_head.position).y;
        bool found = false;
        float bestArea = 0f;
        float bestY = 0f;

        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp) continue;
            if (requireFloorClassification && plane.classification != PlaneClassification.Floor) continue;

            float area = plane.size.x * plane.size.y;
            if (area < minFloorArea || area <= bestArea) continue;

            float localY = _rig.InverseTransformPoint(plane.center).y;
            float drop = headLocalY - localY;
            if (drop < minEyeHeight || drop > maxEyeHeight) continue; // a table, or something below a platform

            found = true;
            bestArea = area;
            bestY = localY;
        }

        if (!found)
        {
            _hasCandidate = false;
            return; // keep the last steady floor
        }

        if (!_hasCandidate || Mathf.Abs(bestY - _candidateY) > floorStableTolerance)
        {
            _hasCandidate = true;
            _candidateY = bestY;
            _candidateSince = Time.time;
        }
        else if (Time.time - _candidateSince >= floorStableSeconds)
        {
            _hasStableFloor = true;
            _stableFloorLocalY = _candidateY;
        }
    }

    // ── Menu hold ─────────────────────────────────────────────────

    private void UpdateMenuHold()
    {
        if (!menuHoldRecenter) return;

        if (_controller == null)
        {
            if (Time.time >= _nextConnectAttempt) TryConnectController();
            if (_controller == null) return;
        }

        if (!_controller.MenuIsPressed)
        {
            _menuHeldSince = -1f;
            _menuFired = false;
            return;
        }

        if (_menuFired) return; // one re-place per hold
        if (_menuHeldSince < 0f) _menuHeldSince = Time.time;
        if (Time.time - _menuHeldSince >= menuHoldSeconds)
        {
            _menuFired = true;
            Request("menu hold", deferIfSession: false);
        }
    }

    /// <summary>MagicLeapController.Instance throws without an InputActionManager in the scene
    /// (same handling as ControllerRayPointer); retried every few seconds.</summary>
    private void TryConnectController()
    {
        _nextConnectAttempt = Time.time + ConnectRetrySeconds;
        try
        {
            _controller = MagicLeapController.Instance;
        }
        catch (NullReferenceException)
        {
            _controller = null;
            if (!_warnedNoController)
            {
                Debug.LogWarning("[WorldPlacement] No MagicLeapController input — Menu-hold recenter off " +
                                 "until one appears.");
                _warnedNoController = true;
            }
        }
    }

    // ── Output ────────────────────────────────────────────────────

    private void Say(string text)
    {
        if (tts != null) tts.Speak(text);
        else Debug.Log($"[WorldPlacement] (no TTS) {text}");
    }

    private void LogPose(string label)
    {
        if (!logPlacement || _rig == null) return;
        Vector3 headLocal = _rig.InverseTransformPoint(_head.position);
        Debug.Log($"[WorldPlacement] {label}: mode {xrOrigin.CurrentTrackingOriginMode}, " +
                  $"eye height {EyeHeight:F2} m, head local {headLocal:F2}, head yaw {_head.eulerAngles.y:F0}, " +
                  $"head pitch {HeadPitch:F0}, rig {_rig.position:F2} yaw {_rig.eulerAngles.y:F0}, " +
                  $"floor plane {(_hasStableFloor ? _stableFloorLocalY.ToString("F2") : "none")}");
    }
}
