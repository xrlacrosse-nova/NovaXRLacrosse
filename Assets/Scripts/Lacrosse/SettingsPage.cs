using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// The settings board to the left of the ML Rig (see Plans/Current/Input_Styles_Plan.md),
/// clicked with the controller ray (ControllerRayPointer) while the controller is out of the
/// stick mount. Shows one page at a time on the same board:
///   - Main: PLAYER column with "+ New player".
///   - New player: on-screen keyboard and name field. Only shown while naming a player.
/// The player list, Rename/Delete, difficulty and history rows are added once
/// PlayerProfileStore and DifficultySelector exist (Player_Session_History_Plan.md,
/// Difficulty_Progression_Plan.md); until then the board ships without them.
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
        NewPlayer
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

    [Header("References")]
    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    /// <summary>Name rules: 1–12 characters, A–Z, 0–9 and space, trimmed.</summary>
    public const int MaxNameLength = 12;

    /// <summary>
    /// Fired on SAVE with a valid name: (name being renamed, or null for a new player; new name).
    /// PlayerProfileStore will handle this (AddPlayer / Rename), including refusing duplicates.
    /// </summary>
    public event Action<string, string> NameSaved;

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

    private Page _page;
    private readonly StringBuilder _name = new StringBuilder();
    private string _renaming; // null = adding a new player

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (launcher == null)
            Debug.LogWarning("[SettingsPage] No RandomLauncher found — the page won't hide during sessions.");

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
        ShowPage(Page.Main);
    }

    void Start()
    {
        SetShown(launcher == null || !launcher.IsSessionRunning);
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= HandleSessionEnded;
        }
    }

    // ── Session events ────────────────────────────────────────────

    void HandleSessionStarted() => SetShown(false);

    void HandleSessionEnded() => SetShown(true);

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
    }

    /// <summary>Opens the keyboard with an empty name, to add a player.</summary>
    public void OpenNewPlayer() => OpenKeyboard(null);

    /// <summary>Opens the keyboard with <paramref name="playerName"/> filled in, to rename it.</summary>
    public void OpenRename(string playerName) => OpenKeyboard(playerName);

    private void OpenKeyboard(string renaming)
    {
        _renaming = renaming;
        _name.Clear();
        if (renaming != null)
            _name.Append(renaming.Length > MaxNameLength ? renaming.Substring(0, MaxNameLength) : renaming);
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

    private void Save()
    {
        string playerName = _name.ToString().Trim();
        if (playerName.Length == 0)
        {
            _nameMessage.text = "Type a name first";
            return;
        }

        if (NameSaved == null)
            Debug.Log($"[SettingsPage] Name saved: \"{playerName}\" — no PlayerProfileStore listening yet.");
        NameSaved?.Invoke(_renaming, playerName);
        ShowPage(Page.Main);
    }

    private void Cancel() => ShowPage(Page.Main);

    private void RefreshNameField()
    {
        _nameField.text = $"Name: {_name}_";
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

        CreateButton(_mainPage, "+ New player", new Vector2(gap * 2f, y), new Vector2(columnWidth, keySize),
                     OpenNewPlayer);
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

    private float BoardWidth => _boardRect.rect.width;

    private float RowStart(float rowWidth) => (BoardWidth - rowWidth) / 2f;

    /// <summary>A full-board page container.</summary>
    private RectTransform CreatePage(string name)
    {
        RectTransform page = CreateRect($"Page {name}", _boardRect);
        page.anchorMin = Vector2.zero;
        page.anchorMax = Vector2.one;
        page.offsetMin = Vector2.zero;
        page.offsetMax = Vector2.zero;
        return page;
    }

    /// <summary>Button at <paramref name="topLeft"/> (canvas units from the board's top-left, y down).</summary>
    private RayButton CreateButton(RectTransform parent, string text, Vector2 topLeft, Vector2 size,
                                   UnityAction onPressed)
    {
        RectTransform rect = CreateRect($"Button {text}", parent);
        PlaceTopLeft(rect, topLeft, size);

        Image background = rect.gameObject.AddComponent<Image>();
        background.raycastTarget = false;

        TextMeshProUGUI label = CreateLabel(rect, text, Vector2.zero, size, UiPalette.Smoke,
                                            TextAlignmentOptions.Center);

        RayButton button = rect.gameObject.AddComponent<RayButton>();
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

    private static void PlaceTopLeft(RectTransform rect, Vector2 topLeft, Vector2 size)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        rect.sizeDelta = size;
    }
}
