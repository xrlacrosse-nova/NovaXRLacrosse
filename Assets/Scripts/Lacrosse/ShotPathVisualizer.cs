using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Display half of shot path tracking (Plans/Past/Shot_Path_Tracking_Plan.md): draws every
/// shot ShotPathRecorder recorded as a 3D streak in the world, along the exact route the ball
/// flew — launch point to the gate plane (or the stick, for a save).
///
/// Shown when the recorder fires OnPathsReady (session end) and cleared on OnPathsCleared
/// (session start) — the heatmap's lifetime, never during play. No input handling of any kind.
///
/// It catches up in OnEnable (draws or clears to match the recorder), so it still works if its
/// own GameObject was inactive when an event fired — but an always-active GameObject (e.g. the
/// ball) is the intended spot. The streaks themselves live under a runtime "ShotPaths3D" object
/// at the scene root, never under the ball, which keeps moving.
///
/// Streaks taper and fade toward the launch end, so the pile-up near the shooter stays readable.
/// Color is the outcome (UiPalette tokens, matching the heatmap), and the end marker backs it up
/// by shape, as the heatmap does: × on the gate plane for a goal, a sphere at the stick for a
/// save, nothing for a miss. A save's line stops at the stick, with a thin dim tail on to the
/// gate plane.
/// </summary>
public class ShotPathVisualizer : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The recorder on the ball. Leave empty to find one in the scene.")]
    public ShotPathRecorder recorder;

    [Header("Lines")]
    [Tooltip("Optional material for the streaks and markers. It must use vertex color for the " +
             "fade (e.g. URP Particles/Unlit). Leave empty to use the built-in Sprites/Default. " +
             "Dark colors render transparent on ML2 — keep line colors bright.")]
    public Material lineMaterial;

    [Tooltip("Streak width (meters) at the end of the shot.")]
    [Min(0f)]
    public float streakWidth = 0.02f;

    [Tooltip("Streak width at the launch end, as a fraction of streakWidth.")]
    [Range(0f, 1f)]
    public float launchWidthFraction = 0.2f;

    [Tooltip("Streak alpha at the launch end (full alpha at the end of the shot).")]
    [Range(0f, 1f)]
    public float launchAlpha = 0.1f;

    [Tooltip("Width (meters) of a save's projected tail from the stick to the gate plane.")]
    [Min(0f)]
    public float tailStreakWidth = 0.006f;

    [Tooltip("Alpha of a save's projected tail.")]
    [Range(0f, 1f)]
    public float tailAlpha = 0.5f;

    [Header("End Markers")]
    [Tooltip("Diameter (meters) of the sphere at the stick on a save.")]
    [Min(0f)]
    public float saveMarkerSize = 0.05f;

    [Tooltip("Size (meters) of the × on the gate plane for a goal.")]
    [Min(0f)]
    public float goalMarkerSize = 0.08f;

    [Tooltip("Stroke width (meters) of the × for a goal.")]
    [Min(0f)]
    public float goalMarkerThickness = 0.012f;

    [Header("Colors")]
    [Tooltip("Shots that were saved.")]
    public Color saveColor = UiPalette.Save;

    [Tooltip("Shots that scored.")]
    public Color scoreColor = UiPalette.Goal;

    [Tooltip("Shots wide of the cage.")]
    public Color missColor = UiPalette.Miss;

    // ── Private ───────────────────────────────────────────────────

    private const string FallbackShader = "Sprites/Default";

    private Transform _root;
    private Material _material;
    private bool _ownsMaterial;

    // One GameObject per drawn shot, so a clear only touches what this created.
    private readonly List<GameObject> _drawn = new List<GameObject>();

    // Per-color materials for the save spheres (a mesh has no vertex colors to tint).
    private readonly List<Material> _markerMaterials = new List<Material>();

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (recorder == null)
            recorder = FindFirstObjectByType<ShotPathRecorder>();

        if (recorder == null)
            Debug.LogWarning("[ShotPathVisualizer] No ShotPathRecorder found — nothing will be drawn.");

        if (lineMaterial != null)
        {
            _material = lineMaterial;
        }
        else
        {
            Shader shader = Shader.Find(FallbackShader);
            if (shader == null)
                Debug.LogWarning($"[ShotPathVisualizer] Shader \"{FallbackShader}\" not found — " +
                                 "assign a Line Material, or the streaks won't render correctly.");
            else
            {
                _material = new Material(shader);
                _ownsMaterial = true;
            }
        }

        _root = new GameObject("ShotPaths3D").transform;
    }

    void OnEnable()
    {
        if (recorder == null) return;

        recorder.OnPathsReady += HandlePathsReady;
        recorder.OnPathsCleared += HandlePathsCleared;

        // Catch up on anything that happened while this was inactive (unsubscribed).
        if (recorder.PathsShown)
            HandlePathsReady(recorder.Paths);
        else
            HandlePathsCleared();
    }

    void OnDisable()
    {
        if (recorder == null) return;

        recorder.OnPathsReady -= HandlePathsReady;
        recorder.OnPathsCleared -= HandlePathsCleared;
    }

    void OnDestroy()
    {
        ClearDrawn();

        if (_ownsMaterial && _material != null)
            Destroy(_material);

        if (_root != null)
            Destroy(_root.gameObject);
    }

    // ── Display ──────────────────────────────────────────────────

    private void HandlePathsCleared()
    {
        ClearDrawn();
    }

    private void HandlePathsReady(IReadOnlyList<ShotPathRecorder.ShotPath> paths)
    {
        ClearDrawn();

        for (int i = 0; i < paths.Count; i++)
            DrawPath(paths[i], i + 1);
    }

    /// <summary>Destroys every streak and marker from the last redraw.</summary>
    private void ClearDrawn()
    {
        for (int i = 0; i < _drawn.Count; i++)
            if (_drawn[i] != null)
                Destroy(_drawn[i]);
        _drawn.Clear();

        for (int i = 0; i < _markerMaterials.Count; i++)
            Destroy(_markerMaterials[i]);
        _markerMaterials.Clear();
    }

    private void DrawPath(ShotPathRecorder.ShotPath path, int number)
    {
        if (path.points == null || path.points.Length < 2) return;

        GameObject shot = new GameObject($"Shot {number} ({path.outcome})");
        shot.transform.SetParent(_root, false);
        _drawn.Add(shot);

        Color color = ColorFor(path.outcome);
        Color launchColor = color;
        launchColor.a = launchAlpha;

        AddLine(shot.transform, "Streak", path.points,
                streakWidth * launchWidthFraction, streakWidth, launchColor, color);

        if (path.projectedTail != null && path.projectedTail.Length >= 2)
        {
            Color tailColor = color;
            tailColor.a = tailAlpha;
            AddLine(shot.transform, "Tail", path.projectedTail,
                    tailStreakWidth, tailStreakWidth, tailColor, tailColor);
        }

        Vector3 end = path.points[path.points.Length - 1];
        switch (path.outcome)
        {
            case ShotPathRecorder.PathOutcome.Goal:
                AddCross(shot.transform, end, color);
                break;
            case ShotPathRecorder.PathOutcome.Save:
                AddSphere(shot.transform, end, color);
                break;
        }
    }

    private void AddLine(Transform parent, string name, Vector3[] points,
                         float startWidth, float endWidth, Color startColor, Color endColor)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = points.Length;
        line.SetPositions(points);
        line.startWidth = startWidth;
        line.endWidth = endWidth;
        line.startColor = startColor;
        line.endColor = endColor;
        line.numCapVertices = 2;
        line.alignment = LineAlignment.View;
        line.sharedMaterial = _material;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
    }

    /// <summary>× flat on the gate plane (which faces Z), centered on the crossing point.</summary>
    private void AddCross(Transform parent, Vector3 center, Color color)
    {
        float h = goalMarkerSize * 0.5f;
        Vector3 a = new Vector3(h, h, 0f);
        Vector3 b = new Vector3(h, -h, 0f);

        AddLine(parent, "Cross A", new[] { center - a, center + a },
                goalMarkerThickness, goalMarkerThickness, color, color);
        AddLine(parent, "Cross B", new[] { center - b, center + b },
                goalMarkerThickness, goalMarkerThickness, color, color);
    }

    /// <summary>Sphere at the stick contact. Its collider is removed straight away so it can't
    /// touch the ball or the stick.</summary>
    private void AddSphere(Transform parent, Vector3 center, Color color)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        DestroyImmediate(sphere.GetComponent<Collider>());

        sphere.name = "Save Marker";
        sphere.transform.SetParent(parent, false);
        sphere.transform.position = center;
        sphere.transform.localScale = Vector3.one * saveMarkerSize;

        MeshRenderer renderer = sphere.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        if (_material != null)
        {
            Material material = new Material(_material) { color = color };
            _markerMaterials.Add(material);
            renderer.sharedMaterial = material;
        }
    }

    private Color ColorFor(ShotPathRecorder.PathOutcome outcome)
    {
        switch (outcome)
        {
            case ShotPathRecorder.PathOutcome.Goal: return scoreColor;
            case ShotPathRecorder.PathOutcome.Save: return saveColor;
            default: return missColor;
        }
    }
}
