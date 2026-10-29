using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Saves every finished session to the device for the selected player
/// (Plans/Current/Player_Session_History_Plan.md, "SessionLogger"). Attach to the ball, next to
/// RandomLauncher and GoalDetector.
///   - Session start: a new SessionRecord for PlayerProfileStore.CurrentPlayer, with the
///     launcher settings copied.
///   - Each shot: aim, speed and the shooter's hand at launch (RandomLauncher.LastAim*,
///     ShooterAnimationController.CurrentShotRightHanded), the gate-plane point and
///     outcome when it ends (goal: OnPlaneCrossed, save: OnSaved).
///   - Shots the heatmap doesn't record aren't saved either: one that despawns before crossing
///     the gate plane or being saved (e.g. off the goalie's body), or one that crosses outside
///     the gate. So the saved session matches what was shown.
///   - Session end: flight paths are read from ShotPathRecorder.Paths once, here, paired with the
///     shots in order and stored gate-relative. Then the session file and index.json are written.
/// A session that never ends (app closed, Play stopped mid-session) isn't saved. File IO only
/// happens at session end, never mid-shot.
///
/// Setup: add to the ball. The store and difficulty selector are found automatically if left empty.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
[RequireComponent(typeof(RandomLauncher))]
public class SessionLogger : MonoBehaviour
{
    [Tooltip("Leave empty to find the PlayerProfileStore in the scene. None = nothing is saved.")]
    public PlayerProfileStore players;

    [Tooltip("Optional. Leave empty to find the DifficultySelector in the scene; its level is " +
             "saved with each session.")]
    public DifficultySelector difficulty;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private RandomLauncher _launcher;
    private ShotPathRecorder _pathRecorder;   // optional: no paths saved without it
    private FloorBoundary _floorBoundary;

    private SessionRecord _session;           // null outside a session (or with no store)
    private float _sessionStartTime;
    private ShotRecord _pending;              // launched, not ended yet

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
        _launcher = GetComponent<RandomLauncher>();
        _pathRecorder = GetComponent<ShotPathRecorder>();
        _floorBoundary = GetComponent<FloorBoundary>();

        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();
        if (players == null)
            Debug.LogWarning("[SessionLogger] No PlayerProfileStore found — sessions won't be saved.");

        if (difficulty == null)
            difficulty = FindFirstObjectByType<DifficultySelector>();

        if (_pathRecorder == null)
            Debug.LogWarning("[SessionLogger] No ShotPathRecorder on the ball — sessions are saved without flight paths.");
    }

    void OnEnable()
    {
        _launcher.OnSessionStarted += HandleSessionStarted;
        _launcher.OnSessionEnded += HandleSessionEnded;
        _goalDetector.OnShotLaunched += HandleShotLaunched;
        _goalDetector.OnPlaneCrossed += HandlePlaneCrossed;
        _goalDetector.OnSaved += HandleSaved;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned += HandleDespawned;
    }

    void OnDisable()
    {
        _launcher.OnSessionStarted -= HandleSessionStarted;
        _launcher.OnSessionEnded -= HandleSessionEnded;
        _goalDetector.OnShotLaunched -= HandleShotLaunched;
        _goalDetector.OnPlaneCrossed -= HandlePlaneCrossed;
        _goalDetector.OnSaved -= HandleSaved;
        if (_floorBoundary != null)
            _floorBoundary.OnDespawned -= HandleDespawned;
    }

    // ── Session ───────────────────────────────────────────────────

    private void HandleSessionStarted()
    {
        _pending = null;
        _session = null;

        PlayerProfile player = players != null ? players.CurrentPlayer : null;
        if (player == null) return;

        _session = new SessionRecord
        {
            sessionId = Guid.NewGuid().ToString("N"),
            playerId = player.id,
            startedUtc = DateTime.UtcNow.ToString("o"),
            settings = SnapshotSettings(),
        };
        _sessionStartTime = Time.time;
    }

    private void HandleSessionEnded()
    {
        // The last shot's despawn can end the session before HandleDespawned runs.
        _pending = null;
        if (_session == null) return;

        SessionRecord session = _session;
        _session = null;

        session.endedUtc = DateTime.UtcNow.ToString("o");
        foreach (ShotRecord shot in session.shots)
        {
            if (shot.outcome == ShotRecord.Save)
                session.saves++;
            else
                session.goals++;
        }

        AttachPaths(session);

        try
        {
            string file = SessionFiles.SaveSession(session);
            Debug.Log($"[SessionLogger] Saved session for {PlayerName(session.playerId)}: " +
                      $"{session.saves} of {session.shots.Count} saved → {file}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SessionLogger] Couldn't save the session ({e.Message}).");
        }
    }

    private SessionSettings SnapshotSettings()
    {
        return new SessionSettings
        {
            aimMode = _launcher.aimMode.ToString(),
            launchSpeed = _launcher.launchSpeed,
            launchSpeedVariance = _launcher.launchSpeedVariance,
            edgePadding = _launcher.edgePadding,
            quadrantDepth = _launcher.quadrantDepth,
            shotsPerSession = _launcher.shotsPerSession,
            difficulty = difficulty != null ? difficulty.Selected.ToString() : "",
        };
    }

    // ── Shots ─────────────────────────────────────────────────────

    /// <summary>RandomLauncher sets LastAim* and LastLaunchSpeed before GoalDetector fires this.
    /// A shot still pending here never ended, so it's replaced (same rule as ShotPathRecorder).</summary>
    private void HandleShotLaunched()
    {
        if (_session == null) return;

        _pending = new ShotRecord
        {
            index = _session.shots.Count,
            aimedQuadrant = _launcher.LastAimQuadrant,
            aimNormalized = _launcher.LastAimNormalized,
            launchSpeed = _launcher.LastLaunchSpeed,
            launchTime = Time.time - _sessionStartTime,
            shooterHand = ShooterHand(),
        };
    }

    /// <summary>The launched shot's hand, or "" for an instant launch with no shooter avatar.</summary>
    private string ShooterHand()
    {
        ShooterAnimationController shooter = _launcher.shooterAnimation;
        if (shooter == null || !shooter.IsAvailable) return "";
        return shooter.CurrentShotRightHanded ? ShotRecord.RightHand : ShotRecord.LeftHand;
    }

    private void HandlePlaneCrossed(Vector3 crossingPos)
    {
        if (_pending == null) return;

        // Outside the gate: ShotHeatmapRecorder warns and leaves it off the readout, so it isn't
        // saved either (every shot is aimed on target; this is a bug, not a miss).
        if (!_goalDetector.GoalScored)
        {
            _pending = null;
            return;
        }

        FinishShot(crossingPos, ShotRecord.Goal);
    }

    /// <summary>OnSaved and OnPlaneCrossed are mutually exclusive per shot.</summary>
    private void HandleSaved(Vector3 projectedPos)
    {
        if (_pending == null) return;
        FinishShot(projectedPos, ShotRecord.Save);
    }

    /// <summary>Despawned with the shot still open: it never crossed the gate plane and wasn't
    /// saved, so it's dropped (the heatmap and path streaks drop it too).</summary>
    private void HandleDespawned()
    {
        _pending = null;
    }

    private void FinishShot(Vector3 gatePlanePos, string outcome)
    {
        // Same normalization as the heatmap, so the saved point matches its dot.
        Vector2 normalized = QuadrantMath.NormalizeToGate(
            _goalDetector.goalGateCenter, _goalDetector.goalGateHalfSize, gatePlanePos);

        _pending.resultNormalized = normalized;
        _pending.resultQuadrant = QuadrantMath.BucketFromNormalized(normalized);
        _pending.outcome = outcome;

        _session.shots.Add(_pending);
        _pending = null;
    }

    // ── Paths ─────────────────────────────────────────────────────

    /// <summary>Pairs ShotPathRecorder's paths with the saved shots in order. Read once here at
    /// session end, so it doesn't matter which OnSaved / OnPlaneCrossed subscriber ran first.
    /// The recorder keeps off-gate crossings as Miss paths; those have no saved shot and are
    /// skipped. If the rest still don't line up, the session is saved without paths rather
    /// than with the wrong ones.</summary>
    private void AttachPaths(SessionRecord session)
    {
        if (_pathRecorder == null) return;

        var paths = new List<ShotPathRecorder.ShotPath>();
        foreach (ShotPathRecorder.ShotPath path in _pathRecorder.Paths)
        {
            if (path.outcome != ShotPathRecorder.PathOutcome.Miss)
                paths.Add(path);
        }

        if (paths.Count != session.shots.Count)
        {
            Debug.LogWarning($"[SessionLogger] {paths.Count} paths for {session.shots.Count} shots — " +
                             "saving the session without paths.");
            return;
        }

        for (int i = 0; i < paths.Count; i++)
        {
            bool pathSaved = paths[i].outcome == ShotPathRecorder.PathOutcome.Save;
            bool shotSaved = session.shots[i].outcome == ShotRecord.Save;
            if (pathSaved != shotSaved || paths[i].quadrant != session.shots[i].resultQuadrant)
            {
                Debug.LogWarning($"[SessionLogger] Path {i + 1} doesn't match shot {i + 1} — " +
                                 "saving the session without paths.");
                return;
            }
        }

        Vector3 center = _goalDetector.goalGateCenter;
        for (int i = 0; i < paths.Count; i++)
        {
            session.shots[i].path = ToGateRelative(paths[i].points, center);
            session.shots[i].projectedTail = ToGateRelative(paths[i].projectedTail, center);
        }
    }

    /// <summary>The gate plane is the world Z plane, so gate-relative is just minus its center.</summary>
    private static Vector3[] ToGateRelative(Vector3[] points, Vector3 center)
    {
        if (points == null) return Array.Empty<Vector3>();

        var relative = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            relative[i] = points[i] - center;
        return relative;
    }

    private string PlayerName(string playerId)
    {
        if (players != null)
        {
            foreach (PlayerProfile player in players.Players)
            {
                if (player.id == playerId)
                    return player.displayName;
            }
        }
        return playerId;
    }
}
