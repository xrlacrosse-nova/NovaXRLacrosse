using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds the difficulty the goalie picked (High school, College, Pro) and copies its values onto
/// the RandomLauncher and CrowdAmbience. The choice is fixed: it never changes based on how the
/// goalie does. See Plans/Current/Difficulty_Progression_Plan.md.
///   - Picked on the settings page's DIFFICULTY column (SettingsPage calls SetDifficulty), or
///     with this component's context menu in the Editor. No UI of its own.
///   - Refused while a session is running (the page is hidden then anyway).
///   - Remembered between runs in PlayerPrefs unless Remember Selection is off.
///
/// Setup: add to the ball or a "Difficulty" GameObject. The launcher and crowd are found
/// automatically if left empty (assumes one of each in the scene).
/// </summary>
public class DifficultySelector : MonoBehaviour
{
    [Tooltip("The levels and their shot settings. Starting values — tune after play-testing.")]
    public List<DifficultyLevel> levels = DefaultLevels();

    [Tooltip("The difficulty at startup (or when Remember Selection is off / nothing is saved).")]
    public Difficulty selected = Difficulty.College;

    [Tooltip("Save the last choice so the headset opens on it. Untick while tuning so every Play " +
             "starts at Selected.")]
    public bool rememberSelection = true;

    [Header("References")]
    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    [Tooltip("Optional. Leave empty to find the CrowdAmbience in the scene.")]
    public CrowdAmbience crowd;

    private const string PrefsKey = "Difficulty.Selected";

    /// <summary>Fired after a new difficulty is applied (and once at startup).</summary>
    public event Action<Difficulty> OnDifficultyChanged;

    public Difficulty Selected => selected;

    public IReadOnlyList<DifficultyLevel> Levels => levels;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher == null)
            Debug.LogWarning("[DifficultySelector] No RandomLauncher found — difficulty won't change the shots.");

        if (crowd == null)
            crowd = FindFirstObjectByType<CrowdAmbience>();

        // Loaded in Awake so Selected is right before the settings page reads it.
        if (rememberSelection &&
            Enum.TryParse(PlayerPrefs.GetString(PrefsKey, ""), out Difficulty saved) &&
            FindLevel(saved) != null)
        {
            selected = saved;
        }
    }

    void Start()
    {
        DifficultyLevel level = FindLevel(selected);
        if (level == null)
        {
            Debug.LogWarning($"[DifficultySelector] No level for {selected} in the Levels list — shots " +
                             "use the RandomLauncher's Inspector values.");
            return;
        }

        level.ApplyTo(launcher, crowd);
        Debug.Log($"[DifficultySelector] Starting on {level.displayName}.");
        OnDifficultyChanged?.Invoke(selected);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Applies and saves <paramref name="difficulty"/>. Returns false (and changes
    /// nothing) while a session is running or if the level isn't in the list.</summary>
    public bool SetDifficulty(Difficulty difficulty)
    {
        if (launcher != null && launcher.IsSessionRunning)
        {
            Debug.Log($"[DifficultySelector] Can't change to {difficulty} during a session — refused.");
            return false;
        }

        DifficultyLevel level = FindLevel(difficulty);
        if (level == null)
        {
            Debug.LogWarning($"[DifficultySelector] No level for {difficulty} in the Levels list.");
            return false;
        }

        selected = difficulty;
        level.ApplyTo(launcher, crowd);

        if (rememberSelection)
        {
            PlayerPrefs.SetString(PrefsKey, difficulty.ToString());
            PlayerPrefs.Save();
        }

        Debug.Log($"[DifficultySelector] Difficulty set to {level.displayName} — speed {level.launchSpeed} " +
                  $"± {level.launchSpeedVariance} m/s, {level.shotsPerSession} shots, " +
                  $"{level.minShotInterval}-{level.maxShotInterval} s apart.");
        OnDifficultyChanged?.Invoke(difficulty);
        return true;
    }

    public DifficultyLevel FindLevel(Difficulty difficulty)
    {
        if (levels == null) return null;
        foreach (DifficultyLevel level in levels)
        {
            if (level != null && level.difficulty == difficulty)
                return level;
        }
        return null;
    }

    // ── Editor ────────────────────────────────────────────────────

    [ContextMenu("Set High school")]
    private void SetHighSchool() => SetDifficulty(Difficulty.HighSchool);

    [ContextMenu("Set College")]
    private void SetCollege() => SetDifficulty(Difficulty.College);

    [ContextMenu("Set Pro")]
    private void SetPro() => SetDifficulty(Difficulty.Pro);

    // ── Defaults ──────────────────────────────────────────────────

    /// <summary>The plan's starting table. Speeds are kept under real shot speeds until fast
    /// shots are confirmed readable on device.</summary>
    private static List<DifficultyLevel> DefaultLevels()
    {
        return new List<DifficultyLevel>
        {
            //                  difficulty             name           speed var  pad   depth  interval   shots crowd
            new DifficultyLevel(Difficulty.HighSchool, "High school", 16f, 1.0f, 0.20f, 0.50f, 7f, 10f, 8,  0.3f),
            new DifficultyLevel(Difficulty.College,    "College",     21f, 1.5f, 0.08f, 0.65f, 5f, 8f,  10, 0.6f),
            new DifficultyLevel(Difficulty.Pro,        "Pro",         26f, 2.0f, 0.02f, 0.80f, 4f, 6f,  12, 1.0f),
        };
    }
}
