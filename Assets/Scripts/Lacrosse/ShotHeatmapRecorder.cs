using System;
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
/// Readout (Plans/Past/UI_Design_Plan.md; copy per Plans/Past/Field_Markings_Plan.md): dotContainer is drawn as the goal mouth —
/// an optional runtime-built cage frame, each corner's name and save count inside that corner,
/// and a "6 of 8 saved" headline. Saves are dots, goals use goalMarkPrefab (e.g. an ×) so the
/// two read apart by shape as well as color.
///
/// Gradient (Plans/Current/Heatmap_Gradient_Plan.md): a runtime-built texture under the dots.
/// Brightness = how many shots came to an area, color = the save rate there
/// (UiPalette.SaveRateRamp). ThisSession uses the session that just ended; AllSessions uses every
/// session kept this run (or loaded with LoadHistory), older ones fainter by recencyDecay, and
/// adds an "All sessions" line under the headline. ShowLifetime draws a given set of shots with
/// every session weighted equally and no dots; ShowLive returns to the live readout.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class ShotHeatmapRecorder : MonoBehaviour
{
    public enum GradientMode { Off, ThisSession, AllSessions }

    [Header("Visualization")]
    [Tooltip("Color for shots that scored (bad outcome — the goalie let it in).")]
    public Color scoreColor = UiPalette.Goal;

    [Tooltip("Color for shots that were saved (good outcome — the goalie kept it out).")]
    public Color saveColor = UiPalette.Save;

    [Tooltip("Diameter (in UI units) of each plotted shot dot.")]
    [Range(4f, 200f)]
    public float dotSize = 26f;

    [Tooltip("Draw the per-shot dots and × marks (live readout only; the Lifetime view never has dots).")]
    public bool showDots = true;

    [Tooltip("Orange (goals) and blue (saves) instead of red and green, for the gradient, legend, " +
             "dots and × marks. Overrides scoreColor/saveColor while on.")]
    public bool colorblindSafe = false;

    [Header("Gradient")]
    [Tooltip("Off: dots only. This Session: gradient of the session that just ended. All Sessions: " +
             "every session kept this run (or loaded with LoadHistory), older sessions fainter.")]
    public GradientMode gradientMode = GradientMode.ThisSession;

    [Tooltip("Blur radius (σ) in normalized gate units, where the gate spans -1 to 1. Smaller = " +
             "sharper spots, bigger = smoother.")]
    [Range(0.05f, 0.5f)]
    public float gradientRadius = 0.18f;

    [Tooltip("Brightness curve. Below 1 lifts faint areas; 1 = brightness straight from shot count.")]
    [Range(0.2f, 1f)]
    public float gradientGamma = 0.6f;

    [Tooltip("All Sessions only: weight = recencyDecay ^ sessions ago, so older sessions fade. " +
             "1 = every session counts equally.")]
    [Range(0.5f, 1f)]
    public float recencyDecay = 0.85f;

    [Tooltip("Gradient texture size, in pixels per side.")]
    [Range(32, 256)]
    public int gradientResolution = 96;

    [Tooltip("All Sessions only: the gradient uses at most this many of the newest shots. The " +
             "'All sessions' totals still count every shot.")]
    [Min(1)]
    public int maxGradientShots = 500;

    [Header("Gradient Legend")]
    [Tooltip("Thin Goals → Saves color strip under the cage, built at runtime.")]
    public bool drawGradientLegend = false;

    [Tooltip("Gap (UI units) between the bottom of the box (or the bottom bar) and the strip. " +
             "Raise it if the strip overlaps the headline.")]
    [Min(0f)]
    public float legendGap = 30f;

    [Tooltip("Height (UI units) of the color strip.")]
    [Min(1f)]
    public float legendHeight = 18f;

    [Tooltip("Font size of the 'Goals' / 'Saves' labels under the strip.")]
    [Min(1f)]
    public float legendFontSize = 36f;

    [Header("UI (World Space Canvas)")]
    [Tooltip("Root panel toggled active/inactive to show or hide the whole heatmap. Leave unassigned to disable.")]
    public GameObject heatmapPanel;

    [Tooltip("Headline under the cage, e.g. '6 of 8 saved'. Leave unassigned to fall back to " +
             "quadrantBreakdownText.")]
    public TextMeshProUGUI headlineText;

    [Tooltip("One label per corner, in Quadrant order: TopLeft, TopRight, BottomLeft, BottomRight. " +
             "Each gets its corner's name and count, e.g. 'Top left\\n0 of 1'. Text alignment is " +
             "set by the script so each label hugs its own outer corner.")]
    public TextMeshProUGUI[] cornerLabels = new TextMeshProUGUI[4];

    [Tooltip("Fallback only: used when headlineText is unassigned. Gets the headline plus all four " +
             "corners, each as position name and tally, e.g. 'Top right: 0/1'. Leave unassigned " +
             "to disable.")]
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

    private const int LegendResolution = 64;

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;
    private DifficultySelector _difficulty; // optional: names the level under the headline

    // The current (or just-ended) session: dots, corner labels, headline.
    private readonly List<RecordedShot> _shots = new List<RecordedShot>();
    // Finished sessions, oldest first, for the All Sessions gradient.
    private readonly List<RecordedShot> _history = new List<RecordedShot>();
    private readonly List<RecordedShot> _scratch = new List<RecordedShot>();
    private List<RecordedShot> _lifetimeShots; // non-null while the Lifetime view is shown

    private int _nextSession = 0;    // session index stamped on the shots being recorded now
    private bool _committed = false; // _shots already added to _history

    private readonly List<RectTransform> _dotInstances = new List<RectTransform>();
    private bool _visible = false;
    private GameObject _cageFrame;

    private RawImage _gradientImage;
    private Texture2D _gradientTexture;
    private float[] _density;
    private float[] _goalSum;
    private Color32[] _pixels;
    private float[] _columnWeights; // one shot's Gaussian per texture column / row
    private float[] _rowWeights;

    private GameObject _legend;
    private RectTransform _legendStrip;
    private Texture2D _legendTexture;
    private TextMeshProUGUI _legendGoals;
    private TextMeshProUGUI _legendSaves;
    private bool? _legendRampColorblind; // colorblindSafe the legend texture was last filled for

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
        _launcher = GetComponent<RandomLauncher>();
        _difficulty = FindFirstObjectByType<DifficultySelector>();

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

    void OnDestroy()
    {
        if (_gradientTexture != null)
            Destroy(_gradientTexture);
        if (_legendTexture != null)
            Destroy(_legendTexture);
    }

    // ── Recording ────────────────────────────────────────────────

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        // GoalDetector's own verdict, set before OnPlaneCrossed fires. Every shot is aimed
        // inside the cage, so a crossing outside it is a bug rather than a miss: warn and
        // leave it off the readout.
        if (!_goalDetector.GoalScored)
        {
            Debug.LogWarning($"[ShotHeatmapRecorder] Shot crossed the goal plane outside the gate at " +
                             $"({crossingPos.x:F3}, {crossingPos.y:F3}); not recorded.");
            return;
        }

        RecordShot(crossingPos, scored: true);
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

        _shots.Add(new RecordedShot(normalized, scored, _nextSession));

        Debug.Log($"[ShotHeatmapRecorder] Recorded shot #{_shots.Count}: raw=({gatePlanePos.x:F3}, {gatePlanePos.y:F3}) " +
                  $"center=({center.x:F3}, {center.y:F3}) half=({half.x:F3}, {half.y:F3}) " +
                  $"normalized=({normalized.x:F3}, {normalized.y:F3}) scored={scored}");
    }

    private void HandleSessionStarted()
    {
        ClearRecordedShots();
    }

    private void HandleSessionEnded()
    {
        CommitSession();
        ShowHeatmap();
    }

    /// <summary>Adds the session that just ended to the history, once.</summary>
    private void CommitSession()
    {
        if (_committed)
            return;

        _committed = true;
        if (_shots.Count == 0)
            return;

        _history.AddRange(_shots);
        _nextSession++;
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Clears the current session's shots and hides the heatmap (and any Lifetime
    /// view). The All Sessions history is kept; see ResetGradientHistory.</summary>
    public void ClearRecordedShots()
    {
        _shots.Clear();
        _committed = false;
        _lifetimeShots = null;
        _visible = false;

        HideDisplay();
    }

    /// <summary>Shows the heatmap for whatever shots have been recorded so far.</summary>
    public void ShowHeatmap()
    {
        _visible = true;
        RebuildDisplay();
    }

    /// <summary>Replaces the All Sessions history, e.g. with a player's saved shots
    /// (Player_Session_History_Plan.md). Oldest first; each shot's session index sets how much
    /// it fades. A session still being recorded is kept and stays the newest. A session that
    /// already ended is in the old history, so it's replaced with it (its dots stay until the
    /// next session starts).</summary>
    public void LoadHistory(IEnumerable<RecordedShot> shots)
    {
        _history.Clear();
        if (shots != null)
            _history.AddRange(shots);

        int next = 0;
        for (int i = 0; i < _history.Count; i++)
            next = Mathf.Max(next, _history[i].session + 1);

        RestampLiveSession(next);
        Redraw();
    }

    /// <summary>Lifetime view (Player_Session_History_Plan.md): the gradient of the given shots
    /// with every session weighted equally (ignores recencyDecay and gradientMode), no dots, and
    /// their totals on the corner labels and headline. ShowLive or the next session start
    /// closes it. Refused while a session is running.</summary>
    public void ShowLifetime(IReadOnlyList<RecordedShot> shots)
    {
        if (_launcher != null && _launcher.IsSessionRunning)
        {
            Debug.LogWarning("[ShotHeatmapRecorder] ShowLifetime refused: a session is running.");
            return;
        }

        _lifetimeShots = shots != null ? new List<RecordedShot>(shots) : new List<RecordedShot>();
        RebuildDisplay();
    }

    /// <summary>Closes the Lifetime view and brings back the last live session's readout (or
    /// hides the heatmap if there isn't one).</summary>
    public void ShowLive()
    {
        _lifetimeShots = null;

        if (_visible && _shots.Count > 0)
            RebuildDisplay();
        else
            HideDisplay();
    }

    [ContextMenu("Reset gradient history")]
    public void ResetGradientHistory()
    {
        _history.Clear();
        RestampLiveSession(0);
        Redraw();
    }

    /// <summary>Gives a session that isn't in the history yet the given index, so it stays the
    /// newest after the history changes.</summary>
    private void RestampLiveSession(int index)
    {
        _nextSession = index;
        if (_committed)
            return;

        for (int i = 0; i < _shots.Count; i++)
            _shots[i] = new RecordedShot(_shots[i].normalized, _shots[i].scored, index);
    }

    private void Redraw()
    {
        if (_lifetimeShots != null || _visible)
            RebuildDisplay();
    }

    // ── Display ──────────────────────────────────────────────────

    private void RebuildDisplay()
    {
        bool lifetime = _lifetimeShots != null;
        if (dotContainer == null || (!lifetime && (!_visible || _shots.Count == 0)))
            return;

        if (heatmapPanel != null)
            heatmapPanel.SetActive(true);

        UpdateCageFrame();
        UpdateLabels(lifetime);

        ClearDotInstances();
        bool gradientShown = UpdateGradient(lifetime);
        UpdateLegend(gradientShown);

        if (!lifetime && showDots && dotPrefab != null)
            PlotDots();
    }

    private void HideDisplay()
    {
        ClearDotInstances();

        if (_gradientImage != null)
            _gradientImage.gameObject.SetActive(false);
        if (_legend != null)
            _legend.SetActive(false);
        if (heatmapPanel != null)
            heatmapPanel.SetActive(false);
    }

    private void PlotDots()
    {
        Vector2 boxSize = dotContainer.rect.size;
        Debug.Log($"[ShotHeatmapRecorder] RebuildDisplay: dotContainer size={boxSize}, plotting {_shots.Count} points");

        Color goalColor = colorblindSafe ? UiPalette.GoalSafe : scoreColor;
        Color savedColor = colorblindSafe ? UiPalette.SaveSafe : saveColor;

        for (int i = 0; i < _shots.Count; i++)
        {
            RecordedShot shot = _shots[i];
            RectTransform prefab = shot.scored && goalMarkPrefab != null ? goalMarkPrefab : dotPrefab;

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
                shot.normalized.x * 0.5f * boxSize.x,
                shot.normalized.y * 0.5f * boxSize.y);

            // Graphic covers both a UI Image (dot) and a TMP text (× goal mark).
            Graphic graphic = dot.GetComponent<Graphic>();
            if (graphic != null)
                graphic.color = shot.scored ? goalColor : savedColor;

            Debug.Log($"[ShotHeatmapRecorder] Dot {i}: normalized=({shot.normalized.x:F3}, {shot.normalized.y:F3}) " +
                      $"-> anchoredPosition={dot.anchoredPosition} scored={shot.scored}");

            _dotInstances.Add(dot);
        }
    }

    /// <summary>Buckets each shot by quadrant, fresh off the same list the dot-plot uses (or the
    /// Lifetime shots) — no separate running counters to keep in sync or reset — and writes the
    /// headline and corner labels (or the single fallback label).</summary>
    private void UpdateLabels(bool lifetime)
    {
        List<RecordedShot> shots = lifetime ? _lifetimeShots : _shots;

        int[] saves = new int[4];
        int[] totals = new int[4];
        int totalSaves = 0;

        for (int i = 0; i < shots.Count; i++)
        {
            int index = (int)QuadrantMath.BucketFromNormalized(shots[i].normalized);
            totals[index]++;
            if (!shots[i].scored)
            {
                saves[index]++;
                totalSaves++;
            }
        }

        string headline = $"{totalSaves} of {shots.Count} saved";

        // All Sessions: this session's tally, then the running one under it.
        string trend = "";
        if (!lifetime && gradientMode == GradientMode.AllSessions)
        {
            CollectAllSessions(_scratch);
            int allSaves = 0;
            for (int i = 0; i < _scratch.Count; i++)
                if (!_scratch[i].scored)
                    allSaves++;

            if (_scratch.Count > 0)
            {
                headline = "This session: " + headline;
                trend = $"All sessions: {allSaves} of {_scratch.Count} " +
                        $"({Mathf.RoundToInt(100f * allSaves / _scratch.Count)}%)";
            }
        }

        // The difficulty, so screenshots say what level the session was at. It can't change
        // mid-session, so the selector's current level is the session's level. Lifetime spans
        // many levels, so it's labelled as Lifetime instead.
        string level = lifetime ? "Lifetime" : _difficulty != null ? _difficulty.SelectedName : "";

        string Count(Quadrant q)
        {
            int index = (int)q;
            return totals[index] == 0 ? "No shots" : $"{saves[index]} of {totals[index]}";
        }

        // Extra lines smaller, so they stay footnotes to the headline.
        if (headlineText != null)
        {
            string text = headline;
            if (trend.Length > 0)
                text += $"\n<size=60%>{trend}</size>";
            if (level.Length > 0)
                text += $"\n<size=60%>{level}</size>";
            headlineText.text = text;
        }

        if (cornerLabels != null)
        {
            for (int i = 0; i < cornerLabels.Length && i < 4; i++)
            {
                if (cornerLabels[i] == null) continue;

                Quadrant q = (Quadrant)i;
                cornerLabels[i].text = $"{PositionName(q)}\n{Count(q)}";
                cornerLabels[i].alignment = CornerAlignment(q);
                cornerLabels[i].color = UiPalette.Chalk;
            }
        }

        if (headlineText == null && quadrantBreakdownText != null)
        {
            // Plain position name (where on the goal), then the tally.
            string Corner(Quadrant q)
            {
                int index = (int)q;
                return $"{PositionName(q)}: {saves[index]}/{totals[index]}";
            }

            // Level inline here, so the corner lines below keep their place.
            string levelSuffix = level.Length > 0 ? $" ({level})" : "";
            string trendLine = trend.Length > 0 ? $"{trend}\n" : "";
            quadrantBreakdownText.text =
                $"Shot Heatmap: {headline}{levelSuffix}\n" +
                trendLine +
                $"{Corner(Quadrant.TopLeft)}  ·  {Corner(Quadrant.TopRight)}\n" +
                $"{Corner(Quadrant.BottomLeft)}  ·  {Corner(Quadrant.BottomRight)}";
        }
    }

    /// <summary>Plain position name for a corner, as the goalie sees the goal.</summary>
    private static string PositionName(Quadrant quadrant)
    {
        switch (quadrant)
        {
            case Quadrant.TopLeft: return "Top left";
            case Quadrant.TopRight: return "Top right";
            case Quadrant.BottomLeft: return "Bottom left";
            default: return "Bottom right";
        }
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

    // ── Gradient ─────────────────────────────────────────────────

    /// <summary>Every shot the All Sessions mode covers, oldest first: the history plus the
    /// current session if it isn't in the history yet.</summary>
    private void CollectAllSessions(List<RecordedShot> into)
    {
        into.Clear();
        into.AddRange(_history);
        if (!_committed)
            into.AddRange(_shots);
    }

    /// <summary>Draws the gradient for the current view. Returns whether it's shown.</summary>
    private bool UpdateGradient(bool lifetime)
    {
        _scratch.Clear();
        int start = 0;
        bool recency = false;

        if (lifetime)
        {
            _scratch.AddRange(_lifetimeShots);
        }
        else if (gradientMode == GradientMode.ThisSession)
        {
            _scratch.AddRange(_shots);
        }
        else if (gradientMode == GradientMode.AllSessions)
        {
            CollectAllSessions(_scratch);
            start = Mathf.Max(0, _scratch.Count - maxGradientShots);
            recency = true;
        }

        if (_scratch.Count - start <= 0)
        {
            if (_gradientImage != null)
                _gradientImage.gameObject.SetActive(false);
            return false;
        }

        EnsureGradientImage();
        FillGradient(_scratch, start, recency);

        _gradientImage.gameObject.SetActive(true);
        // First sibling, so the order is gradient → cage crosshair/frame → dots.
        _gradientImage.rectTransform.SetAsFirstSibling();
        return true;
    }

    private void EnsureGradientImage()
    {
        if (_gradientImage == null)
        {
            RectTransform rect = CreateRect("Gradient", dotContainer, Vector2.zero, Vector2.one,
                                            Vector2.zero, Vector2.zero);
            _gradientImage = rect.gameObject.AddComponent<RawImage>();
            _gradientImage.raycastTarget = false;
        }

        int n = gradientResolution;
        if (_gradientTexture == null || _gradientTexture.width != n)
        {
            if (_gradientTexture != null)
                Destroy(_gradientTexture);

            _gradientTexture = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "HeatmapGradient",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            _density = new float[n * n];
            _goalSum = new float[n * n];
            _pixels = new Color32[n * n];
            _columnWeights = new float[n];
            _rowWeights = new float[n];
        }

        _gradientImage.texture = _gradientTexture;
    }

    /// <summary>Each shot adds a Gaussian blob (σ = gradientRadius) to every pixel near it.
    /// Density sets brightness, relative to the map's own peak; the scored share sets the color.
    /// Texture row 0 is the bottom, matching normalized -1 = bottom.
    /// The Gaussian splits into a column part times a row part, so each shot needs one Exp per
    /// column and per row it touches instead of one per pixel.</summary>
    private void FillGradient(List<RecordedShot> shots, int start, bool recency)
    {
        int n = gradientResolution;
        Array.Clear(_density, 0, _density.Length);
        Array.Clear(_goalSum, 0, _goalSum.Length);

        int newest = 0;
        if (recency)
            for (int i = start; i < shots.Count; i++)
                newest = Mathf.Max(newest, shots[i].session);

        float sigma = gradientRadius;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);
        // Past 4σ on either axis a blob is under 0.04% of its peak, so skipping it costs
        // nothing visible.
        float cutoff = 4f * sigma;
        float cell = 2f / n;

        for (int i = start; i < shots.Count; i++)
        {
            RecordedShot shot = shots[i];
            float weight = recency ? Mathf.Pow(recencyDecay, newest - shot.session) : 1f;
            Vector2 p = shot.normalized;

            int x0 = Mathf.Max(0, Mathf.FloorToInt((p.x - cutoff + 1f) / cell));
            int x1 = Mathf.Min(n - 1, Mathf.CeilToInt((p.x + cutoff + 1f) / cell));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((p.y - cutoff + 1f) / cell));
            int y1 = Mathf.Min(n - 1, Mathf.CeilToInt((p.y + cutoff + 1f) / cell));

            for (int px = x0; px <= x1; px++)
            {
                float dx = (px + 0.5f) * cell - 1f - p.x;
                _columnWeights[px] = Mathf.Exp(-dx * dx * inv2Sigma2);
            }
            for (int py = y0; py <= y1; py++)
            {
                float dy = (py + 0.5f) * cell - 1f - p.y;
                // The session weight rides on the row part, so the inner loop is one multiply.
                _rowWeights[py] = Mathf.Exp(-dy * dy * inv2Sigma2) * weight;
            }

            for (int py = y0; py <= y1; py++)
            {
                float rowWeight = _rowWeights[py];
                int rowStart = py * n;
                for (int px = x0; px <= x1; px++)
                {
                    float w = _columnWeights[px] * rowWeight;
                    _density[rowStart + px] += w;
                    if (shot.scored)
                        _goalSum[rowStart + px] += w;
                }
            }
        }

        float maxDensity = 0f;
        for (int i = 0; i < _density.Length; i++)
            maxDensity = Mathf.Max(maxDensity, _density[i]);

        for (int i = 0; i < _pixels.Length; i++)
        {
            float density = _density[i];
            if (density <= 0f || maxDensity <= 0f)
            {
                // Black and clear: invisible on the additive display, so empty cage stays see-through.
                _pixels[i] = new Color32(0, 0, 0, 0);
                continue;
            }

            float intensity = Mathf.Pow(density / maxDensity, gradientGamma);
            float saveRate = 1f - _goalSum[i] / density;
            Color c = UiPalette.SaveRateRamp(saveRate, colorblindSafe);

            // Full ramp color with alpha = intensity: the UI shader multiplies by alpha, so that
            // alone sets the brightness (darkening the RGB too would square it).
            _pixels[i] = new Color(c.r, c.g, c.b, intensity);
        }

        _gradientTexture.SetPixels32(_pixels);
        _gradientTexture.Apply(false);
    }

    // ── Legend ───────────────────────────────────────────────────

    /// <summary>The legend only explains the gradient, so it hides whenever the gradient is.</summary>
    private void UpdateLegend(bool gradientShown)
    {
        if (!drawGradientLegend || !gradientShown)
        {
            if (_legend != null)
                _legend.SetActive(false);
            return;
        }

        if (_legend == null)
            BuildLegend();

        // Laid out every time, so the gap/size fields can be tuned between sessions.
        float top = -((drawCageFrame ? bottomBarThickness : 0f) + legendGap);
        float bottom = top - legendHeight;
        float labelBottom = bottom - legendFontSize * 1.5f;

        _legendStrip.offsetMin = new Vector2(0f, bottom);
        _legendStrip.offsetMax = new Vector2(0f, top);

        _legendGoals.rectTransform.offsetMin = new Vector2(0f, labelBottom);
        _legendGoals.rectTransform.offsetMax = new Vector2(0f, bottom);
        _legendSaves.rectTransform.offsetMin = new Vector2(0f, labelBottom);
        _legendSaves.rectTransform.offsetMax = new Vector2(0f, bottom);
        _legendGoals.fontSize = legendFontSize;
        _legendSaves.fontSize = legendFontSize;

        // The ramp only changes with colorblindSafe, so refill the texture only then.
        if (_legendRampColorblind != colorblindSafe)
        {
            Color32[] ramp = new Color32[LegendResolution];
            for (int i = 0; i < ramp.Length; i++)
                ramp[i] = UiPalette.SaveRateRamp(i / (LegendResolution - 1f), colorblindSafe);
            _legendTexture.SetPixels32(ramp);
            _legendTexture.Apply(false);
            _legendRampColorblind = colorblindSafe;
        }

        _legend.SetActive(true);
    }

    /// <summary>Built once, as a child of dotContainer stretched to its rect; the strip and
    /// labels hang below its bottom edge, so it follows the box like the cage frame.</summary>
    private void BuildLegend()
    {
        RectTransform root = CreateRect("GradientLegend", dotContainer, Vector2.zero, Vector2.one,
                                        Vector2.zero, Vector2.zero);
        _legend = root.gameObject;

        _legendTexture = new Texture2D(LegendResolution, 1, TextureFormat.RGBA32, false)
        {
            name = "HeatmapLegend",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        _legendStrip = CreateRect("Strip", root, new Vector2(0f, 0f), new Vector2(1f, 0f),
                                  Vector2.zero, Vector2.zero);
        RawImage strip = _legendStrip.gameObject.AddComponent<RawImage>();
        strip.texture = _legendTexture;
        strip.raycastTarget = false;

        // Save rate 0 (all goals) on the left, 1 (all saves) on the right, as the strip runs.
        _legendGoals = CreateLegendLabel("GoalsLabel", root, new Vector2(0f, 0f), new Vector2(0.5f, 0f),
                                         "Goals", TextAlignmentOptions.TopLeft);
        _legendSaves = CreateLegendLabel("SavesLabel", root, new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                                         "Saves", TextAlignmentOptions.TopRight);
    }

    private TextMeshProUGUI CreateLegendLabel(string name, Transform parent, Vector2 anchorMin,
                                              Vector2 anchorMax, string text, TextAlignmentOptions alignment)
    {
        RectTransform rect = CreateRect(name, parent, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
        TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (headlineText != null)
            label.font = headlineText.font;
        label.text = text;
        label.alignment = alignment;
        label.color = UiPalette.Chalk;
        label.raycastTarget = false;
        return label;
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
            // First sibling, so dots and labels draw on top of it. (The gradient is moved in
            // front of it afterwards, in UpdateGradient.)
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
