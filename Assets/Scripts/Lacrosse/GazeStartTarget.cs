using System.Collections.Generic;
using MagicLeap.Android;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Interactions;

/// <summary>
/// START target beside the shooter: staring at it for DwellSeconds starts a session, so the
/// goalie's hands stay on the stick in the ready stance (see Plans/Current/Input_Styles_Plan.md).
///   - While gaze is on it, the ring fills; look away and it drains back.
///   - Full: RandomLauncher.TryStartSession(), and the target hides until the session ends.
///   - Accidental starts (the goalie looks toward the shooter between reps): after a session
///     ends it ignores gaze for RearmDelay, and gaze must enter it fresh after it reappears, so
///     already looking at it doesn't count.
///
/// Gaze source: eye gaze (OpenXR eye-gaze interaction, needs the eye-tracking permission and
/// per-user calibration). Falls back to head gaze, with a warning, when eye tracking isn't
/// available. The Editor has no eye tracker, so it always uses the camera's forward.
///
/// The hit test is geometric (gaze ray within TargetRadius of Gaze Center), so the target needs
/// no collider and can't block the controller ray or the ball. A dim full ring (the track) is
/// built behind Ring Fill at runtime, so there's always something visible to look at, not just
/// blank space where the empty fill will be.
///
/// Setup: put on a "StartTarget" GameObject beside the shooter, facing the goalie. Make the ring
/// and START label children of Visual Root (hidden mid-session; this GameObject stays active to
/// keep listening for session events). Ring Fill is an Image with Image Type = Filled, Radial 360.
/// </summary>
public class GazeStartTarget : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Ring + START label. Shown only while no session is running.")]
    public GameObject visualRoot;

    [Tooltip("Image (Image Type = Filled, Radial 360) whose fill shows dwell progress.")]
    public Image ringFill;

    [Tooltip("Point the gaze has to be on — put it at the ring's center, with START inside the ring. " +
             "Empty = this GameObject's position.")]
    public Transform gazeCenter;

    [Tooltip("Gaze within this distance (meters) of Gaze Center counts as on it. " +
             "~0.5 m (1 m across) at ~10 m.")]
    [Min(0.01f)]
    public float targetRadius = 0.5f;

    [Tooltip("Color of the part of the ring that fills in; replaces Ring Fill's Image color at startup. " +
             "Use something bright: the headset's display is additive, so dark colors look see-through.")]
    public Color fillColor = new Color(0f, 1f, 0f, 1f);

    [Tooltip("Build a dim full ring behind Ring Fill so the target is visible before it fills.")]
    public bool showTrack = true;

    [Tooltip("Color of the track ring behind the fill.")]
    public Color trackColor = new Color(1f, 1f, 1f, 0.25f);

    [Header("Dwell")]
    [Tooltip("Seconds of continuous gaze to start a session.")]
    [Min(0.1f)]
    public float dwellSeconds = 3f;

    [Tooltip("How much faster the ring drains than it fills when gaze leaves the target.")]
    [Min(0.1f)]
    public float drainSpeedMultiplier = 2f;

    [Tooltip("Seconds after a session ends before gaze counts again, so there's no instant restart.")]
    [Min(0f)]
    public float rearmDelay = 2f;

    [Header("Gaze")]
    [Tooltip("Head-gaze camera, and the fallback when eye tracking is unavailable. Leave empty to " +
             "use Camera.main. The eye-gaze pose is in this camera's parent space.")]
    public Camera gazeCamera;

    [Tooltip("Use eye gaze on device. Off = always head gaze.")]
    public bool useEyeGaze = true;

    [Header("References")]
    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    // Device name the OpenXR eye-gaze interaction registers (same as the SDK's GazeTrackingExample).
    private const string EyeGazeDeviceName = "Eye Tracking OpenXR";

    // How long a dropped eye frame (a blink) keeps the last eye ray before falling back to head gaze.
    private const float EyeGazeHoldSeconds = 0.3f;

    // Seconds between searches for the eye-gaze device while it's missing.
    private const float EyeDeviceSearchInterval = 1f;

    // ── Private ───────────────────────────────────────────────────

    private float _progress;
    private float _rearmUntil;
    private bool _needsFreshEntry = true;
    private bool _shown;

    private bool _eyePermissionGranted;
    private bool _warnedNoEyeGaze;
    private InputDevice _eyeDevice;
    private Ray _lastEyeGaze;
    private float _lastEyeGazeTime = float.NegativeInfinity;
    private float _nextEyeDeviceSearch;
    private readonly List<InputDevice> _devices = new List<InputDevice>();

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher == null)
            Debug.LogWarning("[GazeStartTarget] No RandomLauncher found — the START target can't start a session.");

        // Hiding this GameObject (or a parent) would also stop it listening for OnSessionEnded,
        // so the target would never come back.
        if (visualRoot != null && transform.IsChildOf(visualRoot.transform))
        {
            Debug.LogWarning("[GazeStartTarget] Visual Root must be a child of StartTarget, not StartTarget " +
                             "itself or a parent — ignoring it. Move the ring and label under a child.");
            visualRoot = null;
        }

        if (ringFill != null)
            ringFill.color = fillColor;

        if (showTrack && ringFill != null)
            BuildTrack();
    }

    // A copy of Ring Fill, always full and dim, drawn just behind it.
    private void BuildTrack()
    {
        var track = new GameObject("RingTrack", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)track.transform;
        var fillRect = ringFill.rectTransform;
        rect.SetParent(fillRect.parent, false);
        rect.SetSiblingIndex(fillRect.GetSiblingIndex()); // earlier sibling = drawn first (behind)
        rect.anchorMin = fillRect.anchorMin;
        rect.anchorMax = fillRect.anchorMax;
        rect.pivot = fillRect.pivot;
        rect.anchoredPosition3D = fillRect.anchoredPosition3D;
        rect.sizeDelta = fillRect.sizeDelta;
        rect.localRotation = fillRect.localRotation;
        rect.localScale = fillRect.localScale;

        var image = track.GetComponent<Image>();
        image.sprite = ringFill.sprite;
        image.material = ringFill.material;
        image.preserveAspect = ringFill.preserveAspect;
        image.type = Image.Type.Simple;
        image.color = trackColor;
        image.raycastTarget = false;
    }

    private Vector3 GazeCenter => gazeCenter != null ? gazeCenter.position : transform.position;

    void Start()
    {
        if (useEyeGaze && !Application.isEditor)
            Permissions.RequestPermission(Permissions.EyeTracking, OnPermissionGranted, OnPermissionDenied, OnPermissionDenied);

        SetShown(launcher == null || !launcher.IsSessionRunning);
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= HandleSessionEnded;
        }
    }

    void Update()
    {
        if (!_shown) return;

        bool onTarget = TryGetGazeRay(out Ray gaze) && IsOnTarget(gaze);

        // Fresh-entry rule: gaze has to leave the target once after it reappears.
        if (!onTarget)
            _needsFreshEntry = false;

        bool counting = onTarget && !_needsFreshEntry && Time.time >= _rearmUntil;
        float rate = 1f / dwellSeconds;
        _progress = counting
            ? Mathf.Min(1f, _progress + rate * Time.deltaTime)
            : Mathf.Max(0f, _progress - rate * drainSpeedMultiplier * Time.deltaTime);

        if (ringFill != null)
            ringFill.fillAmount = _progress;

        if (_progress >= 1f && launcher != null)
        {
            Debug.Log("[GazeStartTarget] Dwell complete — starting session.");
            // One attempt per full ring: if the start doesn't happen (launcher disabled), the
            // goalie has to dwell again instead of this retrying every frame.
            _progress = 0f;
            launcher.TryStartSession(); // OnSessionStarted hides the target
        }
    }

    // ── Session events ────────────────────────────────────────────

    void HandleSessionStarted() => SetShown(false);

    void HandleSessionEnded()
    {
        _rearmUntil = Time.time + rearmDelay;
        SetShown(true);
    }

    private void SetShown(bool shown)
    {
        _shown = shown;
        _progress = 0f;
        _needsFreshEntry = true;

        if (ringFill != null)
            ringFill.fillAmount = 0f;
        if (visualRoot != null)
            visualRoot.SetActive(shown);
    }

    // ── Gaze ──────────────────────────────────────────────────────

    private bool IsOnTarget(Ray gaze)
    {
        Vector3 toTarget = GazeCenter - gaze.origin;
        float along = Vector3.Dot(toTarget, gaze.direction);
        if (along <= 0f) return false; // behind the viewer

        float offAxis = (toTarget - gaze.direction * along).magnitude;
        return offAxis <= targetRadius;
    }

    private bool TryGetGazeRay(out Ray gaze)
    {
        gaze = default;
        Camera cam = gazeCamera != null ? gazeCamera : Camera.main;
        if (cam == null) return false;

        if (useEyeGaze && _eyePermissionGranted)
        {
            if (TryGetEyeGaze(cam, out gaze))
            {
                _lastEyeGaze = gaze;
                _lastEyeGazeTime = Time.time;
                return true;
            }

            // A dropped frame (e.g. a blink) holds the last eye ray, so it doesn't drain the ring.
            if (_eyeDevice.isValid && Time.time - _lastEyeGazeTime <= EyeGazeHoldSeconds)
            {
                gaze = _lastEyeGaze;
                return true;
            }

            // Eye tracker present but lost the eyes for longer: no gaze at all, rather than head
            // gaze, so a head pointed near START can't start a session while the eyes are elsewhere.
            if (_eyeDevice.isValid)
                return false;

            if (!_warnedNoEyeGaze)
            {
                Debug.LogWarning("[GazeStartTarget] No eye-gaze device (OpenXR Eye Gaze Interaction feature " +
                                 "off?) — using head gaze for START.");
                _warnedNoEyeGaze = true;
            }
        }

        // Head gaze: the Editor, eye tracking off/denied, or no eye-gaze device.
        gaze = new Ray(cam.transform.position, cam.transform.forward);
        return true;
    }

    private bool TryGetEyeGaze(Camera cam, out Ray gaze)
    {
        gaze = default;

        if (!_eyeDevice.isValid)
        {
            // Searching for the device is throttled: without the eye-gaze feature it never appears.
            if (Time.time < _nextEyeDeviceSearch) return false;
            _nextEyeDeviceSearch = Time.time + EyeDeviceSearchInterval;

            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.EyeTracking, _devices);
            _eyeDevice = _devices.Find(device => device.name == EyeGazeDeviceName);
            if (!_eyeDevice.isValid) return false;
        }

        if (!_eyeDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked ||
            !_eyeDevice.TryGetFeatureValue(EyeTrackingUsages.gazePosition, out Vector3 position) ||
            !_eyeDevice.TryGetFeatureValue(EyeTrackingUsages.gazeRotation, out Quaternion rotation))
            return false;

        // The gaze pose is in tracking space, same as the camera's local pose.
        Transform space = cam.transform.parent;
        if (space != null)
        {
            position = space.TransformPoint(position);
            rotation = space.rotation * rotation;
        }

        gaze = new Ray(position, rotation * Vector3.forward);
        return true;
    }

    private void OnPermissionGranted(string permission)
    {
        if (permission == Permissions.EyeTracking)
            _eyePermissionGranted = true;
    }

    private void OnPermissionDenied(string permission)
    {
        if (permission == Permissions.EyeTracking)
            Debug.LogWarning("[GazeStartTarget] Eye-tracking permission denied — using head gaze for START.");
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(GazeCenter, targetRadius);
    }
}
