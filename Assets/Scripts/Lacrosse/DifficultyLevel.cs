using UnityEngine;

/// <summary>The levels a goalie can pick on the settings page. See
/// Plans/Current/Difficulty_Progression_Plan.md.</summary>
public enum Difficulty
{
    HighSchool,
    College,
    Pro
}

/// <summary>
/// One difficulty's shot settings. A plain serializable class (not a ScriptableObject), so the
/// levels live in DifficultySelector's Inspector list with defaults set in code.
///
/// ApplyTo() snapshot-copies the values onto the launcher and crowd once, when the choice is
/// made. Nothing reads them live, so a running session can't be affected.
/// </summary>
[System.Serializable]
public class DifficultyLevel
{
    public Difficulty difficulty;

    [Tooltip("Shown on the settings page, in the countdown (GET READY · College) and under the " +
             "heatmap headline.")]
    public string displayName;

    [Tooltip("Release speed (m/s). With BallFlight drag on, the ball arrives ~6-7% slower.")]
    [Min(1f)]
    public float launchSpeed;

    [Tooltip("Each shot's speed is Launch Speed ± this much (m/s), picked at random.")]
    [Min(0f)]
    public float launchSpeedVariance;

    [Tooltip("RandomInQuadrant aim: inset from each quadrant edge. Less = tighter corners.")]
    [Range(0f, 0.45f)]
    public float edgePadding;

    [Tooltip("FixedPoint aim: 0 = quadrant center, 1 = corner. More = tighter corners.")]
    [Range(0f, 1f)]
    public float quadrantDepth;

    [Tooltip("Seconds between shots, picked at random in this range.")]
    [Min(0f)]
    public float minShotInterval;
    [Min(0f)]
    public float maxShotInterval;

    [Min(1)]
    public int shotsPerSession;

    [Tooltip("Crowd cheer/boo volume. 0 = silent.")]
    [Range(0f, 1f)]
    public float crowdVolume;

    public DifficultyLevel() { }

    public DifficultyLevel(Difficulty difficulty, string displayName, float launchSpeed,
                           float launchSpeedVariance, float edgePadding, float quadrantDepth,
                           float minShotInterval, float maxShotInterval, int shotsPerSession,
                           float crowdVolume)
    {
        this.difficulty = difficulty;
        this.displayName = displayName;
        this.launchSpeed = launchSpeed;
        this.launchSpeedVariance = launchSpeedVariance;
        this.edgePadding = edgePadding;
        this.quadrantDepth = quadrantDepth;
        this.minShotInterval = minShotInterval;
        this.maxShotInterval = maxShotInterval;
        this.shotsPerSession = shotsPerSession;
        this.crowdVolume = crowdVolume;
    }

    /// <summary>Copies every value onto the launcher (and the crowd, if there is one).</summary>
    public void ApplyTo(RandomLauncher launcher, CrowdAmbience crowd)
    {
        if (launcher != null)
        {
            launcher.launchSpeed = launchSpeed;
            launcher.launchSpeedVariance = launchSpeedVariance;
            launcher.edgePadding = edgePadding;
            launcher.quadrantDepth = quadrantDepth;
            launcher.minShotInterval = Mathf.Min(minShotInterval, maxShotInterval);
            launcher.maxShotInterval = Mathf.Max(minShotInterval, maxShotInterval);
            launcher.shotsPerSession = Mathf.Max(1, shotsPerSession);
            launcher.readyLabel = displayName;
        }

        if (crowd != null)
            crowd.volume = crowdVolume;
    }
}
