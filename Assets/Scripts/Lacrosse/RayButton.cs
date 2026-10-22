using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// One clickable button on the settings page, picked with the controller ray
/// (ControllerRayPointer) and the trigger. See Plans/Current/Input_Styles_Plan.md.
///   - Hover: label and background brighten.
///   - Press: the button sinks briefly and fires OnPressed.
///   - Selected (e.g. the current player): Chalk label in brackets. Unselected: Smoke.
/// Never red/green: those colors mean shot outcomes only (UiPalette).
///
/// Setup: put on a UI element (RectTransform) on a world-space Canvas, with a label and
/// optionally a background Image. The BoxCollider is sized to the RectTransform and made a
/// trigger so it can't push the ball around. SettingsPage builds its buttons at runtime; this
/// can also be added by hand.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class RayButton : MonoBehaviour
{
    [Tooltip("Text on the button. Its color follows hover/selected state.")]
    public TMP_Text label;

    [Tooltip("Optional background. Its alpha rises on hover.")]
    public Graphic background;

    [Tooltip("Fired when the button is clicked (trigger while hovered, or mouse click in the Editor).")]
    public UnityEvent onPressed = new UnityEvent();

    [Header("Feel")]
    [Tooltip("How far (meters) the button sinks into the page when clicked.")]
    [Min(0f)]
    public float sinkDepth = 0.01f;

    [Tooltip("How long (seconds) the button stays sunk after a click.")]
    [Min(0f)]
    public float sinkSeconds = 0.12f;

    // Background alpha. ML2 is additive, so a dim background reads as a faint plate.
    private const float IdleBackgroundAlpha = 0.25f;
    private const float HoverBackgroundAlpha = 0.6f;

    // ── Private ───────────────────────────────────────────────────

    private BoxCollider _collider;
    private string _text = "";
    private bool _hovered;
    private bool _selected;
    private Vector3 _restLocalPosition;
    private Coroutine _sink;

    public bool IsHovered => _hovered;
    public bool IsSelected => _selected;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _collider = GetComponent<BoxCollider>();
        _collider.isTrigger = true;

        if (label != null)
            _text = label.text;

        FitColliderToRect();
        Refresh();
    }

    void OnDisable()
    {
        // A page hidden mid-sink comes back flat and un-hovered.
        if (_sink != null)
        {
            StopCoroutine(_sink);
            _sink = null;
            transform.localPosition = _restLocalPosition;
        }
        _hovered = false;
        Refresh();
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Assigns visuals after AddComponent, for buttons built at runtime.</summary>
    public void Setup(TMP_Text buttonLabel, Graphic buttonBackground)
    {
        label = buttonLabel;
        background = buttonBackground;
        _text = label != null ? label.text : "";
        FitColliderToRect();
        Refresh();
    }

    /// <summary>The button's text, without selection brackets.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            Refresh();
        }
    }

    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        Refresh();
    }

    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        Refresh();
    }

    /// <summary>Clicks the button: sink, then fire OnPressed.</summary>
    public void Press()
    {
        if (!isActiveAndEnabled) return;

        if (_sink != null)
        {
            StopCoroutine(_sink);
            transform.localPosition = _restLocalPosition;
        }
        // Captured per press, so a button moved after Awake (e.g. a scrolled row) sinks from
        // where it is now.
        _restLocalPosition = transform.localPosition;
        _sink = StartCoroutine(Sink());

        onPressed.Invoke();
    }

    // ── Visuals ───────────────────────────────────────────────────

    private void Refresh()
    {
        if (label != null)
        {
            label.text = _selected ? $"[ {_text} ]" : _text;
            label.color = _selected || _hovered ? UiPalette.Chalk : UiPalette.Smoke;
        }

        if (background != null)
        {
            Color c = UiPalette.Smoke;
            c.a = _hovered ? HoverBackgroundAlpha : IdleBackgroundAlpha;
            background.color = c;
        }
    }

    private IEnumerator Sink()
    {
        // A world-space canvas faces the viewer with its forward pointing away, so +forward is
        // "into the page".
        transform.position += transform.forward * sinkDepth;
        yield return new WaitForSeconds(sinkSeconds);
        transform.localPosition = _restLocalPosition;
        _sink = null;
    }

    /// <summary>Sizes the collider to the RectTransform, so runtime-built buttons of any size
    /// are hit exactly where they're drawn.</summary>
    private void FitColliderToRect()
    {
        if (_collider == null || !(transform is RectTransform rect)) return;

        Vector2 size = rect.rect.size;
        // Thin slab; depth in local units, so ~1 cm at a typical 0.001 canvas scale.
        _collider.size = new Vector3(size.x, size.y, 10f);
        _collider.center = new Vector3((0.5f - rect.pivot.x) * size.x, (0.5f - rect.pivot.y) * size.y, 0f);
    }
}
