using TMPro;
using UnityEngine;

/// <summary>
/// Shows how many shots are left in the current session ("3 shots left", "1 shot left"), from
/// the start of the pre-start countdown until the session ends. The count includes the shot in
/// progress and drops when the ball despawns, between shots, so the change can't warn of a shot.
///
/// Setup: attach to a world-space TextMeshPro label. Launcher and Label are found automatically
/// if left empty. See Plans/Future/Reviewed/Shots_Remaining_Display_Plan.md.
/// </summary>
public class ShotsRemainingDisplay : MonoBehaviour
{
    [Tooltip("The session driver. Found in the scene if empty.")]
    public RandomLauncher launcher;

    [Tooltip("The text to write to. Uses the one on this GameObject if empty.")]
    public TMP_Text label;

    // ── Private ───────────────────────────────────────────────────

    private FloorBoundary _floorBoundary;
    private bool _sessionActive;
    private int _shotsLeft;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (label == null)
            label = GetComponent<TMP_Text>();

        if (launcher != null)
            _floorBoundary = launcher.GetComponent<FloorBoundary>();

        if (label != null)
        {
            label.color = UiPalette.Chalk;
            label.enabled = false;
        }
        else
        {
            Debug.LogWarning("[ShotsRemainingDisplay] No TextMeshPro label found — nothing will be shown.");
        }
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
        else
        {
            Debug.LogWarning("[ShotsRemainingDisplay] No RandomLauncher found — the count will never show.");
        }

        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += HandleDespawned;
        else
            Debug.LogWarning("[ShotsRemainingDisplay] No FloorBoundary on the launcher — the count will never drop.");
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= HandleSessionEnded;
        }

        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;

        HandleSessionEnded();
    }

    // ── Event handlers ────────────────────────────────────────────

    void HandleSessionStarted()
    {
        _sessionActive = true;
        _shotsLeft = launcher.shotsPerSession;
        Show();
    }

    void HandleSessionEnded()
    {
        _sessionActive = false;

        // Hide the label only. Deactivating this GameObject would run OnDisable and unsubscribe
        // from the events, so the count would never come back.
        if (label != null)
            label.enabled = false;
    }

    /// <summary>On the last shot, this and RandomLauncher's session end run off the same event in
    /// either order. If this runs first, the 0 is hidden before the frame draws.</summary>
    void HandleDespawned()
    {
        if (!_sessionActive) return;

        _shotsLeft = Mathf.Max(_shotsLeft - 1, 0);
        Show();
    }

    // ── Display ───────────────────────────────────────────────────

    void Show()
    {
        if (label == null) return;

        label.text = _shotsLeft == 1 ? "1 shot left" : $"{_shotsLeft} shots left";
        label.enabled = true;
    }
}
