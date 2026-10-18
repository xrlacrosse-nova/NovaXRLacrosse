using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI mesh that draws ShotPathVisualizer's streaks and end caps inside a RectTransform. Built at
/// runtime by ShotPathVisualizer — not meant to be added by hand.
///
/// Drawn as a canvas Graphic (not LineRenderers) so it sorts, scales and hides with the rest of
/// the World Space heatmap canvas, and so per-vertex width and color give the streak taper and
/// alpha fade for free. Positions are normalized to the rect ([0, 1] on each axis, 0 = left /
/// bottom) and mapped at mesh-build time, so the drawing follows the rect's size.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class ShotPathGraphic : MaskableGraphic
{
    private struct Stroke
    {
        public Vector2[] points;   // normalized rect coordinates
        public float startWidth, endWidth;
        public Color startColor, endColor;
    }

    private struct Mark
    {
        public Vector2 position;   // normalized rect coordinates
        public float size;
        public float thickness;    // cross only
        public Color color;
        public bool isCross;
    }

    private const int DotSegments = 12;

    private readonly List<Stroke> _strokes = new List<Stroke>();
    private readonly List<Mark> _marks = new List<Mark>();

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Removes everything drawn.</summary>
    public void Clear()
    {
        _strokes.Clear();
        _marks.Clear();
        SetVerticesDirty();
    }

    /// <summary>A polyline whose width and color blend from the first point to the last.</summary>
    public void AddStroke(Vector2[] points, float startWidth, float endWidth, Color startColor, Color endColor)
    {
        if (points == null || points.Length < 2) return;

        _strokes.Add(new Stroke
        {
            points = points,
            startWidth = startWidth,
            endWidth = endWidth,
            startColor = startColor,
            endColor = endColor,
        });
        SetVerticesDirty();
    }

    /// <summary>An × of the given size (UI units, tip to tip) and stroke thickness.</summary>
    public void AddCross(Vector2 position, float size, float thickness, Color color)
    {
        _marks.Add(new Mark { position = position, size = size, thickness = thickness, color = color, isCross = true });
        SetVerticesDirty();
    }

    /// <summary>A filled dot of the given diameter (UI units).</summary>
    public void AddDot(Vector2 position, float size, Color color)
    {
        _marks.Add(new Mark { position = position, size = size, color = color, isCross = false });
        SetVerticesDirty();
    }

    // ── Mesh ─────────────────────────────────────────────────────

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect rect = rectTransform.rect;

        for (int s = 0; s < _strokes.Count; s++)
            DrawStroke(vh, rect, _strokes[s]);

        // Marks after strokes, so the end caps sit on top of the lines.
        for (int m = 0; m < _marks.Count; m++)
        {
            Mark mark = _marks[m];
            Vector2 center = ToLocal(rect, mark.position);

            if (mark.isCross)
            {
                float h = mark.size * 0.5f * 0.7071f; // half-extent of each diagonal along x and y
                AddQuadLine(vh, center + new Vector2(-h, -h), center + new Vector2(h, h),
                            mark.thickness, mark.thickness, mark.color, mark.color);
                AddQuadLine(vh, center + new Vector2(-h, h), center + new Vector2(h, -h),
                            mark.thickness, mark.thickness, mark.color, mark.color);
            }
            else
            {
                AddDisc(vh, center, mark.size * 0.5f, mark.color);
            }
        }
    }

    private static void DrawStroke(VertexHelper vh, Rect rect, Stroke stroke)
    {
        int last = stroke.points.Length - 1;

        for (int i = 0; i < last; i++)
        {
            float t0 = (float)i / last;
            float t1 = (float)(i + 1) / last;

            AddQuadLine(vh,
                        ToLocal(rect, stroke.points[i]), ToLocal(rect, stroke.points[i + 1]),
                        Mathf.Lerp(stroke.startWidth, stroke.endWidth, t0),
                        Mathf.Lerp(stroke.startWidth, stroke.endWidth, t1),
                        Color.Lerp(stroke.startColor, stroke.endColor, t0),
                        Color.Lerp(stroke.startColor, stroke.endColor, t1));
        }
    }

    private static Vector2 ToLocal(Rect rect, Vector2 normalized)
    {
        return new Vector2(rect.xMin + normalized.x * rect.width, rect.yMin + normalized.y * rect.height);
    }

    /// <summary>One segment as a quad, with its own width and color at each end. Straight flights
    /// barely bend, so segments are left unjoined.</summary>
    private static void AddQuadLine(VertexHelper vh, Vector2 a, Vector2 b, float widthA, float widthB,
                                    Color colorA, Color colorB)
    {
        Vector2 dir = b - a;
        if (dir.sqrMagnitude < 1e-6f) return;

        Vector2 normal = new Vector2(-dir.y, dir.x).normalized;
        Vector2 na = normal * (widthA * 0.5f);
        Vector2 nb = normal * (widthB * 0.5f);

        int start = vh.currentVertCount;
        vh.AddVert(a - na, colorA, Vector4.zero);
        vh.AddVert(a + na, colorA, Vector4.zero);
        vh.AddVert(b + nb, colorB, Vector4.zero);
        vh.AddVert(b - nb, colorB, Vector4.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    private static void AddDisc(VertexHelper vh, Vector2 center, float radius, Color color)
    {
        int start = vh.currentVertCount;
        vh.AddVert(center, color, Vector4.zero);

        for (int i = 0; i < DotSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / DotSegments;
            vh.AddVert(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color, Vector4.zero);
        }

        for (int i = 0; i < DotSegments; i++)
            vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % DotSegments);
    }
}
