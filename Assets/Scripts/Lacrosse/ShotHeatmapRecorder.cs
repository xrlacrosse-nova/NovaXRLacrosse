using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Records where each shot crosses the goal plane during a session (or, for a shot the goalie
/// saved before it got there, where it was headed at the plane) and displays a heatmap once
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

    [Tooltip("TextMeshPro label showing the per-quadrant save/score tally (e.g. 'TL: 2/2 saved · " +
             "TR: 0/1 saved · BL: 3/3 saved · BR: 1/2 saved'). Leave unassigned to disable.")]
    public TextMeshProUGUI quadrantBreakdownText;

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
        _goalDetector.OnSaved += HandleSaved;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted += HandleSessionStarted;
            _launcher.OnSessionEnded += HandleSessionEnded;
        }
    }

    void OnDisable()
    {
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        _goalDetector.OnSaved -= HandleSaved;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted -= HandleSessionStarted;
            _launcher.OnSessionEnded -= HandleSessionEnded;
        }
    }

    // ── Recording ────────────────────────────────────────────────

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        // Read GoalDetector's own scored/missed verdict instead of re-deriving it from
        // position math here — re-deriving it against goalGateHalfSize (the same box
        // used to normalize position) meant any shot the launcher can ever aim at
        // was geometrically guaranteed to be "scored," since RandomLauncher/QuadrantMath
        // never aim outside that box. GoalDetector.GoalScored is already correct and is
        // set before OnPlaneCrossed fires, so it reflects this exact crossing.
        RecordShot(crossingPos, _goalDetector.GoalScored);
    }

    /// <summary>A saved ball never crosses the gate plane, so OnPlaneCrossed doesn't fire for it —
    /// GoalDetector raises OnSaved instead, with where the shot was headed at the gate plane.
    /// The two events are mutually exclusive per shot, so nothing is recorded twice.</summary>
    private void HandleSaved(Vector3 projectedPos)
    {
        RecordShot(projectedPos, scored: false);
    }

    private void RecordShot(Vector3 gatePlanePos, bool scored)
    {
        Vector3 center = _goalDetector.goalGateCenter;
        Vector2 half = _goalDetector.goalGateHalfSize;

        float nx = Mathf.Clamp((gatePlanePos.x - center.x) / half.x, -1f, 1f);
        float ny = Mathf.Clamp((gatePlanePos.y - center.y) / half.y, -1f, 1f);

        _points.Add(new Vector2(nx, ny));
        _scored.Add(scored);

        Debug.Log($"[ShotHeatmapRecorder] Recorded shot #{_points.Count}: raw=({gatePlanePos.x:F3}, {gatePlanePos.y:F3}) " +
                  $"center=({center.x:F3}, {center.y:F3}) half=({half.x:F3}, {half.y:F3}) " +
                  $"normalized=({nx:F3}, {ny:F3}) scored={scored}");
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

        if (quadrantBreakdownText != null)
            quadrantBreakdownText.text = BuildQuadrantBreakdown();

        ClearDotInstances();

        Vector2 boxSize = dotContainer.rect.size;
        Debug.Log($"[ShotHeatmapRecorder] RebuildDisplay: dotContainer size={boxSize}, plotting {_points.Count} points");

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

            Debug.Log($"[ShotHeatmapRecorder] Dot {i}: normalized=({_points[i].x:F3}, {_points[i].y:F3}) " +
                      $"-> anchoredPosition={dot.anchoredPosition} scored={_scored[i]}");

            _dotInstances.Add(dot);
        }
    }

    /// <summary>Buckets each recorded point by quadrant, fresh off the same _points/_scored lists
    /// the dot-plot already uses — no separate running counters to keep in sync or reset.</summary>
    private string BuildQuadrantBreakdown()
    {
        int[] scores = new int[4];
        int[] totals = new int[4];

        for (int i = 0; i < _points.Count; i++)
        {
            int index = (int)QuadrantMath.BucketFromNormalized(_points[i]);
            totals[index]++;
            if (_scored[i]) scores[index]++;
        }

        string Tally(Quadrant q)
        {
            int index = (int)q;
            int saves = totals[index] - scores[index];
            return $"{QuadrantLabel(q)}: {saves}/{totals[index]} saved";
        }

        return $"{Tally(Quadrant.TopLeft)} · {Tally(Quadrant.TopRight)} · " +
               $"{Tally(Quadrant.BottomLeft)} · {Tally(Quadrant.BottomRight)}";
    }

    private static string QuadrantLabel(Quadrant quadrant)
    {
        switch (quadrant)
        {
            case Quadrant.TopLeft: return "TL";
            case Quadrant.TopRight: return "TR";
            case Quadrant.BottomLeft: return "BL";
            default: return "BR";
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
