using UnityEngine;
using UnityEngine.InputSystem;
using MagicLeap.Examples;

/// <summary>
/// Controller ray for the settings page (see Plans/Current/Input_Styles_Plan.md). Casts a ray
/// from the controller's pointer pose, highlights the RayButton it points at, and clicks it on
/// the trigger with a light haptic tick. A trigger pull with nothing hovered does nothing.
///
/// The laser and hit dot only show while the ray is on a button or the page (SettingsPage's
/// board collider), so they don't clutter the view while the goalie watches the shooter. The
/// page is hidden mid-session, so nothing can be clicked then.
///
/// Editor only: clicking a button in the Game view with the mouse works like the trigger, so
/// the page can be tested without a headset.
///
/// Setup: attach to the ML Rig and optionally assign a laser material. Requires an
/// InputActionManager in the scene for MagicLeapController; without one it logs a warning once
/// and only the Editor mouse works.
/// </summary>
public class ControllerRayPointer : MonoBehaviour
{
    [Header("Ray")]
    [Tooltip("How far (meters) the ray reaches. The page is ~1.5 m away.")]
    [Min(0.1f)]
    public float maxDistance = 5f;

    [Tooltip("The space the controller pose is reported in (the rig's Camera Offset). Leave " +
             "empty to use the main camera's parent.")]
    public Transform trackingSpace;

    [Header("Laser")]
    [Tooltip("Optional material for the laser and dot. Leave empty to use Sprites/Default.")]
    public Material laserMaterial;

    [Tooltip("Laser width (meters).")]
    [Min(0f)]
    public float laserWidth = 0.003f;

    [Tooltip("Hit dot diameter (meters).")]
    [Min(0f)]
    public float dotSize = 0.015f;

    [Header("Click Haptic")]
    [Tooltip("Vibration strength of the click tick, from 0 to 1.")]
    [Range(0f, 1f)]
    public float clickAmplitude = 0.15f;

    [Tooltip("Vibration length (seconds) of the click tick.")]
    [Min(0f)]
    public float clickDuration = 0.03f;

    private const string FallbackShader = "Sprites/Default";

    // Seconds between attempts to reach MagicLeapController while it's unavailable.
    private const float ConnectRetrySeconds = 1f;

    // ── Private ───────────────────────────────────────────────────

    private MagicLeapController _controller;
    private bool _warnedUnavailable;
    private float _nextConnectAttempt;

    private LineRenderer _laser;
    private Transform _dot;
    private Material _material;
    private bool _ownsMaterial;
    private Material _dotMaterial;

    // Roomy: the 5 m ray can pass through the goal, avatar, stick and trigger zones too, and
    // NonAlloc drops hits beyond the buffer in no particular order.
    private readonly RaycastHit[] _hits = new RaycastHit[64];
    private RayButton _hovered;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (laserMaterial != null)
        {
            _material = laserMaterial;
        }
        else
        {
            Shader shader = Shader.Find(FallbackShader);
            if (shader == null)
                Debug.LogWarning($"[ControllerRayPointer] Shader \"{FallbackShader}\" not found — " +
                                 "assign a Laser Material, or the laser won't render correctly.");
            else
            {
                _material = new Material(shader);
                _ownsMaterial = true;
            }
        }

        _laser = new GameObject("RayLaser").AddComponent<LineRenderer>();
        _laser.transform.SetParent(transform, false);
        _laser.useWorldSpace = true;
        _laser.positionCount = 2;
        _laser.startWidth = laserWidth;
        _laser.endWidth = laserWidth;
        _laser.startColor = UiPalette.Smoke;
        _laser.endColor = UiPalette.Chalk;
        _laser.sharedMaterial = _material;
        _laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _laser.receiveShadows = false;

        GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "RayDot";
        Destroy(dot.GetComponent<Collider>()); // the ray must never hit its own dot
        dot.transform.SetParent(transform, false);
        dot.transform.localScale = Vector3.one * dotSize;
        Renderer dotRenderer = dot.GetComponent<Renderer>();
        dotRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (_material != null)
        {
            _dotMaterial = new Material(_material) { color = UiPalette.Chalk };
            dotRenderer.sharedMaterial = _dotMaterial;
        }
        _dot = dot.transform;

        ShowLaser(false);
    }

    void OnEnable()
    {
        TryConnectController();
    }

    /// <summary>
    /// MagicLeapController.Instance throws if there's no InputActionManager in the scene yet (or
    /// at all, e.g. Editor testing without the ML rig). Retried from Update until it connects,
    /// so a rig that comes up after this component still gets a working ray.
    /// </summary>
    private void TryConnectController()
    {
        _nextConnectAttempt = Time.time + ConnectRetrySeconds;
        try
        {
            _controller = MagicLeapController.Instance;
            _controller.TriggerPressed += HandleTriggerPressed;
        }
        catch (System.NullReferenceException)
        {
            _controller = null;
            if (!_warnedUnavailable)
            {
                Debug.LogWarning("[ControllerRayPointer] No MagicLeapController input available (no " +
                                 "InputActionManager in scene) — controller ray disabled until one appears" +
#if UNITY_EDITOR
                                 "; mouse clicks in the Game view still work" +
#endif
                                 ".");
                _warnedUnavailable = true;
            }
        }
    }

    void OnDisable()
    {
        if (_controller != null)
        {
            _controller.TriggerPressed -= HandleTriggerPressed;
            _controller = null;
        }

        SetHovered(null);
        ShowLaser(false);
    }

    void OnDestroy()
    {
        if (_ownsMaterial && _material != null)
            Destroy(_material);
        if (_dotMaterial != null)
            Destroy(_dotMaterial);
    }

    void Update()
    {
        if (_controller == null && Time.time >= _nextConnectAttempt)
            TryConnectController();

        RayButton hovered = null;
        bool laserShown = false;

        if (_controller != null && _controller.IsTracked)
        {
            Transform space = TrackingSpace();
            Vector3 origin = space != null ? space.TransformPoint(_controller.PointerPosition) : _controller.PointerPosition;
            Quaternion rotation = space != null ? space.rotation * _controller.PointerRotation : _controller.PointerRotation;
            Ray ray = new Ray(origin, rotation * Vector3.forward);

            if (CastAtPage(ray, out hovered, out Vector3 hitPoint))
            {
                _laser.SetPosition(0, origin);
                _laser.SetPosition(1, hitPoint);
                _dot.position = hitPoint;
                laserShown = true;
            }
        }

#if UNITY_EDITOR
        // Editor-only: the mouse in the Game view stands in for the controller ray.
        if (hovered == null && Mouse.current != null && Camera.main != null)
        {
            Ray mouseRay = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
            CastAtPage(mouseRay, out hovered, out _);
            if (hovered != null && Mouse.current.leftButton.wasPressedThisFrame)
                Click(hovered);
        }
#endif

        ShowLaser(laserShown);
        SetHovered(hovered);
    }

    // ── Ray ───────────────────────────────────────────────────────

    /// <summary>
    /// Casts <paramref name="ray"/> at the settings page. Returns true if it hits a RayButton
    /// or the page's board, with the nearest button (if any) in <paramref name="button"/>.
    /// Buttons win over the board behind them; anything else the ray passes through is ignored,
    /// so the page can't be blocked.
    /// </summary>
    private bool CastAtPage(Ray ray, out RayButton button, out Vector3 hitPoint)
    {
        button = null;
        hitPoint = Vector3.zero;

        int count = Physics.RaycastNonAlloc(ray, _hits, maxDistance, Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Collide);

        float nearestButton = float.MaxValue;
        float nearestPage = float.MaxValue;
        Vector3 pagePoint = Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            RayButton hitButton = hit.collider.GetComponent<RayButton>();
            if (hitButton != null)
            {
                if (hit.distance < nearestButton)
                {
                    nearestButton = hit.distance;
                    button = hitButton;
                    hitPoint = hit.point;
                }
            }
            else if (hit.distance < nearestPage && hit.collider.GetComponentInParent<SettingsPage>() != null)
            {
                nearestPage = hit.distance;
                pagePoint = hit.point;
            }
        }

        if (button != null) return true;
        if (nearestPage < float.MaxValue)
        {
            hitPoint = pagePoint;
            return true;
        }
        return false;
    }

    private Transform TrackingSpace()
    {
        if (trackingSpace != null) return trackingSpace;
        Camera cam = Camera.main;
        return cam != null ? cam.transform.parent : null;
    }

    // ── Click ─────────────────────────────────────────────────────

    private void HandleTriggerPressed(InputAction.CallbackContext ctx)
    {
        if (_hovered != null)
            Click(_hovered);
    }

    private void Click(RayButton button)
    {
        button.Press();

        if (_controller != null)
            _controller.SendHapticImpulse(clickAmplitude, clickDuration);
    }

    // ── Visuals ───────────────────────────────────────────────────

    private void SetHovered(RayButton button)
    {
        if (_hovered == button) return;

        if (_hovered != null)
            _hovered.SetHovered(false);
        _hovered = button;
        if (_hovered != null)
            _hovered.SetHovered(true);
    }

    private void ShowLaser(bool shown)
    {
        if (_laser != null && _laser.enabled != shown)
            _laser.enabled = shown;
        if (_dot != null && _dot.gameObject.activeSelf != shown)
            _dot.gameObject.SetActive(shown);
    }
}
