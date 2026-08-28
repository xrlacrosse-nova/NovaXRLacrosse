using System.Collections.Generic;
using UnityEngine;

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
    [Tooltip("Color for shots that scored.")]
    public Color goalColor = Color.green;

    [Tooltip("Color for shots that missed.")]
    public Color missColor = Color.red;

    [Tooltip("Radius (in pixels) of each plotted shot marker.")]
    [Range(2f, 20f)]
    public float dotRadius = 6f;

    [Tooltip("Width of the heatmap box on screen, as a fraction of screen width.")]
    [Range(0.1f, 0.9f)]
    public float boxWidthFraction = 0.4f;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;

    private readonly List<Vector2> _points = new List<Vector2>();
    private readonly List<bool> _scored = new List<bool>();
    private bool _visible = false;

    private static Texture2D _dotTexture;
    private static Texture2D DotTexture
    {
        get
        {
            if (_dotTexture == null)
            {
                _dotTexture = new Texture2D(1, 1);
                _dotTexture.SetPixel(0, 0, Color.white);
                _dotTexture.Apply();
            }
            return _dotTexture;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
        _launcher = GetComponent<RandomLauncher>();
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
    }

    /// <summary>Shows the heatmap for whatever shots have been recorded so far.</summary>
    public void ShowHeatmap()
    {
        _visible = true;
    }

    // ── On-screen heatmap ────────────────────────────────────────

    void OnGUI()
    {
        if (!_visible || _points.Count == 0) return;

        Vector2 half = _goalDetector.goalGateHalfSize;
        float boxWidth = Screen.width * boxWidthFraction;
        float boxHeight = boxWidth * (half.y / half.x);
        Rect box = new Rect((Screen.width - boxWidth) * 0.5f, Screen.height * 0.55f, boxWidth, boxHeight);

        int goals = 0;
        for (int i = 0; i < _scored.Count; i++)
            if (_scored[i]) goals++;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 32,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        GUI.Label(new Rect(box.x, box.y - 50f, box.width, 40f),
                  $"SHOT HEATMAP — {goals}/{_points.Count} scored", titleStyle);

        GUI.Box(box, GUIContent.none);

        for (int i = 0; i < _points.Count; i++)
        {
            // point.x/y are in [-1, 1] with +y = up; GUI space has +y = down, so invert.
            float px = box.x + (_points[i].x * 0.5f + 0.5f) * box.width;
            float py = box.y + (1f - (_points[i].y * 0.5f + 0.5f)) * box.height;

            DrawDot(px, py, dotRadius, _scored[i] ? goalColor : missColor);
        }
    }

    private static void DrawDot(float centerX, float centerY, float radius, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(centerX - radius, centerY - radius, radius * 2f, radius * 2f), DotTexture);
        GUI.color = prev;
    }
}
