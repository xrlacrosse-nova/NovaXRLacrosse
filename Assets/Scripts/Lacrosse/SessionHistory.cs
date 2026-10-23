using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads a player's saved sessions back and replays them (Plans/Current/Player_Session_History_Plan.md,
/// "SessionHistory"). Data + replay only, no UI: the History page on SettingsPage calls this.
///   - ListSessions: the player's sessions from index.json, newest first.
///   - GetTrends: save % over the last N sessions, best and worst corner (a corner needs at
///     least minCornerShots shots in those sessions to count).
///   - GetLifetime: all-time totals, summed from index.json alone.
///   - Replay: loads one session file and shows it on the existing heatmap and as path
///     streaks, as they appeared at its end. CloseReplay brings back the last live session's.
///   - ReplayLifetime: every shot the player has faced as one heatmap gradient (no dots, no
///     streaks, every session weighted equally) with all-time totals.
///   - Feeds the heatmap's All Sessions mode with the player's saved shots (LoadHistory) at
///     startup and on every player change, so it spans app restarts.
/// A replay closes on its own when the next session starts (the heatmap and path recorder
/// clear then) or when a different player is selected. Every saved shot of a player is read
/// at most once until a new session is saved (cached).
///
/// Setup: add to the "Players" GameObject. Everything is found automatically if left empty.
/// </summary>
public class SessionHistory : MonoBehaviour
{
    /// <summary>Recent form for the History page's trend line.</summary>
    public struct Trends
    {
        public int sessions, shots, saves;
        public bool hasBest, hasWorst;
        public Quadrant best, worst;
        public float bestRate, worstRate;   // 0–1
    }

    /// <summary>All-time totals for a player.</summary>
    public struct Lifetime
    {
        public int sessions, shots, saves;
        public int[] shotsPerQuadrant, savesPerQuadrant;
    }

    [Tooltip("How many recent sessions the trend line covers.")]
    [Min(1)]
    public int trendSessions = 10;

    [Tooltip("A corner needs at least this many shots in those sessions to be best or worst, so " +
             "one lucky shot can't make a corner 'best at 100%'.")]
    [Min(1)]
    public int minCornerShots = 2;

    [Header("References")]
    [Tooltip("Leave empty to find the PlayerProfileStore in the scene.")]
    public PlayerProfileStore players;

    [Tooltip("Leave empty to find the RandomLauncher in the scene (its GoalDetector places replays).")]
    public RandomLauncher launcher;

    [Tooltip("Leave empty to find the ShotHeatmapRecorder in the scene.")]
    public ShotHeatmapRecorder heatmap;

    [Tooltip("Leave empty to find the ShotPathVisualizer in the scene.")]
    public ShotPathVisualizer pathVisualizer;

    [Tooltip("Optional. Leave empty to find the DifficultySelector in the scene; names each " +
             "session's level.")]
    public DifficultySelector difficulty;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private string _replayingId;   // sessionId being replayed, null when live
    private bool _lifetimeShown;

    // Every saved shot of one player, oldest first (session = index in index.json).
    private string _cachedPlayerId;
    private List<RecordedShot> _cachedShots;

    /// <summary>The sessionId being replayed, or null when the live readout (or Lifetime) is showing.</summary>
    public string ReplayingSessionId => _replayingId;

    /// <summary>True while the Lifetime view is on the heatmap.</summary>
    public bool IsLifetimeShown => _lifetimeShown;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();
        if (players == null)
            Debug.LogWarning("[SessionHistory] No PlayerProfileStore found — no history to show.");

        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher != null)
            _goalDetector = launcher.GetComponent<GoalDetector>();
        if (_goalDetector == null)
            Debug.LogWarning("[SessionHistory] No RandomLauncher / GoalDetector found — replays can't be placed.");

        if (heatmap == null)
            heatmap = FindFirstObjectByType<ShotHeatmapRecorder>();
        if (pathVisualizer == null)
            pathVisualizer = FindFirstObjectByType<ShotPathVisualizer>();
        if (difficulty == null)
            difficulty = FindFirstObjectByType<DifficultySelector>();
    }

    void Start()
    {
        FeedHeatmapHistory();
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
        if (players != null)
        {
            players.OnPlayerChanged += HandlePlayerChanged;
            players.OnPlayersChanged += HandlePlayersChanged;
        }
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= HandleSessionEnded;
        }
        if (players != null)
        {
            players.OnPlayerChanged -= HandlePlayerChanged;
            players.OnPlayersChanged -= HandlePlayersChanged;
        }
    }

    /// <summary>The heatmap and path recorder clear themselves at session start, which ends
    /// the replay too; only the state here needs resetting.</summary>
    private void HandleSessionStarted()
    {
        _replayingId = null;
        _lifetimeShown = false;
    }

    /// <summary>SessionLogger has just saved a new file, so the cached shots are stale. The
    /// heatmap adds the session to its own All Sessions history, so it isn't reloaded.</summary>
    private void HandleSessionEnded() => _cachedShots = null;

    /// <summary>A replay belongs to the player who was selected; the All Sessions history
    /// switches to the new player's.</summary>
    private void HandlePlayerChanged(PlayerProfile _)
    {
        CloseReplay();
        FeedHeatmapHistory();
    }

    /// <summary>A delete can remove the cached player's files.</summary>
    private void HandlePlayersChanged() => _cachedShots = null;

    // ── Reading ───────────────────────────────────────────────────

    /// <summary>The player's sessions, newest first. Empty for no player or no sessions.</summary>
    public IReadOnlyList<SessionSummary> ListSessions(PlayerProfile player)
    {
        var sessions = new List<SessionSummary>();
        if (player == null) return sessions;

        sessions.AddRange(SessionFiles.LoadIndex(player.id).sessions);
        sessions.RemoveAll(s => s == null);
        sessions.Reverse();
        return sessions;
    }

    /// <summary>Save % over the player's last <paramref name="lastN"/> sessions, and their best
    /// and worst corner by save %. Ties go to the corner with more shots.</summary>
    public Trends GetTrends(PlayerProfile player, int lastN)
    {
        var trends = new Trends();
        var shots = new int[4];
        var saves = new int[4];

        IReadOnlyList<SessionSummary> sessions = ListSessions(player);
        for (int i = 0; i < sessions.Count && i < lastN; i++)
        {
            SessionSummary s = sessions[i];
            trends.sessions++;
            trends.shots += s.shots;
            trends.saves += s.saves;
            AddPerQuadrant(shots, s.shotsPerQuadrant);
            AddPerQuadrant(saves, s.savesPerQuadrant);
        }

        for (int q = 0; q < 4; q++)
        {
            if (shots[q] < minCornerShots) continue;

            float rate = (float)saves[q] / shots[q];
            if (!trends.hasBest || rate > trends.bestRate ||
                (rate == trends.bestRate && shots[q] > shots[(int)trends.best]))
            {
                trends.hasBest = true;
                trends.best = (Quadrant)q;
                trends.bestRate = rate;
            }
            if (!trends.hasWorst || rate < trends.worstRate ||
                (rate == trends.worstRate && shots[q] > shots[(int)trends.worst]))
            {
                trends.hasWorst = true;
                trends.worst = (Quadrant)q;
                trends.worstRate = rate;
            }
        }

        // One counted corner is both; call it best only.
        if (trends.hasWorst && trends.hasBest && trends.worst == trends.best)
            trends.hasWorst = false;

        return trends;
    }

    /// <inheritdoc cref="GetTrends(PlayerProfile, int)"/>
    public Trends GetTrends(PlayerProfile player) => GetTrends(player, trendSessions);

    /// <summary>All-time totals from index.json alone (it already holds per-corner counts), so
    /// no session file is opened.</summary>
    public Lifetime GetLifetime(PlayerProfile player)
    {
        var lifetime = new Lifetime { shotsPerQuadrant = new int[4], savesPerQuadrant = new int[4] };
        foreach (SessionSummary s in ListSessions(player))
        {
            lifetime.sessions++;
            lifetime.shots += s.shots;
            lifetime.saves += s.saves;
            AddPerQuadrant(lifetime.shotsPerQuadrant, s.shotsPerQuadrant);
            AddPerQuadrant(lifetime.savesPerQuadrant, s.savesPerQuadrant);
        }
        return lifetime;
    }

    // ── Replay ────────────────────────────────────────────────────

    /// <summary>Shows a saved session of the selected player on the heatmap and as path
    /// streaks. Returns false (and changes nothing) while a session is running or if the
    /// session file can't be read.</summary>
    public bool Replay(SessionSummary summary)
    {
        if (launcher != null && launcher.IsSessionRunning)
        {
            Debug.Log("[SessionHistory] Can't replay during a session — refused.");
            return false;
        }

        SessionRecord session = LoadSession(players != null ? players.CurrentPlayer : null, summary);
        if (session == null) return false;

        var shots = new List<RecordedShot>(session.shots.Count);
        foreach (ShotRecord shot in session.shots)
            shots.Add(new RecordedShot(shot.resultNormalized, shot.outcome == ShotRecord.Goal, 0));

        if (heatmap != null)
            heatmap.ShowSession(shots, LevelName(session.settings != null ? session.settings.difficulty : ""));
        if (pathVisualizer != null && _goalDetector != null)
            pathVisualizer.ShowPaths(ToWorldPaths(session));

        _replayingId = session.sessionId;
        _lifetimeShown = false;
        Debug.Log($"[SessionHistory] Replaying {summary.file}: {session.saves} of {session.shots.Count} saved.");
        return true;
    }

    /// <summary>Lifetime view: every shot <paramref name="player"/> has faced as one heatmap
    /// gradient, every session weighted equally, all-time totals on the labels. No dots and no
    /// path streaks (hundreds of either would be unreadable). Returns false while a session is
    /// running or if there's no heatmap.</summary>
    public bool ReplayLifetime(PlayerProfile player)
    {
        if (launcher != null && launcher.IsSessionRunning)
        {
            Debug.Log("[SessionHistory] Can't show Lifetime during a session — refused.");
            return false;
        }
        if (heatmap == null || player == null) return false;

        List<RecordedShot> shots = LoadAllShots(player);
        heatmap.ShowLifetime(shots);
        if (pathVisualizer != null)
            pathVisualizer.ShowPaths(null);

        _replayingId = null;
        _lifetimeShown = true;
        Debug.Log($"[SessionHistory] Showing Lifetime for {player.displayName}: {shots.Count} shots.");
        return true;
    }

    /// <summary>Hides the replay (session or Lifetime) and brings back the last live
    /// session's heatmap and path streaks (not an empty view).</summary>
    public void CloseReplay()
    {
        if (_replayingId == null && !_lifetimeShown) return;
        _replayingId = null;
        _lifetimeShown = false;

        if (heatmap != null)
            heatmap.ShowLive();
        if (pathVisualizer != null)
            pathVisualizer.ShowLive();
    }

    // ── Display helpers ───────────────────────────────────────────

    /// <summary>The level's display name ("High school"), or the stored name if that level is
    /// no longer in the selector's list.</summary>
    public string LevelName(string storedDifficulty)
    {
        if (string.IsNullOrEmpty(storedDifficulty)) return "";

        if (difficulty != null && Enum.TryParse(storedDifficulty, out Difficulty d))
        {
            DifficultyLevel level = difficulty.FindLevel(d);
            if (level != null)
                return level.displayName;
        }
        return storedDifficulty;
    }

    /// <summary>The session's start in the headset's local time, e.g. "Oct 21  14:30".</summary>
    public static string StartLabel(SessionSummary summary)
    {
        if (!DateTime.TryParse(summary.startedUtc, CultureInfo.InvariantCulture,
                               DateTimeStyles.RoundtripKind, out DateTime started))
            return "?";

        return started.ToLocalTime().ToString("MMM d  HH:mm", CultureInfo.InvariantCulture);
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>Hands the selected player's saved shots to the heatmap's All Sessions mode,
    /// so its running tally and gradient cover their real history, not just this run.</summary>
    private void FeedHeatmapHistory()
    {
        if (heatmap == null || players == null) return;

        List<RecordedShot> shots = LoadAllShots(players.CurrentPlayer);
        heatmap.LoadHistory(shots);
        Debug.Log($"[SessionHistory] Loaded {shots.Count} saved shots for {players.CurrentPlayer.displayName} " +
                  "into the heatmap's All Sessions history.");
    }

    /// <summary>Every saved shot of <paramref name="player"/>, oldest session first, each
    /// stamped with its session's place in index.json (what All Sessions fades by). Reads
    /// each session file once; the result is cached until a session is saved or a player is
    /// added, renamed or deleted. Unreadable files are skipped (with a warning).</summary>
    private List<RecordedShot> LoadAllShots(PlayerProfile player)
    {
        if (player == null) return new List<RecordedShot>();
        if (_cachedShots != null && _cachedPlayerId == player.id) return _cachedShots;

        var shots = new List<RecordedShot>();
        List<SessionSummary> sessions = SessionFiles.LoadIndex(player.id).sessions;
        int sessionIndex = 0;
        foreach (SessionSummary summary in sessions)
        {
            SessionRecord session = LoadSession(player, summary);
            if (session == null) continue;

            foreach (ShotRecord shot in session.shots)
                shots.Add(new RecordedShot(shot.resultNormalized, shot.outcome == ShotRecord.Goal, sessionIndex));
            sessionIndex++;
        }

        _cachedPlayerId = player.id;
        _cachedShots = shots;
        return shots;
    }

    private static SessionRecord LoadSession(PlayerProfile player, SessionSummary summary)
    {
        if (player == null || summary == null || string.IsNullOrEmpty(summary.file))
            return null;

        // The index is a file on disk: only accept a bare file name, never a path out of the folder.
        if (Path.GetFileName(summary.file) != summary.file)
        {
            Debug.LogWarning($"[SessionHistory] Bad session file name in index.json: \"{summary.file}\".");
            return null;
        }

        string path = Path.Combine(SessionFiles.SessionsFolder(player.id), summary.file);
        if (!SessionFiles.TryRead(path, out SessionRecord session) || session.shots == null)
        {
            Debug.LogWarning($"[SessionHistory] Couldn't load {summary.file} — not replayed.");
            return null;
        }
        return session;
    }

    /// <summary>Saved paths are gate-relative; the gate center is added back so the streaks
    /// line up with the goal where it is now.</summary>
    private List<ShotPathRecorder.ShotPath> ToWorldPaths(SessionRecord session)
    {
        Vector3 center = _goalDetector.goalGateCenter;
        var paths = new List<ShotPathRecorder.ShotPath>(session.shots.Count);

        foreach (ShotRecord shot in session.shots)
        {
            if (shot.path == null || shot.path.Length < 2) continue;   // saved without paths

            paths.Add(new ShotPathRecorder.ShotPath
            {
                points = ToWorld(shot.path, center),
                projectedTail = ToWorld(shot.projectedTail, center),
                outcome = shot.outcome == ShotRecord.Save
                    ? ShotPathRecorder.PathOutcome.Save
                    : ShotPathRecorder.PathOutcome.Goal,
                quadrant = shot.resultQuadrant,
            });
        }
        return paths;
    }

    private static Vector3[] ToWorld(Vector3[] points, Vector3 center)
    {
        if (points == null) return Array.Empty<Vector3>();

        var world = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            world[i] = points[i] + center;
        return world;
    }

    private static void AddPerQuadrant(int[] into, int[] add)
    {
        if (add == null) return;
        for (int q = 0; q < into.Length && q < add.Length; q++)
            into[q] += add[q];
    }
}
