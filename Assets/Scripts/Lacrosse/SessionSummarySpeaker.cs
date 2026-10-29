using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Speaks a short recap after each saved session (Plans/Past/Spoken_Summary_And_Voice_Commands_Plan.md,
/// "Post-session summary"). Spoken only; nothing is shown on the board.
///   - Listens to SessionLogger.OnSessionSaved, not OnSessionEnded, so the player's index
///     already lists this session. No PlayerProfileStore = nothing saved = no summary.
///   - Says it after speakDelay, so it doesn't land on the last shot's haptics / crowd.
///   - Up to three sentences (BuildSummary): the total, the toughest (or best) corner this
///     session, and a trend once the player has 3+ sessions.
///   - Repeat() says the last summary again. It's cleared, and any speech stopped, when a
///     session starts or the player changes.
///
/// Setup: add to the "Voice" GameObject next to TextToSpeech. Everything else is found
/// automatically if left empty.
/// </summary>
[RequireComponent(typeof(TextToSpeech))]
public class SessionSummarySpeaker : MonoBehaviour
{
    [Tooltip("Seconds after the session ends before the summary is spoken.")]
    [Min(0f)]
    public float speakDelay = 1f;

    [Tooltip("The trend sentence needs this many points of change in save % vs. the recent average.")]
    [Min(0f)]
    public float trendPoints = 10f;

    [Header("References")]
    [Tooltip("Leave empty to find the SessionLogger in the scene.")]
    public SessionLogger sessionLogger;

    [Tooltip("Leave empty to find the SessionHistory in the scene (minCornerShots, trendSessions, " +
             "the player's sessions).")]
    public SessionHistory history;

    [Tooltip("Leave empty to find the PlayerProfileStore in the scene.")]
    public PlayerProfileStore players;

    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    /// <summary>The last summary spoken for the current player, or null.</summary>
    public string LastSummary { get; private set; }

    // ── Private ───────────────────────────────────────────────────

    // Fewer sessions than this (this one included) = no trend sentence.
    private const int MinTrendSessions = 3;

    private TextToSpeech _tts;
    private Coroutine _pending;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _tts = GetComponent<TextToSpeech>();

        if (sessionLogger == null)
            sessionLogger = FindFirstObjectByType<SessionLogger>();
        if (sessionLogger == null)
            Debug.LogWarning("[SessionSummarySpeaker] No SessionLogger found — no summaries.");

        if (history == null)
            history = FindFirstObjectByType<SessionHistory>();
        if (history == null)
            Debug.LogWarning("[SessionSummarySpeaker] No SessionHistory found — no summaries.");

        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
    }

    void OnEnable()
    {
        if (sessionLogger != null)
            sessionLogger.OnSessionSaved += HandleSessionSaved;
        if (launcher != null)
            launcher.OnSessionStarted += Clear;
        if (players != null)
            players.OnPlayerChanged += HandlePlayerChanged;
    }

    void OnDisable()
    {
        if (sessionLogger != null)
            sessionLogger.OnSessionSaved -= HandleSessionSaved;
        if (launcher != null)
            launcher.OnSessionStarted -= Clear;
        if (players != null)
            players.OnPlayerChanged -= HandlePlayerChanged;
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Says the last summary again. False (nothing said) when there isn't one.</summary>
    public bool Repeat()
    {
        if (string.IsNullOrEmpty(LastSummary))
            return false;

        CancelPending();
        _tts.Speak(LastSummary);
        return true;
    }

    // ── Events ────────────────────────────────────────────────────

    private void HandleSessionSaved(SessionRecord session)
    {
        if (history == null || players == null) return;

        // Player changes are refused mid-session, so the saved session is the current player's.
        IReadOnlyList<SessionSummary> sessions = history.ListSessions(players.CurrentPlayer);
        if (sessions.Count == 0 || sessions[0].sessionId != session.sessionId)
        {
            Debug.LogWarning("[SessionSummarySpeaker] The saved session isn't the newest in the " +
                             "player's index — no summary.");
            return;
        }

        string summary = BuildSummary(sessions, history.minCornerShots, history.trendSessions, trendPoints);
        if (summary.Length == 0) return;

        LastSummary = summary;
        CancelPending();
        _pending = StartCoroutine(SpeakAfterDelay(summary));
    }

    private void HandlePlayerChanged(PlayerProfile _) => Clear();

    /// <summary>Session start or player change: the summary belonged to the last session / player.</summary>
    private void Clear()
    {
        LastSummary = null;
        CancelPending();
        _tts.Stop();
    }

    private IEnumerator SpeakAfterDelay(string summary)
    {
        yield return new WaitForSeconds(speakDelay);
        _pending = null;
        _tts.Speak(summary);
    }

    private void CancelPending()
    {
        if (_pending == null) return;
        StopCoroutine(_pending);
        _pending = null;
    }

    // ── Summary rules ─────────────────────────────────────────────

    /// <summary>
    /// The spoken summary for the newest session in <paramref name="newestFirst"/> (the player's
    /// sessions, this one first). Empty if it has no shots.
    ///   1. Total: "You saved 8 of 12." / "You saved all 12. Perfect session."
    ///   2. Toughest corner (lowest save rate among corners with at least minCornerShots shots,
    ///      below the overall rate): "Bottom left was toughest, 1 of 4." If none, a corner with
    ///      at least minCornerShots shots and every one saved: "Top right, 3 of 3."
    ///   3. With 3+ sessions, first match: the same toughest corner in each of the last 3 or
    ///      more sessions ("Bottom left has been lowest for 3 sessions."), or save % at least
    ///      trendPoints above / below the average of the previous sessions within trendSessions
    ///      ("That's up from 60 percent recently.").
    /// </summary>
    public static string BuildSummary(IReadOnlyList<SessionSummary> newestFirst, int minCornerShots,
                                      int trendSessions, float trendPoints)
    {
        if (newestFirst == null || newestFirst.Count == 0) return "";
        SessionSummary current = newestFirst[0];
        if (current.shots == 0) return "";

        var text = new StringBuilder();

        // 1. Total.
        if (current.saves == current.shots)
            text.Append($"You saved all {current.shots}. Perfect session.");
        else
            text.Append($"You saved {current.saves} of {current.shots}.");

        // 2. Corner.
        if (TryToughestCorner(current, minCornerShots, out Quadrant toughest))
        {
            int q = (int)toughest;
            text.Append($" {ShotHeatmapRecorder.PositionName(toughest)} was toughest, " +
                        $"{current.savesPerQuadrant[q]} of {current.shotsPerQuadrant[q]}.");
        }
        else if (TryPerfectCorner(current, minCornerShots, out Quadrant best))
        {
            int q = (int)best;
            text.Append($" {ShotHeatmapRecorder.PositionName(best)}, " +
                        $"{current.savesPerQuadrant[q]} of {current.shotsPerQuadrant[q]}.");
        }

        // 3. Trend.
        if (newestFirst.Count >= MinTrendSessions)
        {
            string trend = TrendSentence(newestFirst, minCornerShots, trendSessions, trendPoints);
            if (trend.Length > 0)
                text.Append(' ').Append(trend);
        }

        return text.ToString();
    }

    /// <summary>The lowest-save-rate corner with at least minCornerShots shots, if it's below
    /// the session's overall rate. Ties go to more shots, then Quadrant order.</summary>
    private static bool TryToughestCorner(SessionSummary s, int minCornerShots, out Quadrant corner)
    {
        corner = Quadrant.TopLeft;
        if (s.shots == 0) return false;

        double overall = (double)s.saves / s.shots;
        bool found = false;
        double lowest = 0;

        for (int q = 0; q < 4; q++)
        {
            int shots = s.shotsPerQuadrant[q];
            if (shots < minCornerShots) continue;

            double rate = (double)s.savesPerQuadrant[q] / shots;
            if (!found || rate < lowest || (rate == lowest && shots > s.shotsPerQuadrant[(int)corner]))
            {
                found = true;
                corner = (Quadrant)q;
                lowest = rate;
            }
        }

        return found && lowest < overall;
    }

    /// <summary>A corner with at least minCornerShots shots, every one saved. Ties go to more
    /// shots, then Quadrant order.</summary>
    private static bool TryPerfectCorner(SessionSummary s, int minCornerShots, out Quadrant corner)
    {
        corner = Quadrant.TopLeft;
        bool found = false;

        for (int q = 0; q < 4; q++)
        {
            int shots = s.shotsPerQuadrant[q];
            if (shots < minCornerShots || s.savesPerQuadrant[q] != shots) continue;

            if (!found || shots > s.shotsPerQuadrant[(int)corner])
            {
                found = true;
                corner = (Quadrant)q;
            }
        }

        return found;
    }

    private static string TrendSentence(IReadOnlyList<SessionSummary> newestFirst, int minCornerShots,
                                        int trendSessions, float trendPoints)
    {
        // Same toughest corner, session after session, counting back from this one.
        if (TryToughestCorner(newestFirst[0], minCornerShots, out Quadrant corner))
        {
            int streak = 1;
            while (streak < newestFirst.Count &&
                   TryToughestCorner(newestFirst[streak], minCornerShots, out Quadrant earlier) &&
                   earlier == corner)
            {
                streak++;
            }

            if (streak >= MinTrendSessions)
                return $"{ShotHeatmapRecorder.PositionName(corner)} has been lowest for {streak} sessions.";
        }

        // This session's save % vs. the average of the previous ones in the trend window.
        int window = Mathf.Min(newestFirst.Count, trendSessions);
        double sum = 0;
        int counted = 0;
        for (int i = 1; i < window; i++)
        {
            SessionSummary s = newestFirst[i];
            if (s.shots == 0) continue;
            sum += 100.0 * s.saves / s.shots;
            counted++;
        }
        if (counted == 0) return "";

        double recent = sum / counted;
        double now = 100.0 * newestFirst[0].saves / newestFirst[0].shots;
        int recentRounded = Mathf.RoundToInt((float)recent);

        if (now - recent >= trendPoints)
            return $"That's up from {recentRounded} percent recently.";
        if (recent - now >= trendPoints)
            return $"That's down from {recentRounded} percent recently.";
        return "";
    }
}
