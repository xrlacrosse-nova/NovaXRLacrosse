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
///
/// Readout (Plans/Past/UI_Design_Plan.md; copy per Plans/Current/Field_Markings_Plan.md): dotContainer is drawn as the goal mouth —
/// an optional runtime-built cage frame, each corner's name and save count inside that corner,
/// and a "6 of 8 saved" headline. Saves are dots, goals use goalMarkPrefab (e.g. an ×) so the
/// two read apart by shape as well as color.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class ShotHeatmapRecorder : MonoBehaviour
{
    public enum GoalieHand { Right, Left }

    [Header("Visualization")]
    [Tooltip("Color for shots that scored (bad outcome — the goalie let it in).")]
    public Color scoreColor = UiPalette.Goal;

    [Tooltip("Color for shots that were saved (good outcome — the goalie kept it out).")]
    public Color saveColor = UiPalette.Save;

    [Tooltip("Diameter (in UI units) of each plotted shot dot.")]
    [Range(4f, 200f)]
    public float dotSize = 26f;

    [Tooltip("The goalie's stick hand. Sets which corners are called 'Stick' vs 'Off-stick': " +
             "for a right-handed goalie, stick side is the goalie's right.")]
    public GoalieHand goalieHand = GoalieHand.Right;

    [Header("UI (World Space Canvas)")]
    [Tooltip("Root panel toggled active/inactive to show or hide the whole heatmap. Leave unassigned to disable.")]
    public GameObject heatmapPanel;

    [Tooltip("Headline under the cage, e.g. '6 of 8 saved'. Leave unassigned to fall back to " +
             "quadrantBreakdownText.")]
    public TextMeshProUGUI headlineText;

    [Tooltip("One label per corner, in Quadrant order: TopLeft, TopRight, BottomLeft, BottomRight. " +
             "Each gets its corner's name and count, e.g. 'Stick high\\n0 of 1'. Text alignment is " +
             "set by the script so each label hugs its own outer corner.")]
    public TextMeshProUGUI[] cornerLabels = new TextMeshProUGUI[4];

    [Tooltip("Fallback only: used when headlineText is unassigned. Gets the headline plus all four " +
             "corner counts as plain lines. Leave unassigned to disable.")]
    public TextMeshProUGUI quadrantBreakdownText;

    [Tooltip("Prefab instantiated for each saved shot — a small UI Image (e.g. a circle sprite).")]
    public RectTransform dotPrefab;

    [Tooltip("Optional prefab for shots that scored, e.g. a TMP text holding '×'. Its Image or " +
             "TMP text is tinted scoreColor. Leave unassigned to use dotPrefab for goals too.")]
    public RectTransform goalMarkPrefab;

    [Tooltip("Defines the heatmap box: dots are instantiated as children of this RectTransform and " +
             "positioned within its bounds. Its pivot must be (0.5, 0.5) — center — so normalized " +
             "shot coordinates map directly onto anchoredPosition.")]
    public RectTransform dotContainer;

    [Header("Cage Frame")]
    [Tooltip("Build a goal frame (posts, crossbar, bottom bar) and a quadrant crosshair around " +
             "dotContainer at runtime. Turn off if the panel already has hand-placed grid lines.")]
    public bool drawCageFrame = false;

    [Tooltip("Thickness (UI units) of the posts and crossbar, drawn just outside the box.")]
    [Min(0f)]
    public float postThickness = 45f;

    [Tooltip("Thickness (UI units) of the bottom bar, drawn just outside the box.")]
    [Min(0f)]
    public float bottomBarThickness = 16f;

    [Tooltip("Thickness (UI units) of the dim crosshair splitting the box into quadrants.")]
    [Min(0f)]
    public float crosshairThickness = 10f;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;

    private readonly List<Vector2> _points = new List<Vector2>();
    private readonly List<bool> _scored = new List<bool>();
    private readonly List<RectTransform> _dotInstances = new List<RectTransform>();
    private bool _visible = false;
    private GameObject _cageFrame;

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

        Vector2 normalized = QuadrantMath.NormalizeToGate(center, half, gatePlanePos);
        float nx = normalized.x;
        float ny = normalized.y;

        _points.Add(normalized);
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

        UpdateCageFrame();
        UpdateLabels();

        ClearDotInstances();

        Vector2 boxSize = dotContainer.rect.size;
        Debug.Log($"[ShotHeatmapRecorder] RebuildDisplay: dotContainer size={boxSize}, plotting {_points.Count} points");

        for (int i = 0; i < _points.Count; i++)
        {
            RectTransform prefab = _scored[i] && goalMarkPrefab != null ? goalMarkPrefab : dotPrefab;

            // worldPositionStays: false — dotContainer sits under a heavily-downscaled World
            // Space canvas, so the default world-position-preserving Instantiate would blow up
            // each dot's localScale to compensate, making dots render enormous and overlap into
            // one blob near the center instead of appearing as small, correctly-spread markers.
            RectTransform dot = Instantiate(prefab, dotContainer, false);
            dot.gameObject.SetActive(true);
            dot.localScale = Vector3.one;
            dot.sizeDelta = Vector2.one * dotSize;

            // Normalized point is [-1, 1] with +y = up; anchoredPosition on a center-pivot
            // RectTransform uses the same convention, so no axis flip is needed here.
            dot.anchoredPosition = new Vector2(
                _points[i].x * 0.5f * boxSize.x,
                _points[i].y * 0.5f * boxSize.y);

            // Graphic covers both a UI Image (dot) and a TMP text (× goal mark).
            Graphic graphic = dot.GetComponent<Graphic>();
            if (graphic != null)
                graphic.color = _scored[i] ? scoreColor : saveColor;

            Debug.Log($"[ShotHeatmapRecorder] Dot {i}: normalized=({_points[i].x:F3}, {_points[i].y:F3}) " +
                      $"-> anchoredPosition={dot.anchoredPosition} scored={_scored[i]}");

            _dotInstances.Add(dot);
        }
    }

    /// <summary>Buckets each recorded point by quadrant, fresh off the same _points/_scored lists
    /// the dot-plot already uses — no separate running counters to keep in sync or reset — and
    /// writes the headline and corner labels (or the single fallback label).</summary>
    private void UpdateLabels()
    {
        int[] saves = new int[4];
        int[] totals = new int[4];
        int totalSaves = 0;

        for (int i = 0; i < _points.Count; i++)
        {
            int index = (int)QuadrantMath.BucketFromNormalized(_points[i]);
            totals[index]++;
            if (!_scored[i])
            {
                saves[index]++;
                totalSaves++;
            }
        }

        string headline = $"{totalSaves} of {_points.Count} saved";

        string Count(Quadrant q)
        {
            int index = (int)q;
            return totals[index] == 0 ? "No shots" : $"{saves[index]} of {totals[index]}";
        }

        if (headlineText != null)
            headlineText.text = headline;

        if (cornerLabels != null)
        {
            for (int i = 0; i < cornerLabels.Length && i < 4; i++)
            {
                if (cornerLabels[i] == null) continue;

                Quadrant q = (Quadrant)i;
                cornerLabels[i].text = $"{CornerName(q)}\n{Count(q)}";
                cornerLabels[i].alignment = CornerAlignment(q);
                cornerLabels[i].color = UiPalette.Chalk;
            }
        }

        if (headlineText == null && quadrantBreakdownText != null)
        {
            quadrantBreakdownText.text =
                $"{headline}\n" +
                $"{CornerName(Quadrant.TopLeft)} {Count(Quadrant.TopLeft)}, " +
                $"{CornerName(Quadrant.TopRight)} {Count(Quadrant.TopRight)}\n" +
                $"{CornerName(Quadrant.BottomLeft)} {Count(Quadrant.BottomLeft)}, " +
                $"{CornerName(Quadrant.BottomRight)} {Count(Quadrant.BottomRight)}";
        }
    }

    /// <summary>Goalie terms for a corner. Quadrant Left is -X, the goalie's own left (the
    /// goalie faces +Z); for a right-handed goalie the stick side is the right.</summary>
    private string CornerName(Quadrant quadrant)
    {
        bool left = quadrant == Quadrant.TopLeft || quadrant == Quadrant.BottomLeft;
        bool top = quadrant == Quadrant.TopLeft || quadrant == Quadrant.TopRight;
        bool stickSide = goalieHand == GoalieHand.Right ? !left : left;

        return (stickSide ? "Stick" : "Off-stick") + (top ? " high" : " low");
    }

    private static TextAlignmentOptions CornerAlignment(Quadrant quadrant)
    {
        switch (quadrant)
        {
            case Quadrant.TopLeft: return TextAlignmentOptions.TopLeft;
            case Quadrant.TopRight: return TextAlignmentOptions.TopRight;
            case Quadrant.BottomLeft: return TextAlignmentOptions.BottomLeft;
            default: return TextAlignmentOptions.BottomRight;
        }
    }

    // ── Cage frame ───────────────────────────────────────────────

    /// <summary>Builds the frame once, the first time it's needed, as children of dotContainer
    /// stretched to its rect — so it follows the box's size with nothing hand-placed. Bars sit
    /// just outside the box so they never cover a dot on the edge.</summary>
    private void UpdateCageFrame()
    {
        if (!drawCageFrame)
        {
            if (_cageFrame != null)
                _cageFrame.SetActive(false);
            return;
        }

        if (_cageFrame == null)
        {
            RectTransform root = CreateRect("CageFrame", dotContainer, Vector2.zero, Vector2.one,
                                            Vector2.zero, Vector2.zero);
            // First sibling, so dots and labels draw on top of it.
            root.SetAsFirstSibling();
            _cageFrame = root.gameObject;

            float p = postThickness;
            float b = bottomBarThickness;
            float c = crosshairThickness * 0.5f;

            // Crosshair first, so the frame bars draw over its ends.
            CreateBar("CrosshairVertical", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                      new Vector2(-c, 0f), new Vector2(c, 0f), UiPalette.Smoke);
            CreateBar("CrosshairHorizontal", root, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                      new Vector2(0f, -c), new Vector2(0f, c), UiPalette.Smoke);

            CreateBar("LeftPost", root, new Vector2(0f, 0f), new Vector2(0f, 1f),
                      new Vector2(-p, -b), new Vector2(0f, p), UiPalette.Chalk);
            CreateBar("RightPost", root, new Vector2(1f, 0f), new Vector2(1f, 1f),
                      new Vector2(0f, -b), new Vector2(p, p), UiPalette.Chalk);
            CreateBar("Crossbar", root, new Vector2(0f, 1f), new Vector2(1f, 1f),
                      new Vector2(0f, 0f), new Vector2(0f, p), UiPalette.Chalk);
            CreateBar("BottomBar", root, new Vector2(0f, 0f), new Vector2(1f, 0f),
                      new Vector2(0f, -b), new Vector2(0f, 0f), UiPalette.Chalk);
        }

        _cageFrame.SetActive(true);
    }

    private static RectTransform CreateRect(string name, Transform parent, Vector2 anchorMin,
                                            Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        return rect;
    }

    private static void CreateBar(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                                  Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        RectTransform rect = CreateRect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private void ClearDotInstances()
    {
        for (int i = 0; i < _dotInstances.Count; i++)
            if (_dotInstances[i] != null)
                Destroy(_dotInstances[i].gameObject);

        _dotInstances.Clear();
    }
}
