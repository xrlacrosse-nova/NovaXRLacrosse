using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Records where each shot crosses the goal plane during a session and displays a heatmap once
/// the session ends. Attach to the same GameObject as GoalDetector (and RandomLauncher, if present)
/// — the ball.
///
/// Display window: the heatmap only appears after a full session finishes (RandomLauncher.OnSessionEnded)
/// and is cleared/hidden the moment the next session starts (RandomLauncher.OnSessionStarted). It never
/// shows during active play. If no RandomLauncher is present, points are still recorded but the
/// heatmap is only ever shown/cleared manually via ShowHeatmap()/ClearRecordedShots().
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class ShotHeatmapRecorder : MonoBehaviour
{
    [Header("Visualization")]
    [Tooltip("Color for shots that scored (bad outcome — the goalie let it in).")]
    public Color scoreColor = Color.red;

    [Tooltip("Color for shots that were saved (good outcome — the goalie kept it out).")]
    public Color saveColor = Color.green;

    [Tooltip("Diameter (in UI units) of each plotted shot dot.")]
    [Range(4f, 60f)]
    public float dotSize = 26f;

    [Header("UI (World Space Canvas)")]
    [Tooltip("Root panel toggled active/inactive to show or hide the whole heatmap. Leave unassigned to disable.")]
    public GameObject heatmapPanel;

    [Tooltip("TextMeshPro label showing 'SHOT HEATMAP — X/Y saved'.")]
    public TextMeshProUGUI heatmapTitle;

    [Tooltip("Prefab instantiated for each recorded shot — a small UI Image (e.g. a circle sprite).")]
    public RectTransform dotPrefab;

    [Tooltip("Defines the heatmap box: dots are instantiated as children of this RectTransform and " +
             "positioned within its bounds. Its pivot must be (0.5, 0.5) — center — so normalized " +
             "shot coordinates map directly onto anchoredPosition.")]
    public RectTransform dotContainer;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;

    private readonly List<Vector2> _points = new List<Vector2>();
    private readonly List<bool> _scored = new List<bool>();
    private readonly List<RectTransform> _dotInstances = new List<RectTransform>();
    private bool _visible = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
        _launcher = GetComponent<RandomLauncher>();

        if (heatmapPanel != null)
            heatmapPanel.SetActive(false);
    }

    void OnEnable()
    {
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted += HandleSessionStarted;
            _launcher.OnSessionEnded += HandleSessionEnded;
        }
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted -= HandleSessionStarted;
            _launcher.OnSessionEnded -= HandleSessionEnded;
        }
    }

    // ── Recording ────────────────────────────────────────────────

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        Vector3 center = _goalDetector.goalGateCenter;
        Vector2 half = _goalDetector.goalGateHalfSize;

        float nx = Mathf.Clamp((crossingPos.x - center.x) / half.x, -1f, 1f);
        float ny = Mathf.Clamp((crossingPos.y - center.y) / half.y, -1f, 1f);

        bool scored = Mathf.Abs(crossingPos.x - center.x) <= half.x
                   && Mathf.Abs(crossingPos.y - center.y) <= half.y;

        _points.Add(new Vector2(nx, ny));
        _scored.Add(scored);
    }

    private void HandleSessionStarted()
    {
        ClearRecordedShots();
    }

    private void HandleSessionEnded()
    {
        ShowHeatmap();
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Clears all recorded shots and hides the heatmap.</summary>
    public void ClearRecordedShots()
    {
        _points.Clear();
        _scored.Clear();
        _visible = false;

        ClearDotInstances();

        if (heatmapPanel != null)
            heatmapPanel.SetActive(false);
    }

    /// <summary>Shows the heatmap for whatever shots have been recorded so far.</summary>
    public void ShowHeatmap()
    {
        _visible = true;
        RebuildDisplay();
    }

    // ── Display ──────────────────────────────────────────────────

    private void RebuildDisplay()
    {
        if (!_visible || _points.Count == 0 || dotContainer == null || dotPrefab == null)
            return;

        if (heatmapPanel != null)
            heatmapPanel.SetActive(true);

        if (heatmapTitle != null)
        {
            int scores = 0;
            for (int i = 0; i < _scored.Count; i++)
                if (_scored[i]) scores++;
            int saves = _points.Count - scores;

            heatmapTitle.text = $"SHOT HEATMAP — {saves}/{_points.Count} saved";
        }

        ClearDotInstances();

        Vector2 boxSize = dotContainer.rect.size;

        for (int i = 0; i < _points.Count; i++)
        {
            // worldPositionStays: false — dotContainer sits under a heavily-downscaled World
            // Space canvas, so the default world-position-preserving Instantiate would blow up
            // each dot's localScale to compensate, making dots render enormous and overlap into
            // one blob near the center instead of appearing as small, correctly-spread markers.
            RectTransform dot = Instantiate(dotPrefab, dotContainer, false);
            dot.gameObject.SetActive(true);
            dot.localScale = Vector3.one;
            dot.sizeDelta = Vector2.one * dotSize;

            // Normalized point is [-1, 1] with +y = up; anchoredPosition on a center-pivot
            // RectTransform uses the same convention, so no axis flip is needed here.
            dot.anchoredPosition = new Vector2(
                _points[i].x * 0.5f * boxSize.x,
                _points[i].y * 0.5f * boxSize.y);

            Image img = dot.GetComponent<Image>();
            if (img != null)
                img.color = _scored[i] ? scoreColor : saveColor;

            _dotInstances.Add(dot);
        }
    }

    private void ClearDotInstances()
    {
        for (int i = 0; i < _dotInstances.Count; i++)
            if (_dotInstances[i] != null)
                Destroy(_dotInstances[i].gameObject);

        _dotInstances.Clear();
    }
}
