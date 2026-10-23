using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>One player sharing the headset. The id is a GUID and also names the player's data
/// folder, so a rename never moves files.</summary>
[Serializable]
public class PlayerProfile
{
    public string id;
    public string displayName;      // 1–12 chars, letters, digits and spaces
    public string createdUtc;
    public string difficulty;       // last chosen Difficulty enum name, "" until they pick one
}

/// <summary>profiles.json: every player and the one selected last.</summary>
[Serializable]
public class ProfileList
{
    public int schemaVersion = 1;
    public string selectedPlayerId;
    public List<PlayerProfile> players = new List<PlayerProfile>();
}

/// <summary>
/// The players who share the headset, and which one is in it now
/// (Plans/Current/Player_Session_History_Plan.md). One in the scene.
///   - First run (no profiles.json): creates "Player 1" and selects it. The goalie renames it on
///     the settings page.
///   - There is always at least one player, so CurrentPlayer is never null and every saved
///     session has an owner. Deleting the last one leaves a fresh "Player 1".
///   - Every change is refused while a session is running (the settings page is hidden then
///     anyway); this keeps the rule in one place.
///   - profiles.json is saved after each change (tmp-then-replace, SessionFiles.WriteAtomic).
/// No UI of its own: SettingsPage calls Select / AddPlayer / Rename / Delete / ValidateName.
///
/// Setup: add to an empty "Players" GameObject. The launcher is found automatically if left empty.
/// </summary>
public class PlayerProfileStore : MonoBehaviour
{
    [Tooltip("Leave empty to find the RandomLauncher in the scene. Used to refuse changes mid-session.")]
    public RandomLauncher launcher;

    /// <summary>Name rules: 1–12 characters, letters, digits and spaces, trimmed.</summary>
    public const int MaxNameLength = 12;

    public const string FirstPlayerName = "Player 1";

    // ── Private ───────────────────────────────────────────────────

    private ProfileList _list;
    private PlayerProfile _current;

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Every player, in the order they were added. Reading it loads profiles.json
    /// first, so it's right no matter which script's Awake runs first.</summary>
    public IReadOnlyList<PlayerProfile> Players
    {
        get
        {
            EnsureLoaded();
            return _list.players;
        }
    }

    /// <summary>The player in the headset. Never null.</summary>
    public PlayerProfile CurrentPlayer
    {
        get
        {
            EnsureLoaded();
            return _current;
        }
    }

    /// <summary>Fired when a different player is selected (including after the selected one
    /// is deleted).</summary>
    public event Action<PlayerProfile> OnPlayerChanged;

    /// <summary>Fired when the list is edited (added, renamed, deleted), so the page can redraw.</summary>
    public event Action OnPlayersChanged;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher == null)
            Debug.LogWarning("[PlayerProfileStore] No RandomLauncher found — player changes can't be refused mid-session.");

        EnsureLoaded();
        Debug.Log($"[PlayerProfileStore] {_list.players.Count} player(s), {_current.displayName} selected. " +
                  $"Data: {SessionFiles.Root}");
    }

    // ── Changes ───────────────────────────────────────────────────

    /// <summary>Makes <paramref name="player"/> the one in the headset. Returns false while a
    /// session is running or if the player isn't in the list.</summary>
    public bool Select(PlayerProfile player)
    {
        EnsureLoaded();
        if (!CanChange("select a player")) return false;
        if (player == null || !_list.players.Contains(player))
        {
            Debug.LogWarning("[PlayerProfileStore] Select refused: that player isn't in the list.");
            return false;
        }
        if (player == _current) return true;

        _current = player;
        Save();
        Debug.Log($"[PlayerProfileStore] Selected {player.displayName}.");
        OnPlayerChanged?.Invoke(player);
        return true;
    }

    /// <summary>Adds a player named <paramref name="name"/> and selects them. Returns false
    /// while a session is running or if the name breaks the rules (see ValidateName).</summary>
    public bool AddPlayer(string name)
    {
        EnsureLoaded();
        if (!CanChange("add a player")) return false;
        if (!ValidateName(name, null, out string reason))
        {
            Debug.Log($"[PlayerProfileStore] Add refused: {reason}.");
            return false;
        }

        PlayerProfile player = CreateProfile(name.Trim());
        _list.players.Add(player);
        _current = player;
        Save();

        Debug.Log($"[PlayerProfileStore] Added {player.displayName}.");
        OnPlayersChanged?.Invoke();
        OnPlayerChanged?.Invoke(player);
        return true;
    }

    /// <summary>Renames <paramref name="player"/>. The id, and so the history, is kept.
    /// Returns false while a session is running or if the name breaks the rules.</summary>
    public bool Rename(PlayerProfile player, string name)
    {
        EnsureLoaded();
        if (!CanChange("rename a player")) return false;
        if (player == null || !_list.players.Contains(player))
        {
            Debug.LogWarning("[PlayerProfileStore] Rename refused: that player isn't in the list.");
            return false;
        }
        if (!ValidateName(name, player, out string reason))
        {
            Debug.Log($"[PlayerProfileStore] Rename refused: {reason}.");
            return false;
        }

        string oldName = player.displayName;
        player.displayName = name.Trim();
        Save();

        Debug.Log($"[PlayerProfileStore] Renamed {oldName} to {player.displayName}.");
        OnPlayersChanged?.Invoke();
        return true;
    }

    /// <summary>Removes <paramref name="player"/> and their whole data folder. If they were
    /// selected, the first player left is selected. Deleting the last player leaves a fresh
    /// "Player 1" (same as first run). Returns false while a session is running.</summary>
    public bool Delete(PlayerProfile player)
    {
        EnsureLoaded();
        if (!CanChange("delete a player")) return false;
        if (player == null || !_list.players.Contains(player))
        {
            Debug.LogWarning("[PlayerProfileStore] Delete refused: that player isn't in the list.");
            return false;
        }

        DeleteFolder(player);
        _list.players.Remove(player);

        bool selectionChanged = player == _current;
        if (_list.players.Count == 0)
            _list.players.Add(CreateProfile(FirstPlayerName));
        if (selectionChanged)
            _current = _list.players[0];
        Save();

        Debug.Log($"[PlayerProfileStore] Deleted {player.displayName} and their sessions.");
        OnPlayersChanged?.Invoke();
        if (selectionChanged)
            OnPlayerChanged?.Invoke(_current);
        return true;
    }

    /// <summary>Saves <paramref name="player"/>'s difficulty (a Difficulty enum name), for
    /// DifficultySelector. Not a list change, so no event fires. Returns false while a session
    /// is running.</summary>
    public bool SetPlayerDifficulty(PlayerProfile player, string difficulty)
    {
        EnsureLoaded();
        if (!CanChange("change a player's difficulty")) return false;
        if (player == null || !_list.players.Contains(player)) return false;
        if (player.difficulty == difficulty) return true;

        player.difficulty = difficulty;
        Save();
        return true;
    }

    /// <summary>Checks a name against the rules: trimmed, 1–12 characters, letters, digits and
    /// spaces, and not already used by another player (any case). <paramref name="except"/> is
    /// the player being renamed, who may keep their own name. <paramref name="reason"/> is
    /// short enough for the settings page's message line.</summary>
    public bool ValidateName(string name, PlayerProfile except, out string reason)
    {
        EnsureLoaded();
        string trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0)
        {
            reason = "Type a name first";
            return false;
        }
        if (trimmed.Length > MaxNameLength)
        {
            reason = $"Max {MaxNameLength} characters";
            return false;
        }
        foreach (char c in trimmed)
        {
            bool allowed = c == ' ' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!allowed)
            {
                reason = "Letters, numbers and spaces only";
                return false;
            }
        }
        foreach (PlayerProfile player in _list.players)
        {
            if (player != except &&
                string.Equals(player.displayName?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{trimmed} is already a player";
                return false;
            }
        }

        reason = "";
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────

    private bool CanChange(string action)
    {
        if (launcher != null && launcher.IsSessionRunning)
        {
            Debug.Log($"[PlayerProfileStore] Can't {action} during a session — refused.");
            return false;
        }
        return true;
    }

    /// <summary>Loads profiles.json once. A broken file is kept aside as profiles.json.corrupt
    /// (never overwritten blind) and the store starts over with "Player 1".</summary>
    private void EnsureLoaded()
    {
        if (_list != null) return;

        string path = SessionFiles.ProfilesPath;
        if (File.Exists(path) && !SessionFiles.TryRead(path, out _list))
        {
            try
            {
                File.Copy(path, path + ".corrupt", true);
                Debug.LogWarning($"[PlayerProfileStore] profiles.json couldn't be read — kept as " +
                                 $"profiles.json.corrupt, starting with {FirstPlayerName}.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerProfileStore] profiles.json couldn't be read or kept aside ({e.Message}).");
            }
        }

        if (_list == null)
            _list = new ProfileList();
        if (_list.players == null)
            _list.players = new List<PlayerProfile>();

        // The id names a folder that Delete removes, so anything that isn't a plain GUID is
        // dropped rather than trusted as a path.
        int dropped = _list.players.RemoveAll(p => p == null || !Guid.TryParseExact(p.id, "N", out _));
        if (dropped > 0)
            Debug.LogWarning($"[PlayerProfileStore] Skipped {dropped} player(s) with a missing or bad id in profiles.json.");

        bool firstRun = _list.players.Count == 0;
        if (firstRun)
            _list.players.Add(CreateProfile(FirstPlayerName));

        _current = _list.players.Find(p => p.id == _list.selectedPlayerId) ?? _list.players[0];

        if (firstRun || dropped > 0 || _list.selectedPlayerId != _current.id)
            Save();
    }

    private void Save()
    {
        _list.selectedPlayerId = _current.id;
        try
        {
            SessionFiles.WriteAtomic(SessionFiles.ProfilesPath, JsonUtility.ToJson(_list, true));
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlayerProfileStore] Couldn't save profiles.json ({e.Message}).");
        }
    }

    private static PlayerProfile CreateProfile(string name)
    {
        return new PlayerProfile
        {
            id = Guid.NewGuid().ToString("N"),
            displayName = name,
            createdUtc = DateTime.UtcNow.ToString("o"),
            difficulty = "",
        };
    }

    private static void DeleteFolder(PlayerProfile player)
    {
        string folder = SessionFiles.PlayerFolder(player.id);
        if (!Directory.Exists(folder)) return;

        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PlayerProfileStore] Couldn't delete {folder} ({e.Message}).");
        }
    }

    // ── Editor ────────────────────────────────────────────────────

#if UNITY_EDITOR
    [ContextMenu("Open data folder")]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(SessionFiles.Root);
        UnityEditor.EditorUtility.RevealInFinder(SessionFiles.Root);
    }
#endif
}
