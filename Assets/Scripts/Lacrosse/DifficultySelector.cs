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
///   - Remembered between runs unless Remember Selection is off. With a PlayerProfileStore,
///     each player keeps their own (PlayerProfile.difficulty, Player_Session_History_Plan.md
///     step 5): switching player switches the level, and a player who never picked one keeps
///     the current level. Without a store, one choice is saved for everyone in PlayerPrefs.
///     That old shared PlayerPrefs choice is still read once at startup if the selected player
///     has none yet, so an existing headset keeps its level.
///
/// Setup: add to the ball or a "Difficulty" GameObject. The launcher, crowd and store are found
/// automatically if left empty (assumes one of each in the scene).
/// </summary>
public class DifficultySelector : MonoBehaviour
{
    [Tooltip("The levels and their shot settings, one per difficulty. Starting values — tune " +
             "after play-testing.")]
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

    [Tooltip("Optional. Leave empty to find the PlayerProfileStore in the scene. With one, each " +
             "player keeps their own difficulty; without, one choice is saved for everyone.")]
    public PlayerProfileStore players;

    private const string PrefsKey = "Difficulty.Selected";

    private bool _loaded;

    /// <summary>Fired after a new difficulty is applied (and once at startup).</summary>
    public event Action<Difficulty> OnDifficultyChanged;

    /// <summary>The current difficulty. Reading it loads the saved choice first, so it's right
    /// no matter which script's Awake runs first.</summary>
    public Difficulty Selected
    {
        get
        {
            LoadSelection();
            return selected;
        }
    }

    /// <summary>The current level's display name, or "" if it isn't in the list.</summary>
    public string SelectedName => FindLevel(Selected)?.displayName ?? "";

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

        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();

        WarnAboutDuplicates();
        LoadSelection();
    }

    void OnEnable()
    {
        if (players != null)
            players.OnPlayerChanged += HandlePlayerChanged;
    }

    void OnDisable()
    {
        if (players != null)
            players.OnPlayerChanged -= HandlePlayerChanged;
    }

    void Start()
    {
        DifficultyLevel level = FindLevel(selected);
        if (level == null)
        {
            // Selected was removed from the list: fall back to the first level rather than
            // silently running on the launcher's Inspector values with nothing marked.
            level = FirstLevel();
            if (level == null)
            {
                Debug.LogWarning("[DifficultySelector] The Levels list is empty — shots use the " +
                                 "RandomLauncher's Inspector values.");
                return;
            }
            Debug.LogWarning($"[DifficultySelector] No level for {selected} in the Levels list — " +
                             $"using {level.displayName}.");
            selected = level.difficulty;
        }

        level.ApplyTo(launcher, crowd);
        Debug.Log($"[DifficultySelector] Starting on {level.displayName}.");

        // First run with profiles: the selected player takes on the level they're starting at.
        if (players != null && !TryParseLevel(players.CurrentPlayer.difficulty, out _))
            SaveSelection();

        OnDifficultyChanged?.Invoke(selected);
    }

    /// <summary>Switches to the new player's level. One who never picked a level keeps the
    /// current one, which becomes theirs.</summary>
    private void HandlePlayerChanged(PlayerProfile player)
    {
        if (!rememberSelection || player == null) return;

        if (!TryParseLevel(player.difficulty, out Difficulty theirs))
        {
            SaveSelection();
            return;
        }
        if (theirs == selected) return;

        selected = theirs;
        DifficultyLevel level = FindLevel(theirs);
        level.ApplyTo(launcher, crowd);
        Debug.Log($"[DifficultySelector] {player.displayName} plays on {level.displayName}.");
        OnDifficultyChanged?.Invoke(theirs);
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

        LoadSelection();
        selected = difficulty;
        level.ApplyTo(launcher, crowd);
        SaveSelection();

        Debug.Log($"[DifficultySelector] Difficulty set to {level.displayName} — speed {level.launchSpeed} " +
                  $"± {level.launchSpeedVariance} m/s, {level.shotsPerSession} shots.");
        OnDifficultyChanged?.Invoke(difficulty);
        return true;
    }

    /// <summary>The first level with this difficulty (duplicates after it are ignored).</summary>
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

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>Loads the saved choice once (if Remember Selection is on and it's still in the
    /// list): the selected player's, else the shared PlayerPrefs one.</summary>
    private void LoadSelection()
    {
        if (_loaded) return;
        _loaded = true;
        if (!rememberSelection) return;

        PlayerProfile player = players != null ? players.CurrentPlayer : null;
        if (player != null && TryParseLevel(player.difficulty, out Difficulty theirs))
            selected = theirs;
        else if (TryParseLevel(PlayerPrefs.GetString(PrefsKey, ""), out Difficulty shared))
            selected = shared;
    }

    /// <summary>Saves the current choice: on the selected player with a store, else in the
    /// shared PlayerPrefs key. Nothing when Remember Selection is off.</summary>
    private void SaveSelection()
    {
        if (!rememberSelection) return;

        if (players != null)
        {
            players.SetPlayerDifficulty(players.CurrentPlayer, selected.ToString());
            return;
        }

        PlayerPrefs.SetString(PrefsKey, selected.ToString());
        PlayerPrefs.Save();
    }

    /// <summary>A stored difficulty name that's still in the Levels list.</summary>
    private bool TryParseLevel(string stored, out Difficulty difficulty)
    {
        return Enum.TryParse(stored, out difficulty) && FindLevel(difficulty) != null;
    }

    private DifficultyLevel FirstLevel()
    {
        if (levels == null) return null;
        foreach (DifficultyLevel level in levels)
        {
            if (level != null)
                return level;
        }
        return null;
    }

    /// <summary>A duplicated Inspector entry can never be picked (FindLevel returns the first), so say so.</summary>
    private void WarnAboutDuplicates()
    {
        if (levels == null) return;
        var seen = new HashSet<Difficulty>();
        foreach (DifficultyLevel level in levels)
        {
            if (level != null && !seen.Add(level.difficulty))
                Debug.LogWarning($"[DifficultySelector] The Levels list has more than one {level.difficulty} — " +
                                 "only the first is used.");
        }
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
            //                  difficulty             name           speed var  pad   depth  shots crowd
            new DifficultyLevel(Difficulty.HighSchool, "High school", 16f, 1.0f, 0.20f, 0.50f, 8,  0.3f),
            new DifficultyLevel(Difficulty.College,    "College",     21f, 1.5f, 0.08f, 0.65f, 10, 0.6f),
            new DifficultyLevel(Difficulty.Pro,        "Pro",         26f, 2.0f, 0.02f, 0.80f, 12, 1.0f),
        };
    }
}
