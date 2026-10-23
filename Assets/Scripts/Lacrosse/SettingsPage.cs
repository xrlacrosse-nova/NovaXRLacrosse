using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// The settings board to the left of the ML Rig (see Plans/Current/Input_Styles_Plan.md),
/// clicked with the controller ray (ControllerRayPointer) while the controller is out of the
/// stick mount. Shows one page at a time on the same board:
///   - Main: PLAYER column (one row per player, the selected one marked, "+ New player", and
///     Rename / Delete) and the DIFFICULTY column (one row per level, the current one marked)
///     when a DifficultySelector is in the scene. Without a PlayerProfileStore the player column
///     is just "+ New player".
///   - New player: on-screen keyboard and name field. Only shown while naming (or renaming) a player.
///   - History: the selected player's saved sessions, newest first, paged with older / newer,
///     and their recent trend. Clicking a session replays it on the heatmap and as path streaks
///     (clicking it again, or BACK, returns to the live readout). Opened with
///     "View &lt;player&gt;'s history", which only shows with a SessionHistory in the scene.
///
/// Visible at app start and after OnSessionEnded, hidden on OnSessionStarted, so nothing on it
/// can be clicked (or changed) mid-session.
///
/// Setup: create "SettingsPage" under WorldOrigin, ~1.5 m left of the rig, facing the goalie,
/// with a world-space Canvas child (e.g. 800 x 600 at scale 0.001 = 0.8 x 0.6 m). All buttons
/// and labels are built on that Canvas at runtime.
/// </summary>
public class SettingsPage : MonoBehaviour
{
    public enum Page
    {
        Main,
        NewPlayer,
        History
    }

    [Header("Board")]
    [Tooltip("World-space Canvas the pages are built on. Leave empty to use the Canvas in this " +
             "GameObject's children.")]
    public Canvas board;

    [Tooltip("Margin around the board (fraction of its size) where the controller laser already " +
             "shows, so it appears just before reaching the page.")]
    [Range(0f, 1f)]
    public float laserMargin = 0.2f;

    [Header("Layout (canvas units)")]
    [Tooltip("Keyboard key size. 55 = 5.5 cm at a 0.001 canvas scale.")]
    [Min(10f)]
    public float keySize = 55f;

    [Tooltip("Gap between keys and buttons.")]
    [Min(0f)]
    public float gap = 8f;

    [Tooltip("Font size for buttons and labels.")]
    [Min(4f)]
    public float fontSize = 28f;

    [Header("Players")]
    [Tooltip("Seconds Delete waits for the second, confirming click before it reverts.")]
    [Min(0.5f)]
    public float deleteConfirmSeconds = 3f;

    [Header("References")]
    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    [Tooltip("Leave empty to find the DifficultySelector in the scene. None = no DIFFICULTY column.")]
    public DifficultySelector difficulty;

    [Tooltip("Leave empty to find the PlayerProfileStore in the scene. None = no player list, " +
             "Rename or Delete.")]
    public PlayerProfileStore players;

    [Tooltip("Leave empty to find the SessionHistory in the scene. None = no history button or page.")]
    public SessionHistory history;

    /// <summary>Name rules: 1–12 characters, A–Z, 0–9 and space, trimmed.</summary>
    public const int MaxNameLength = PlayerProfileStore.MaxNameLength;

    private const float BackdropAlpha = 0.35f;

    private const string KeyRow1 = "QWERTYUIOP";
    private const string KeyRow2 = "ASDFGHJKL";
    private const string KeyRow3 = "ZXCVBNM";
    private const string KeyRow4 = "1234567890";

    // ── Private ───────────────────────────────────────────────────

    private RectTransform _boardRect;
    private bool _canHide = true;
    private RectTransform _mainPage;
    private RectTransform _newPlayerPage;
    private TextMeshProUGUI _nameField;
    private TextMeshProUGUI _nameMessage;
    private readonly List<(Difficulty difficulty, RayButton button)> _difficultyRows =
        new List<(Difficulty, RayButton)>();
    private TextMeshProUGUI _difficultyMessage;

    // player column (only with a PlayerProfileStore)
    private RectTransform _playerRows;          // rebuilt when the list changes or scrolls
    private readonly List<(PlayerProfile player, RayButton button)> _playerButtons =
        new List<(PlayerProfile, RayButton)>();
    private Vector2 _playerRowsTopLeft;
    private float _playerColumnWidth;
    private int _playerCapacity;                // rows that fit, "+ New player" included
    private int _playerScroll;                  // first row shown
    private RayButton _scrollUp;
    private RayButton _scrollDown;
    private RayButton _deleteButton;
    private TextMeshProUGUI _playerMessage;
    private PlayerProfile _deleteArmedFor;      // first Delete click waiting for the second
    private float _deleteArmedUntil;
    private RayButton _viewHistoryButton;

    // history page (only with a PlayerProfileStore and SessionHistory)
    private RectTransform _historyPage;
    private TextMeshProUGUI _historyTitle;
    private TextMeshProUGUI _historyPageLabel;  // "(2 / 5)"
    private TextMeshProUGUI _historyTrend;
    private RectTransform _historyRows;         // rebuilt on open and on paging
    private readonly List<(SessionSummary session, RayButton button)> _historyButtons =
        new List<(SessionSummary, RayButton)>();
    private readonly List<SessionSummary> _historySessions = new List<SessionSummary>();
    private RayButton _lifetimeButton;          // first row on every page, while there are sessions
    private string _lifetimeText = "";
    private Vector2 _historyRowsTopLeft;
    private float _historyRowWidth;
    private int _historyCapacity;               // session rows per page
    private int _historyPageIndex;              // 0 = newest
    private RayButton _olderButton;
    private RayButton _newerButton;

    private Page _page;
    private readonly StringBuilder _name = new StringBuilder();
    private PlayerProfile _renaming; // null = adding a new player

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher == null)
            Debug.LogWarning("[SettingsPage] No RandomLauncher found — the page won't hide during sessions.");

        if (difficulty == null)
            difficulty = FindFirstObjectByType<DifficultySelector>();

        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();

        if (history == null)
            history = FindFirstObjectByType<SessionHistory>();

        if (board == null)
            board = GetComponentInChildren<Canvas>(true);
        if (board == null)
        {
            Debug.LogWarning("[SettingsPage] No world-space Canvas assigned or found in children — no page to build.");
            enabled = false;
            return;
        }

        _boardRect = (RectTransform)board.transform;

        // Hiding the board must not hide this GameObject, or OnDisable would stop it listening
        // for OnSessionEnded and the page would never come back.
        _canHide = !transform.IsChildOf(board.transform);
        if (!_canHide)
            Debug.LogWarning("[SettingsPage] The Canvas is on SettingsPage itself (or a parent) — the page " +
                             "can't hide during sessions. Move the Canvas to a child GameObject.");
        board.gameObject.SetActive(true); // build while active so RayButtons size their colliders

        AddLaserCollider();
        BuildBackdrop();
        BuildMainPage();
        BuildNewPlayerPage();
        BuildHistoryPage();
        ShowPage(Page.Main);
    }

    void Start()
    {
        SetShown(launcher == null || !launcher.IsSessionRunning);
        RefreshDifficultyRows();
        RefreshPlayerRows();
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
        if (difficulty != null)
            difficulty.OnDifficultyChanged += HandleDifficultyChanged;
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
        if (difficulty != null)
            difficulty.OnDifficultyChanged -= HandleDifficultyChanged;
        if (players != null)
        {
            players.OnPlayerChanged -= HandlePlayerChanged;
            players.OnPlayersChanged -= HandlePlayersChanged;
        }
    }

    void Update()
    {
        if (_deleteArmedFor != null && Time.time > _deleteArmedUntil)
            DisarmDelete();
    }

    // ── Session events ────────────────────────────────────────────

    /// <summary>The session start closes any replay (the heatmap and paths clear), so the board
    /// comes back on Main rather than on a History page pointing at it.</summary>
    void HandleSessionStarted()
    {
        DisarmDelete();
        if (_page == Page.History)
            ShowPage(Page.Main);
        SetShown(false);
    }

    void HandleSessionEnded() => SetShown(true);

    void HandleDifficultyChanged(Difficulty _) => RefreshDifficultyRows();

    /// <summary>Only rebuilds if the selected row had to scroll into view: a row click lands
    /// here mid-click, and just re-marking keeps that button alive.</summary>
    void HandlePlayerChanged(PlayerProfile _)
    {
        DisarmDelete();
        int scroll = _playerScroll;
        ScrollToSelected();
        if (_playerScroll != scroll)
            RebuildPlayerRows();
        else
            RefreshPlayerRows();
        RefreshViewHistoryLabel();
    }

    void HandlePlayersChanged()
    {
        RebuildPlayerRows();
        RefreshViewHistoryLabel();
    }

    private void SetShown(bool shown)
    {
        if (board != null && _canHide)
            board.gameObject.SetActive(shown);
    }

    // ── Pages ─────────────────────────────────────────────────────

    public Page CurrentPage => _page;

    public void ShowPage(Page page)
    {
        _page = page;
        _mainPage.gameObject.SetActive(page == Page.Main);
        _newPlayerPage.gameObject.SetActive(page == Page.NewPlayer);
        if (_historyPage != null)
            _historyPage.gameObject.SetActive(page == Page.History);
    }

    /// <summary>Opens the keyboard with an empty name, to add a player.</summary>
    public void OpenNewPlayer() => OpenKeyboard(null);

    /// <summary>Opens the keyboard with <paramref name="player"/>'s name filled in, to rename them.</summary>
    public void OpenRename(PlayerProfile player) => OpenKeyboard(player);

    private void OpenKeyboard(PlayerProfile renaming)
    {
        DisarmDelete();
        _renaming = renaming;
        _name.Clear();
        if (renaming != null)
        {
            string current = renaming.displayName ?? "";
            _name.Append(current.Length > MaxNameLength ? current.Substring(0, MaxNameLength) : current);
        }
        _nameMessage.text = "";
        RefreshNameField();
        ShowPage(Page.NewPlayer);
    }

    // ── Keyboard ──────────────────────────────────────────────────

    private void TypeChar(char c)
    {
        if (_name.Length >= MaxNameLength)
        {
            _nameMessage.text = $"Max {MaxNameLength} characters";
            return;
        }
        _name.Append(c);
        _nameMessage.text = "";
        RefreshNameField();
    }

    private void Backspace()
    {
        if (_name.Length > 0)
            _name.Length--;
        _nameMessage.text = "";
        RefreshNameField();
    }

    /// <summary>A refused name (empty, duplicate, session running) keeps the keyboard open
    /// with the reason on its message line.</summary>
    private void Save()
    {
        string playerName = _name.ToString().Trim();
        if (playerName.Length == 0)
        {
            _nameMessage.text = "Type a name first";
            return;
        }

        if (players == null)
        {
            Debug.Log($"[SettingsPage] Name saved: \"{playerName}\" — no PlayerProfileStore in the scene.");
            ShowPage(Page.Main);
            return;
        }

        if (!players.ValidateName(playerName, _renaming, out string reason))
        {
            _nameMessage.text = reason;
            return;
        }

        bool saved = _renaming != null ? players.Rename(_renaming, playerName) : players.AddPlayer(playerName);
        if (!saved)
        {
            _nameMessage.text = "Can't change that now";
            return;
        }
        ShowPage(Page.Main);
    }

    private void Cancel() => ShowPage(Page.Main);

    private void RefreshNameField()
    {
        _nameField.text = $"Name: {_name}_";
    }

    // ── Players ───────────────────────────────────────────────────

    /// <summary>Row click. A refused change says so on the page instead of doing nothing.</summary>
    private void PickPlayer(PlayerProfile player)
    {
        DisarmDelete();
        bool changed = players.Select(player);
        _playerMessage.text = changed ? "" : "Can't change that now";
        RefreshPlayerRows();
    }

    private void RenameSelected() => OpenRename(players.CurrentPlayer);

    /// <summary>First click arms it for deleteConfirmSeconds; a second click within that time
    /// deletes the selected player and all their saved sessions. Otherwise it reverts.</summary>
    private void DeleteSelected()
    {
        PlayerProfile player = players.CurrentPlayer;
        if (_deleteArmedFor != player)
        {
            _deleteArmedFor = player;
            _deleteArmedUntil = Time.time + deleteConfirmSeconds;
            _deleteButton.Text = "Confirm";
            _playerMessage.text = $"Delete {player.displayName}? Click again";
            return;
        }

        DisarmDelete();
        string deletedName = player.displayName;
        _playerMessage.text = players.Delete(player) ? $"Deleted {deletedName}" : "Can't change that now";
    }

    private void DisarmDelete()
    {
        if (_deleteArmedFor == null) return;

        _deleteArmedFor = null;
        if (_deleteButton != null)
            _deleteButton.Text = "Delete";
        if (_playerMessage != null)
            _playerMessage.text = "";
    }

    private void ScrollPlayers(int delta)
    {
        _playerScroll += delta;
        RebuildPlayerRows();
    }

    /// <summary>Scrolls just enough that the selected player's row is on the board, e.g. a
    /// new player added at the bottom of a long list.</summary>
    private void ScrollToSelected()
    {
        if (players == null || _playerCapacity <= 0) return;

        int index = IndexOf(players.CurrentPlayer);
        if (index < 0) return;
        if (index < _playerScroll)
            _playerScroll = index;
        else if (index >= _playerScroll + _playerCapacity)
            _playerScroll = index - _playerCapacity + 1;
    }

    private int IndexOf(PlayerProfile player)
    {
        IReadOnlyList<PlayerProfile> list = players.Players;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == player)
                return i;
        }
        return -1;
    }

    /// <summary>Marks the selected player's row. Run when the rows are built, at Start and on
    /// every change.</summary>
    private void RefreshPlayerRows()
    {
        if (players == null) return;
        foreach (var row in _playerButtons)
            row.button.SetSelected(row.player == players.CurrentPlayer);
    }

    /// <summary>Redraws the player rows: one per player, then "+ New player". Once they outgrow
    /// the column, a window of them shows with UP / DN on the right. The scroll buttons live
    /// outside the rebuilt container, so clicking one never destroys it mid-click.</summary>
    private void RebuildPlayerRows()
    {
        if (_playerRows == null) return;

        foreach (Transform child in _playerRows)
            Destroy(child.gameObject);
        _playerButtons.Clear();

        IReadOnlyList<PlayerProfile> list = players.Players;
        int total = list.Count + 1;
        bool scrolling = total > _playerCapacity;
        _playerScroll = scrolling ? Mathf.Clamp(_playerScroll, 0, total - _playerCapacity) : 0;
        _scrollUp.gameObject.SetActive(scrolling);
        _scrollDown.gameObject.SetActive(scrolling);

        float rowWidth = scrolling ? _playerColumnWidth - keySize - gap : _playerColumnWidth;
        int shown = Mathf.Min(total, _playerCapacity);
        for (int i = 0; i < shown; i++)
        {
            int index = _playerScroll + i;
            Vector2 topLeft = _playerRowsTopLeft + new Vector2(0f, i * (keySize + gap));
            Vector2 size = new Vector2(rowWidth, keySize);

            if (index < list.Count)
            {
                PlayerProfile player = list[index];
                RayButton button = CreateButton(_playerRows, player.displayName, topLeft, size,
                                                () => PickPlayer(player), dimWhenIdle: true);
                _playerButtons.Add((player, button));
            }
            else
            {
                CreateButton(_playerRows, "+ New player", topLeft, size, OpenNewPlayer);
            }
        }

        RefreshPlayerRows();
    }

    // ── History ───────────────────────────────────────────────────

    /// <summary>"View &lt;player&gt;'s history": follows the selected player and renames.</summary>
    private void RefreshViewHistoryLabel()
    {
        if (_viewHistoryButton != null)
            _viewHistoryButton.Text = $"View {players.CurrentPlayer.displayName}'s history";
    }

    /// <summary>Opens the History page for the selected player, newest sessions first. The list
    /// is read once here; nothing is saved while the page is up (it's hidden mid-session).</summary>
    private void OpenHistory()
    {
        DisarmDelete();
        PlayerProfile player = players.CurrentPlayer;

        _historySessions.Clear();
        _historySessions.AddRange(history.ListSessions(player));
        _historyPageIndex = 0;
        _historyTitle.text = $"{player.displayName}: History";
        _historyTrend.text = TrendLine(player);
        _lifetimeText = LifetimeLine(player);

        ShowPage(Page.History); // before building, so the rows' RayButtons size their colliders
        RebuildHistoryRows();
    }

    /// <summary>BACK: closes any replay (the last live session's readout comes back) and
    /// returns to Main.</summary>
    private void CloseHistory()
    {
        history.CloseReplay();
        ShowPage(Page.Main);
    }

    private void PageHistory(int delta)
    {
        _historyPageIndex += delta;
        RebuildHistoryRows();
    }

    /// <summary>Session row click: replays it, or closes the replay if it's the one showing.</summary>
    private void PickSession(SessionSummary session)
    {
        if (history.ReplayingSessionId == session.sessionId)
            history.CloseReplay();
        else if (!history.Replay(session))
            _historyTrend.text = "Couldn't open that session";
        RefreshHistoryRows();
    }

    /// <summary>Lifetime row click: shows the all-time gradient, or closes it if it's showing.</summary>
    private void PickLifetime()
    {
        if (history.IsLifetimeShown)
            history.CloseReplay();
        else if (!history.ReplayLifetime(players.CurrentPlayer))
            _historyTrend.text = "Couldn't open Lifetime";
        RefreshHistoryRows();
    }

    /// <summary>Marks the session (or Lifetime) being replayed.</summary>
    private void RefreshHistoryRows()
    {
        if (_lifetimeButton != null)
            _lifetimeButton.SetSelected(history.IsLifetimeShown);
        foreach (var row in _historyButtons)
            row.button.SetSelected(row.session.sessionId == history.ReplayingSessionId);
    }

    /// <summary>Redraws one page: the Lifetime row first (while there are sessions), then session
    /// rows, e.g. "Oct 21  14:30   6 of 8 saved   College". Older / newer only show when there's
    /// a page that way.</summary>
    private void RebuildHistoryRows()
    {
        foreach (Transform child in _historyRows)
            Destroy(child.gameObject);
        _historyButtons.Clear();
        _lifetimeButton = null;

        int count = _historySessions.Count;
        bool hasLifetime = count > 0;
        int sessionRows = Mathf.Max(1, _historyCapacity - (hasLifetime ? 1 : 0));
        int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)sessionRows));
        _historyPageIndex = Mathf.Clamp(_historyPageIndex, 0, pages - 1);

        _historyPageLabel.text = pages > 1 ? $"({_historyPageIndex + 1} / {pages})" : "";
        _olderButton.gameObject.SetActive(_historyPageIndex < pages - 1);
        _newerButton.gameObject.SetActive(_historyPageIndex > 0);

        if (count == 0)
        {
            CreateLabel(_historyRows, "No saved sessions yet", _historyRowsTopLeft,
                        new Vector2(_historyRowWidth, keySize), UiPalette.Smoke, TextAlignmentOptions.Left);
            return;
        }

        float step = keySize + gap;
        _lifetimeButton = CreateButton(_historyRows, _lifetimeText, _historyRowsTopLeft,
                                       new Vector2(_historyRowWidth, keySize), PickLifetime,
                                       dimWhenIdle: true, alignment: TextAlignmentOptions.Left);

        int first = _historyPageIndex * sessionRows;
        for (int i = 0; i < sessionRows && first + i < count; i++)
        {
            SessionSummary session = _historySessions[first + i];
            string level = history.LevelName(session.difficulty);
            string text = $"{SessionHistory.StartLabel(session)}   {session.saves} of {session.shots} saved" +
                          (level.Length > 0 ? $"   {level}" : "");

            Vector2 topLeft = _historyRowsTopLeft + new Vector2(0f, (i + 1) * step);
            RayButton button = CreateButton(_historyRows, text, topLeft, new Vector2(_historyRowWidth, keySize),
                                            () => PickSession(session), dimWhenIdle: true,
                                            alignment: TextAlignmentOptions.Left);
            _historyButtons.Add((session, button));
        }

        RefreshHistoryRows();
    }

    /// <summary>"Last 10 sessions: 63% saved   Best: Top left 80%   Worst: Bottom right 40%".
    /// No '·' between the parts: the default TMP font may lack it.</summary>
    private string TrendLine(PlayerProfile player)
    {
        SessionHistory.Trends t = history.GetTrends(player);
        if (t.shots == 0) return "";

        string line = t.sessions == 1
            ? $"Last session: {Percent(t.saves, t.shots)} saved"
            : $"Last {t.sessions} sessions: {Percent(t.saves, t.shots)} saved";
        if (t.hasBest)
            line += $"   Best: {ShotHeatmapRecorder.PositionName(t.best)} {Mathf.RoundToInt(t.bestRate * 100f)}%";
        if (t.hasWorst)
            line += $"   Worst: {ShotHeatmapRecorder.PositionName(t.worst)} {Mathf.RoundToInt(t.worstRate * 100f)}%";
        return line;
    }

    /// <summary>"Lifetime   87 of 132 saved   14 sessions", from index.json alone.</summary>
    private string LifetimeLine(PlayerProfile player)
    {
        SessionHistory.Lifetime life = history.GetLifetime(player);
        string sessions = life.sessions == 1 ? "1 session" : $"{life.sessions} sessions";
        return $"Lifetime   {life.saves} of {life.shots} saved   {sessions}";
    }

    private static string Percent(int part, int whole) =>
        whole > 0 ? $"{Mathf.RoundToInt(100f * part / whole)}%" : "0%";

    // ── Difficulty ────────────────────────────────────────────────

    /// <summary>Marks the selector's current level, the one place rows get their selected state.
    /// Run when the column is built, at Start and on every change.</summary>
    private void RefreshDifficultyRows()
    {
        if (difficulty == null) return;
        foreach (var row in _difficultyRows)
            row.button.SetSelected(row.difficulty == difficulty.Selected);
    }

    // ── Building ──────────────────────────────────────────────────

    /// <summary>A plate behind every page, so the board reads as one panel instead of loose
    /// labels floating in the air. ML2 is additive: a dim plate shows as faint grey glass.</summary>
    private void BuildBackdrop()
    {
        RectTransform rect = CreatePage("Backdrop");
        Image plate = rect.gameObject.AddComponent<Image>();
        Color c = UiPalette.Smoke;
        c.a = BackdropAlpha;
        plate.color = c;
        plate.raycastTarget = false;
    }

    private void AddLaserCollider()
    {
        // The board's own trigger collider tells ControllerRayPointer the ray is near the page
        // (laser on, nothing hovered). Buttons win over it.
        BoxCollider col = board.gameObject.GetComponent<BoxCollider>();
        if (col == null)
            col = board.gameObject.AddComponent<BoxCollider>();
        col.isTrigger = true;

        Vector2 size = _boardRect.rect.size * (1f + laserMargin);
        col.size = new Vector3(size.x, size.y, 1f);
        col.center = new Vector3((0.5f - _boardRect.pivot.x) * _boardRect.rect.width,
                                 (0.5f - _boardRect.pivot.y) * _boardRect.rect.height, 0f);
    }

    private void BuildMainPage()
    {
        _mainPage = CreatePage("Main");

        float w = BoardWidth;
        float columnWidth = w / 2f - gap * 2f;
        float y = gap * 2f;

        CreateLabel(_mainPage, "PLAYER", new Vector2(gap * 2f, y), new Vector2(columnWidth, fontSize * 1.4f),
                    UiPalette.Chalk, TextAlignmentOptions.Left);
        y += fontSize * 1.4f + gap;

        if (players != null)
            BuildPlayerColumn(gap * 2f, y, columnWidth);
        else
            CreateButton(_mainPage, "+ New player", new Vector2(gap * 2f, y), new Vector2(columnWidth, keySize),
                         OpenNewPlayer);

        BuildDifficultyColumn(w / 2f + gap, columnWidth);

        // Bottom of the right column, level with Rename / Delete.
        if (players != null && history != null)
        {
            float actionY = BoardHeight - gap * 2f - keySize;
            _viewHistoryButton = CreateButton(_mainPage, "View history", new Vector2(w / 2f + gap, actionY),
                                              new Vector2(columnWidth, keySize), OpenHistory);
            // "View <12 chars>'s history" can outgrow the button at full size.
            AutoSize(_viewHistoryButton.label);
            RefreshViewHistoryLabel();
        }
    }

    /// <summary>PLAYER column below its title: the player rows (rebuilt by RebuildPlayerRows),
    /// a message line, and [ Rename ] [ Delete ] along the bottom of the board.</summary>
    private void BuildPlayerColumn(float x, float top, float columnWidth)
    {
        float step = keySize + gap;
        float actionY = BoardHeight - gap * 2f - keySize;
        float messageY = actionY - gap - fontSize * 1.2f;

        _playerRowsTopLeft = new Vector2(x, top);
        _playerColumnWidth = columnWidth;
        _playerCapacity = Mathf.Max(2, Mathf.FloorToInt((messageY - top) / step));

        _playerRows = CreateRect("Player rows", _mainPage);
        Stretch(_playerRows);

        // UP / DN rather than ▲ / ▼: the default TMP font may not have those glyphs.
        float scrollX = x + columnWidth - keySize;
        _scrollUp = CreateButton(_mainPage, "UP", new Vector2(scrollX, top), new Vector2(keySize, keySize),
                                 () => ScrollPlayers(-1));
        _scrollDown = CreateButton(_mainPage, "DN", new Vector2(scrollX, top + (_playerCapacity - 1) * step),
                                   new Vector2(keySize, keySize), () => ScrollPlayers(1));

        _playerMessage = CreateLabel(_mainPage, "", new Vector2(x, messageY),
                                     new Vector2(columnWidth, fontSize * 1.2f), UiPalette.Smoke,
                                     TextAlignmentOptions.Left);
        // "Delete <12 chars>? Click again" can outgrow the column at full size.
        AutoSize(_playerMessage);

        float actionWidth = (columnWidth - gap) / 2f;
        CreateButton(_mainPage, "Rename", new Vector2(x, actionY), new Vector2(actionWidth, keySize),
                     RenameSelected);
        _deleteButton = CreateButton(_mainPage, "Delete", new Vector2(x + actionWidth + gap, actionY),
                                     new Vector2(actionWidth, keySize), DeleteSelected);

        ScrollToSelected();
        RebuildPlayerRows();
    }

    /// <summary>DIFFICULTY column on the right half of the Main page: one pick-one row per level.
    /// Left out entirely when there's no DifficultySelector, so the page can ship without it.</summary>
    private void BuildDifficultyColumn(float x, float columnWidth)
    {
        if (difficulty == null || difficulty.Levels == null) return;

        float y = gap * 2f;
        CreateLabel(_mainPage, "DIFFICULTY", new Vector2(x, y), new Vector2(columnWidth, fontSize * 1.4f),
                    UiPalette.Chalk, TextAlignmentOptions.Left);
        y += fontSize * 1.4f + gap;

        var built = new HashSet<Difficulty>();
        foreach (DifficultyLevel level in difficulty.Levels)
        {
            // A duplicated entry can't be picked (the selector uses the first), so no row for it.
            if (level == null || !built.Add(level.difficulty)) continue;

            Difficulty d = level.difficulty;
            RayButton button = CreateButton(_mainPage, level.displayName, new Vector2(x, y),
                                            new Vector2(columnWidth, keySize),
                                            () => PickDifficulty(d), dimWhenIdle: true);
            _difficultyRows.Add((d, button));
            y += keySize + gap;
        }

        _difficultyMessage = CreateLabel(_mainPage, "", new Vector2(x, y), new Vector2(columnWidth, fontSize * 1.2f),
                                         UiPalette.Smoke, TextAlignmentOptions.Left);
        RefreshDifficultyRows();
    }

    /// <summary>Row click. A refused change (session running, level missing) says so on the
    /// page instead of the click doing nothing.</summary>
    private void PickDifficulty(Difficulty d)
    {
        bool changed = difficulty.SetDifficulty(d);
        _difficultyMessage.text = changed ? "" : "Can't change that now";
        RefreshDifficultyRows();
    }

    private void BuildNewPlayerPage()
    {
        _newPlayerPage = CreatePage("NewPlayer");

        float w = BoardWidth;
        float y = gap * 2f;

        _nameField = CreateLabel(_newPlayerPage, "Name: _", new Vector2(gap * 2f, y),
                                 new Vector2(w - gap * 4f, fontSize * 1.5f), UiPalette.Chalk,
                                 TextAlignmentOptions.Left);
        y += fontSize * 1.5f;

        _nameMessage = CreateLabel(_newPlayerPage, "", new Vector2(gap * 2f, y),
                                   new Vector2(w - gap * 4f, fontSize * 1.2f), UiPalette.Smoke,
                                   TextAlignmentOptions.Left);
        y += fontSize * 1.2f + gap;

        foreach (string row in new[] { KeyRow1, KeyRow2, KeyRow3, KeyRow4 })
        {
            bool withBackspace = row == KeyRow3;
            int count = row.Length + (withBackspace ? 1 : 0);
            float x = RowStart(count * keySize + (count - 1) * gap);

            foreach (char c in row)
            {
                char key = c;
                CreateButton(_newPlayerPage, key.ToString(), new Vector2(x, y), new Vector2(keySize, keySize),
                             () => TypeChar(key));
                x += keySize + gap;
            }

            // DEL rather than ⌫: the default TMP font has no ⌫ glyph.
            if (withBackspace)
                CreateButton(_newPlayerPage, "DEL", new Vector2(x, y), new Vector2(keySize, keySize), Backspace);

            y += keySize + gap;
        }

        // Bottom row: [ SPACE ]   [ CANCEL ] [ SAVE ]
        float spaceWidth = keySize * 4f + gap * 3f;
        float actionWidth = keySize * 2.5f;
        float bottomWidth = spaceWidth + gap * 3f + actionWidth * 2f + gap;
        float bx = RowStart(bottomWidth);

        CreateButton(_newPlayerPage, "SPACE", new Vector2(bx, y), new Vector2(spaceWidth, keySize), () => TypeChar(' '));
        bx += spaceWidth + gap * 3f;
        CreateButton(_newPlayerPage, "CANCEL", new Vector2(bx, y), new Vector2(actionWidth, keySize), Cancel);
        bx += actionWidth + gap;
        CreateButton(_newPlayerPage, "SAVE", new Vector2(bx, y), new Vector2(actionWidth, keySize), Save);
    }

    /// <summary>History page: title and page count on top, one page of session rows, the trend
    /// line, then [ &lt; older ] [ newer &gt; ] ... [ BACK ] along the bottom. Not built without a
    /// PlayerProfileStore and SessionHistory.</summary>
    private void BuildHistoryPage()
    {
        if (players == null || history == null) return;

        _historyPage = CreatePage("History");

        float x = gap * 2f;
        float inner = BoardWidth - gap * 4f;
        float y = gap * 2f;

        _historyTitle = CreateLabel(_historyPage, "", new Vector2(x, y), new Vector2(inner, fontSize * 1.4f),
                                    UiPalette.Chalk, TextAlignmentOptions.Left);
        _historyPageLabel = CreateLabel(_historyPage, "", new Vector2(x, y), new Vector2(inner, fontSize * 1.4f),
                                        UiPalette.Smoke, TextAlignmentOptions.Right);
        y += fontSize * 1.4f + gap;

        float navY = BoardHeight - gap * 2f - keySize;
        float trendY = navY - gap - fontSize * 1.2f;

        _historyRowsTopLeft = new Vector2(x, y);
        _historyRowWidth = inner;
        _historyCapacity = Mathf.Max(1, Mathf.FloorToInt((trendY - y) / (keySize + gap)));

        _historyRows = CreateRect("History rows", _historyPage);
        Stretch(_historyRows);

        _historyTrend = CreateLabel(_historyPage, "", new Vector2(x, trendY), new Vector2(inner, fontSize * 1.2f),
                                    UiPalette.Smoke, TextAlignmentOptions.Left);
        AutoSize(_historyTrend);

        // < and > rather than ◀ / ▶: the default TMP font may not have those glyphs.
        float navWidth = keySize * 3f;
        _olderButton = CreateButton(_historyPage, "< older", new Vector2(x, navY), new Vector2(navWidth, keySize),
                                    () => PageHistory(1));
        _newerButton = CreateButton(_historyPage, "newer >", new Vector2(x + navWidth + gap, navY),
                                    new Vector2(navWidth, keySize), () => PageHistory(-1));
        CreateButton(_historyPage, "BACK", new Vector2(x + inner - navWidth, navY), new Vector2(navWidth, keySize),
                     CloseHistory);
    }

    /// <summary>Shrinks a label's text (down to half size) instead of letting it overflow.</summary>
    private void AutoSize(TMP_Text label)
    {
        label.enableAutoSizing = true;
        label.fontSizeMin = fontSize * 0.5f;
        label.fontSizeMax = fontSize;
    }

    private float BoardWidth => _boardRect.rect.width;

    private float BoardHeight => _boardRect.rect.height;

    private float RowStart(float rowWidth) => (BoardWidth - rowWidth) / 2f;

    /// <summary>A full-board page container.</summary>
    private RectTransform CreatePage(string name)
    {
        RectTransform page = CreateRect($"Page {name}", _boardRect);
        Stretch(page);
        return page;
    }

    /// <summary>Button at <paramref name="topLeft"/> (canvas units from the board's top-left, y down).
    /// <paramref name="dimWhenIdle"/> = a pick-one row (dim until hovered or selected). A
    /// non-centered label gets an inner margin so it doesn't touch the button's edge.</summary>
    private RayButton CreateButton(RectTransform parent, string text, Vector2 topLeft, Vector2 size,
                                   UnityAction onPressed, bool dimWhenIdle = false,
                                   TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        RectTransform rect = CreateRect($"Button {text}", parent);
        PlaceTopLeft(rect, topLeft, size);

        Image background = rect.gameObject.AddComponent<Image>();
        background.raycastTarget = false;

        TextMeshProUGUI label = CreateLabel(rect, text, Vector2.zero, size, UiPalette.Smoke, alignment);
        if (alignment != TextAlignmentOptions.Center)
            label.margin = new Vector4(gap * 2f, 0f, gap * 2f, 0f);

        RayButton button = rect.gameObject.AddComponent<RayButton>();
        button.dimWhenIdle = dimWhenIdle; // before Setup, which draws the idle color
        button.Setup(label, background);
        button.onPressed.AddListener(onPressed);
        return button;
    }

    private TextMeshProUGUI CreateLabel(RectTransform parent, string text, Vector2 topLeft, Vector2 size,
                                        Color color, TextAlignmentOptions alignment)
    {
        RectTransform rect = CreateRect("Label", parent);
        PlaceTopLeft(rect, topLeft, size);

        TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = alignment;
        label.raycastTarget = false;
        return label;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void PlaceTopLeft(RectTransform rect, Vector2 topLeft, Vector2 size)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        rect.sizeDelta = size;
    }
}
