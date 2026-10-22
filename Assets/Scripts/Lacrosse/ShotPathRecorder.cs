using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Records the flight path of every shot in a session (Plans/Current/Shot_Path_Tracking_Plan.md).
/// Attach to the same GameObject as GoalDetector (and RandomLauncher / ShotHeatmapRecorder, if
/// present) — the ball. Display lives in ShotPathVisualizer, which subscribes to
/// <see cref="OnPathsReady"/> / <see cref="OnPathsCleared"/>.
///
/// Same display window as ShotHeatmapRecorder: paths are handed out once a full session ends
/// (RandomLauncher.OnSessionEnded) and cleared the moment the next one starts
/// (RandomLauncher.OnSessionStarted). Without a RandomLauncher, paths are still recorded but
/// only shown/cleared manually via ShowPaths()/ClearPaths().
///
/// Each path runs from the launch point to where the shot ended: the gate-plane crossing for a
/// goal or miss (the same point as that shot's heatmap dot), or the stick for a save. A save also
/// stores a separate projected tail from the stick to the gate plane. Nothing after the end is
/// recorded (the project has no bounce shots). A shot that never ends that way (e.g. it deflects
/// off the goalie's body and despawns) is dropped with a warning, so a session can show fewer
/// paths than shots fired.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class ShotPathRecorder : MonoBehaviour
{
    public enum PathOutcome { Save, Goal, Miss }

    public struct ShotPath
    {
        /// <summary>Launch → end (gate plane, or the stick for a save). World space.</summary>
        public Vector3[] points;

        /// <summary>Saves only: stick → gate plane (an arc under gravity, else two points).
        /// Empty otherwise.</summary>
        public Vector3[] projectedTail;

        public PathOutcome outcome;

        /// <summary>Quadrant of the gate-plane point (where the shot crossed, or was headed).</summary>
        public Quadrant quadrant;
    }

    [Header("Sampling")]
    [Tooltip("A new point is only added once the ball has moved at least this far (meters) " +
             "from the last one.")]
    [Min(0f)]
    public float minPointSpacing = 0.02f;

    [Tooltip("Safety cap on points per shot. Once reached, only the final end point is still added.")]
    [Min(2)]
    public int maxPointsPerShot = 256;

    [Tooltip("Most paths kept at once; the oldest is dropped past this. Sessions clear the list " +
             "anyway — this bounds it when there's no RandomLauncher to do that.")]
    [Min(1)]
    public int maxPaths = 64;

    [Tooltip("Log each finished path's point count (useful while tuning minPointSpacing).")]
    public bool logPointCounts = true;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;
    private FloorBoundary _floorBoundary;

    private readonly List<ShotPath> _paths = new List<ShotPath>();
    private readonly List<Vector3> _current = new List<Vector3>();
    private bool _recording = false;
    private bool _pathsShown = false;

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Finished paths for the current (or just-ended) session, in shot order.</summary>
    public IReadOnlyList<ShotPath> Paths => _paths;

    /// <summary>True between ShowPaths() and the next ClearPaths(), so a display that becomes
    /// active late (and missed OnPathsReady) can still draw them.</summary>
    public bool PathsShown => _pathsShown;

    /// <summary>Fired by ShowPaths() — at session end when a RandomLauncher is present.</summary>
    public event Action<IReadOnlyList<ShotPath>> OnPathsReady;

    /// <summary>Fired by ClearPaths() — at session start when a RandomLauncher is present.</summary>
    public event Action OnPathsCleared;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
        _launcher = GetComponent<RandomLauncher>();
        _floorBoundary = GetComponent<FloorBoundary>();
    }

    void OnEnable()
    {
        _goalDetector.OnShotLaunched += HandleShotLaunched;
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;
        _goalDetector.OnSaved += HandleSaved;

        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += HandleDespawned;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted += HandleSessionStarted;
            _launcher.OnSessionEnded += HandleSessionEnded;
        }
    }

    void OnDisable()
    {
        _goalDetector.OnShotLaunched -= HandleShotLaunched;
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        _goalDetector.OnSaved -= HandleSaved;

        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;

        if (_launcher != null)
        {
            _launcher.OnSessionStarted -= HandleSessionStarted;
            _launcher.OnSessionEnded -= HandleSessionEnded;
        }
    }

    /// <summary>LateUpdate rather than Update: by now GoalDetector.Update has already run this
    /// frame, so on the crossing frame ShotInFlight is false and the post-crossing position is
    /// never sampled — the path ends exactly on the event's gate-plane point. A save is
    /// registered from a physics trigger, which also runs before this.</summary>
    void LateUpdate()
    {
        if (!_recording || !_goalDetector.ShotInFlight) return;

        AddPoint(transform.position);
    }

    // ── Recording ────────────────────────────────────────────────

    private void HandleShotLaunched()
    {
        // A shot that never ended (e.g. relaunched mid-flight) is dropped rather than kept half-done.
        AbandonOpenPath("a new shot was launched");
        _recording = true;

        // RandomLauncher moves the ball to its launch origin before calling OnBallLaunched().
        _current.Add(transform.position);
    }

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        if (!_recording) return;

        // GoalScored is set before OnPlaneCrossed fires, so it reflects this exact crossing.
        PathOutcome outcome = _goalDetector.GoalScored ? PathOutcome.Goal : PathOutcome.Miss;
        FinishPath(crossingPos, Array.Empty<Vector3>(), crossingPos, outcome);
    }

    /// <summary>OnSaved and OnPlaneCrossed are mutually exclusive per shot, so each path is
    /// finished once.</summary>
    private void HandleSaved(Vector3 projectedPos)
    {
        if (!_recording) return;

        Vector3 contact = _goalDetector.SaveContactPosition;
        Vector3[] tail;
        if ((projectedPos - contact).sqrMagnitude <= 0.0001f)
            tail = Array.Empty<Vector3>();
        else if (_goalDetector.SaveUsedGravity)
            tail = SampleArcTail(contact, _goalDetector.SaveContactVelocity, projectedPos);
        else
            tail = new[] { contact, projectedPos };

        FinishPath(contact, tail, projectedPos, PathOutcome.Save);
    }

    /// <summary>Under gravity the projected tail is the parabola from the stick to the gate
    /// plane (the same one GoalDetector projected the heatmap point along), sampled by time so
    /// points land about minPointSpacing apart, capped at maxPointsPerShot. Ends exactly on
    /// <paramref name="projectedPos"/>.</summary>
    private Vector3[] SampleArcTail(Vector3 contact, Vector3 velocity, Vector3 projectedPos)
    {
        Vector3 gravity = Physics.gravity;
        if (!QuadrantMath.BallisticPlaneCrossing(contact, velocity, gravity,
                                                 _goalDetector.goalGateCenter.z, out _, out float time))
            return new[] { contact, projectedPos };

        // The chord slightly undercounts the arc's length; close enough for spacing.
        float length = (projectedPos - contact).magnitude;
        int segments = minPointSpacing > 0f
            ? Mathf.CeilToInt(length / minPointSpacing)
            : maxPointsPerShot - 1;
        segments = Mathf.Clamp(segments, 1, maxPointsPerShot - 1);

        var tail = new Vector3[segments + 1];
        for (int i = 0; i < segments; i++)
        {
            float t = time * i / segments;
            tail[i] = contact + velocity * t + 0.5f * gravity * t * t;
        }
        tail[segments] = projectedPos;

        return tail;
    }

    /// <summary>The ball despawned with its shot still open: it never crossed the gate plane
    /// and wasn't saved (e.g. it deflected off the goalie's body and dropped). Stop sampling
    /// now instead of recording it bouncing around until the next launch.</summary>
    private void HandleDespawned()
    {
        AbandonOpenPath("the ball despawned before crossing the gate plane or being saved");
    }

    /// <summary>Drops the shot being recorded, if any, with a warning — the session will show
    /// one fewer path than shots fired, and this says why.</summary>
    private void AbandonOpenPath(string reason)
    {
        if (_recording)
            Debug.LogWarning($"[ShotPathRecorder] Dropped an unfinished path ({_current.Count} points): {reason}.");

        _recording = false;
        _current.Clear();
    }

    private void AddPoint(Vector3 point)
    {
        if (_current.Count >= maxPointsPerShot) return;

        Vector3 last = _current[_current.Count - 1];
        if ((point - last).sqrMagnitude < minPointSpacing * minPointSpacing) return;

        _current.Add(point);
    }

    private void FinishPath(Vector3 endPoint, Vector3[] tail, Vector3 gatePlanePos, PathOutcome outcome)
    {
        _recording = false;

        // The end point always goes in, replacing the last sample if the cap was hit.
        if (_current.Count >= maxPointsPerShot)
            _current[_current.Count - 1] = endPoint;
        else
            _current.Add(endPoint);

        var path = new ShotPath
        {
            points = _current.ToArray(),
            projectedTail = tail,
            outcome = outcome,
            quadrant = QuadrantOf(gatePlanePos),
        };
        if (_paths.Count >= maxPaths)
            _paths.RemoveAt(0);
        _paths.Add(path);
        _current.Clear();

        if (logPointCounts)
            Debug.Log($"[ShotPathRecorder] Path #{_paths.Count}: {path.points.Length} points, " +
                      $"{outcome}, {path.quadrant}, ends at ({endPoint.x:F2}, {endPoint.y:F2}, {endPoint.z:F2})");
    }

    /// <summary>Same normalization as ShotHeatmapRecorder (QuadrantMath.NormalizeToGate), so a
    /// path's quadrant always matches its heatmap dot's.</summary>
    private Quadrant QuadrantOf(Vector3 gatePlanePos)
    {
        Vector2 normalized = QuadrantMath.NormalizeToGate(
            _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, gatePlanePos);

        return QuadrantMath.BucketFromNormalized(normalized);
    }

    private void HandleSessionStarted()
    {
        ClearPaths();
    }

    private void HandleSessionEnded()
    {
        // Covers the last shot when RandomLauncher's despawn handler (which ends the session)
        // runs before HandleDespawned.
        AbandonOpenPath("the session ended before the shot finished");
        ShowPaths();
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Hands the recorded paths to listeners (OnPathsReady).</summary>
    public void ShowPaths()
    {
        _pathsShown = true;
        OnPathsReady?.Invoke(_paths);
    }

    /// <summary>Clears all recorded paths (and any shot being recorded) and tells listeners to
    /// hide them (OnPathsCleared).</summary>
    public void ClearPaths()
    {
        _paths.Clear();
        _current.Clear();
        _recording = false;
        _pathsShown = false;

        OnPathsCleared?.Invoke();
    }
}
