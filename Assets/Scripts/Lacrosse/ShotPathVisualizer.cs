using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Display half of shot path tracking (Plans/Current/Shot_Path_Tracking_Plan.md): draws every
/// shot ShotPathRecorder recorded as a streak in a 2D side-view inset on the heatmap canvas —
/// depth (world Z) across, height (world Y) up. Can sit on any GameObject.
///
/// Shown when the recorder fires OnPathsReady (session end) and cleared on OnPathsCleared
/// (session start) — the heatmap's lifetime, never during play. Put the container under the
/// heatmap panel so it also hides and shows with it. No input handling of any kind.
///
/// Streaks taper and fade toward the launch end, so the pile-up near the shooter stays readable.
/// Color is the outcome (UiPalette tokens, matching the heatmap), and the end cap backs it up by
/// shape, as the heatmap does: × for a goal, a dot at the stick for a save, nothing for a miss.
/// A save's line stops at the stick, with a thin dim tail on to the gate plane.
/// </summary>
public class ShotPathVisualizer : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The recorder on the ball. Leave empty to find one in the scene.")]
    public ShotPathRecorder recorder;

    [Header("Container")]
    [Tooltip("The inset box on the heatmap canvas. The streaks are drawn stretched to fill it. " +
             "Put it under the heatmap panel so it shows and hides with the heatmap.")]
    public RectTransform container;

    [Tooltip("Inset (fraction of the box, per side) kept clear around the drawing.")]
    [Range(0f, 0.4f)]
    public float padding = 0.08f;

    [Tooltip("Draw the goal end on the box's right edge. Off = left edge.")]
    public bool gateOnRight = true;

    [Header("Lines")]
    [Tooltip("Optional material for the streaks. Leave empty for the default UI material. " +
             "Dark colors render transparent on ML2 — keep line colors bright.")]
    public Material lineMaterial;

    [Tooltip("Streak width (UI units) at the end of the shot.")]
    [Min(0f)]
    public float lineWidth = 10f;

    [Tooltip("Streak width at the launch end, as a fraction of lineWidth.")]
    [Range(0f, 1f)]
    public float launchWidthFraction = 0.2f;

    [Tooltip("Streak alpha at the launch end (full alpha at the end of the shot).")]
    [Range(0f, 1f)]
    public float launchAlpha = 0.1f;

    [Tooltip("Width (UI units) of a save's projected tail from the stick to the gate plane.")]
    [Min(0f)]
    public float tailWidth = 3f;

    [Tooltip("Alpha of a save's projected tail.")]
    [Range(0f, 1f)]
    public float tailAlpha = 0.5f;

    [Header("End Caps")]
    [Tooltip("Size (UI units) of the × on a goal and the dot on a save.")]
    [Min(0f)]
    public float capSize = 26f;

    [Tooltip("Stroke thickness (UI units) of the × on a goal.")]
    [Min(0f)]
    public float crossThickness = 6f;

    [Header("Colors")]
    [Tooltip("Shots that were saved.")]
    public Color saveColor = UiPalette.Save;

    [Tooltip("Shots that scored.")]
    public Color scoreColor = UiPalette.Goal;

    [Tooltip("Shots wide of the cage.")]
    public Color missColor = UiPalette.Miss;

    [Header("Gate Marker")]
    [Tooltip("Draw the goal mouth as a vertical bar at the gate plane, for reference.")]
    public bool drawGateMarker = true;

    [Tooltip("Width (UI units) of the gate marker.")]
    [Min(0f)]
    public float gateMarkerWidth = 6f;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private ShotPathGraphic _graphic;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (recorder == null)
            recorder = FindFirstObjectByType<ShotPathRecorder>();

        if (recorder == null)
            Debug.LogWarning("[ShotPathVisualizer] No ShotPathRecorder found — nothing will be drawn.");
        else
            _goalDetector = recorder.GetComponent<GoalDetector>();

        if (container == null)
            Debug.LogWarning("[ShotPathVisualizer] No container assigned — nothing will be drawn.");
        else
            CreateGraphic();
    }

    void OnEnable()
    {
        if (recorder == null) return;

        recorder.OnPathsReady += HandlePathsReady;
        recorder.OnPathsCleared += HandlePathsCleared;
    }

    void OnDisable()
    {
        if (recorder == null) return;

        recorder.OnPathsReady -= HandlePathsReady;
        recorder.OnPathsCleared -= HandlePathsCleared;
    }

    /// <summary>One child stretched over the container, holding the mesh for every streak.</summary>
    private void CreateGraphic()
    {
        GameObject go = new GameObject("ShotPaths", typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(container, false);
        rect.localScale = Vector3.one;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _graphic = go.AddComponent<ShotPathGraphic>();
        _graphic.raycastTarget = false;
        if (lineMaterial != null)
            _graphic.material = lineMaterial;

        go.SetActive(false);
    }

    // ── Display ──────────────────────────────────────────────────

    private void HandlePathsCleared()
    {
        if (_graphic == null) return;

        _graphic.Clear();
        _graphic.gameObject.SetActive(false);
    }

    private void HandlePathsReady(IReadOnlyList<ShotPathRecorder.ShotPath> paths)
    {
        if (_graphic == null) return;

        _graphic.Clear();

        if (paths.Count == 0)
        {
            _graphic.gameObject.SetActive(false);
            return;
        }

        ComputeBounds(paths, out Vector2 min, out Vector2 max);

        if (drawGateMarker && _goalDetector != null)
        {
            Vector3 c = _goalDetector.goalGateCenter;
            float h = _goalDetector.goalGateHalfSize.y;
            _graphic.AddStroke(new[] { ToBox(new Vector3(0f, c.y - h, c.z), min, max),
                                       ToBox(new Vector3(0f, c.y + h, c.z), min, max) },
                               gateMarkerWidth, gateMarkerWidth, UiPalette.Chalk, UiPalette.Chalk);
        }

        for (int i = 0; i < paths.Count; i++)
            DrawPath(paths[i], min, max);

        _graphic.gameObject.SetActive(true);
    }

    private void DrawPath(ShotPathRecorder.ShotPath path, Vector2 min, Vector2 max)
    {
        Color color = ColorFor(path.outcome);
        Color launchColor = color;
        launchColor.a = launchAlpha;

        Vector2[] points = new Vector2[path.points.Length];
        for (int p = 0; p < points.Length; p++)
            points[p] = ToBox(path.points[p], min, max);

        _graphic.AddStroke(points, lineWidth * launchWidthFraction, lineWidth, launchColor, color);

        if (path.projectedTail != null && path.projectedTail.Length >= 2)
        {
            Color tailColor = color;
            tailColor.a = tailAlpha;

            Vector2[] tail = new Vector2[path.projectedTail.Length];
            for (int p = 0; p < tail.Length; p++)
                tail[p] = ToBox(path.projectedTail[p], min, max);

            _graphic.AddStroke(tail, tailWidth, tailWidth, tailColor, tailColor);
        }

        Vector2 end = points[points.Length - 1];
        switch (path.outcome)
        {
            case ShotPathRecorder.PathOutcome.Goal:
                _graphic.AddCross(end, capSize, crossThickness, color);
                break;
            case ShotPathRecorder.PathOutcome.Save:
                _graphic.AddDot(end, capSize, color);
                break;
        }
    }

    private Color ColorFor(ShotPathRecorder.PathOutcome outcome)
    {
        switch (outcome)
        {
            case ShotPathRecorder.PathOutcome.Save: return saveColor;
            case ShotPathRecorder.PathOutcome.Goal: return scoreColor;
            default: return missColor;
        }
    }

    // ── Side-view mapping ────────────────────────────────────────

    /// <summary>World (Z, Y) bounds of every path and tail, plus the goal mouth, so the gate
    /// marker is always in frame. Each axis is fitted to the box on its own — the flight is
    /// metres long but only about a metre tall, so a shared scale would flatten it to a line.</summary>
    private void ComputeBounds(IReadOnlyList<ShotPathRecorder.ShotPath> paths, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);

        for (int i = 0; i < paths.Count; i++)
        {
            Encapsulate(paths[i].points, ref min, ref max);
            Encapsulate(paths[i].projectedTail, ref min, ref max);
        }

        if (_goalDetector != null)
        {
            Vector3 c = _goalDetector.goalGateCenter;
            float h = _goalDetector.goalGateHalfSize.y;
            Encapsulate(new Vector2(c.z, c.y - h), ref min, ref max);
            Encapsulate(new Vector2(c.z, c.y + h), ref min, ref max);
        }

        // Keep a degenerate axis (e.g. a single flat shot) from dividing by zero.
        const float minSpan = 0.1f;
        for (int axis = 0; axis < 2; axis++)
        {
            float span = max[axis] - min[axis];
            if (span < minSpan)
            {
                float mid = (min[axis] + max[axis]) * 0.5f;
                min[axis] = mid - minSpan * 0.5f;
                max[axis] = mid + minSpan * 0.5f;
            }
        }
    }

    private static void Encapsulate(Vector3[] points, ref Vector2 min, ref Vector2 max)
    {
        if (points == null) return;

        for (int i = 0; i < points.Length; i++)
            Encapsulate(new Vector2(points[i].z, points[i].y), ref min, ref max);
    }

    private static void Encapsulate(Vector2 zy, ref Vector2 min, ref Vector2 max)
    {
        min = Vector2.Min(min, zy);
        max = Vector2.Max(max, zy);
    }

    /// <summary>World point → normalized box position. Z runs across (flipped so the gate lands
    /// on the side gateOnRight picks), Y runs up.</summary>
    private Vector2 ToBox(Vector3 world, Vector2 min, Vector2 max)
    {
        float u = Mathf.InverseLerp(min.x, max.x, world.z);
        float v = Mathf.InverseLerp(min.y, max.y, world.y);

        if (_goalDetector != null)
        {
            float gateU = Mathf.InverseLerp(min.x, max.x, _goalDetector.goalGateCenter.z);
            bool gateAtHighEnd = gateU >= 0.5f;
            if (gateAtHighEnd != gateOnRight)
                u = 1f - u;
        }

        return new Vector2(padding + u * (1f - 2f * padding), padding + v * (1f - 2f * padding));
    }
}
