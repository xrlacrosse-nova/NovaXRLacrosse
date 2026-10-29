using System.Collections.Generic;
using MagicLeap.Android;
using UnityEngine;
using UnityEngine.XR.MagicLeap;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Voice commands between sessions, so the controller can stay in the stick mount
/// (Plans/Current/Spoken_Summary_And_Voice_Commands_Plan.md, "Voice commands"). Uses Magic Leap's
/// voice intents (MLVoice) with the phrases in voiceConfiguration.
///   - Listens only while no session is running (the settings page is up): started at app
///     start and on OnSessionEnded, stopped on OnSessionStarted, restarted on app resume.
///   - Each command does what the matching click does and gets a short spoken reply.
///   - Our own voice is ignored: events while TextToSpeech is speaking, or within
///     ignoreAfterSpeechSeconds of it finishing, are dropped.
///   - Every voice event is logged ("[VoiceCommands] Event ..."), since intents can fail to
///     fire without any error.
///   - Editor: no voice. Keys stand in: 1/2/3 difficulty, L lifetime, H last session, R repeat.
/// No voice start: sessions start from the gaze target only.
///
/// Setup: add to the "Voice" GameObject next to TextToSpeech and SessionSummarySpeaker, and
/// assign voiceConfiguration (ids must match the constants below). Tick VOICE_INPUT in
/// Manifest Settings, and turn on Settings > Magic Leap Inputs > Voice > Voice Commands on
/// the headset. Everything else is found automatically if left empty.
/// </summary>
[RequireComponent(typeof(TextToSpeech))]
public class VoiceCommands : MonoBehaviour
{
    // Intent ids; must match the Voice Intents Configuration asset.
    public const int HighSchoolId = 1;
    public const int CollegeId = 2;
    public const int ProId = 3;
    public const int LifetimeId = 4;
    public const int LastSessionId = 5;
    public const int RepeatId = 6;

    [Tooltip("Voice Intents Configuration asset (Assets > Create > MagicLeap) with ids 1–6.")]
    public MLVoiceIntentsConfiguration voiceConfiguration;

    [Tooltip("Voice events this soon after our own speech ends are ignored, so a reply or " +
             "summary can't trigger a command.")]
    [Min(0f)]
    public float ignoreAfterSpeechSeconds = 0.5f;

    [Header("References")]
    [Tooltip("Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    [Tooltip("Leave empty to find the DifficultySelector in the scene.")]
    public DifficultySelector difficulty;

    [Tooltip("Leave empty to find the SessionHistory in the scene.")]
    public SessionHistory history;

    [Tooltip("Leave empty to find the PlayerProfileStore in the scene.")]
    public PlayerProfileStore players;

    [Tooltip("Leave empty to use the SessionSummarySpeaker on this GameObject.")]
    public SessionSummarySpeaker summarySpeaker;

    // ── Private ───────────────────────────────────────────────────

    private TextToSpeech _tts;
    private bool _permissionGranted;
    private bool _listening;

    // Voice events can arrive off the main thread; they're handled in Update.
    private readonly Queue<long> _heard = new Queue<long>();
    private readonly object _heardLock = new object();

    private bool SessionRunning => launcher != null && launcher.IsSessionRunning;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _tts = GetComponent<TextToSpeech>();

        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (difficulty == null)
            difficulty = FindFirstObjectByType<DifficultySelector>();
        if (history == null)
            history = FindFirstObjectByType<SessionHistory>();
        if (players == null)
            players = FindFirstObjectByType<PlayerProfileStore>();
        if (summarySpeaker == null)
            summarySpeaker = GetComponent<SessionSummarySpeaker>();
    }

    void Start()
    {
        if (Application.isEditor) return;

        if (voiceConfiguration == null)
        {
            Debug.LogWarning("[VoiceCommands] No Voice Intents Configuration assigned — voice commands off.");
            return;
        }

        Permissions.RequestPermission(MLPermission.VoiceInput, OnPermissionGranted, OnPermissionDenied, OnPermissionDenied);
    }

    void OnEnable()
    {
        MLVoice.OnVoiceEvent += HandleVoiceEvent;
        if (launcher != null)
        {
            launcher.OnSessionStarted += StopListening;
            launcher.OnSessionEnded += StartListening;
        }
    }

    void OnDisable()
    {
        MLVoice.OnVoiceEvent -= HandleVoiceEvent;
        if (launcher != null)
        {
            launcher.OnSessionStarted -= StopListening;
            launcher.OnSessionEnded -= StartListening;
        }
        StopListening();
    }

    void OnApplicationPause(bool paused)
    {
        // Voice processing stops while the app is paused; start it again on resume.
        if (paused)
        {
            StopListening();
        }
        else
        {
            StartListening();
        }
    }

    void Update()
    {
        while (true)
        {
            long id;
            lock (_heardLock)
            {
                if (_heard.Count == 0) break;
                id = _heard.Dequeue();
            }
            HandleCommand(id);
        }

#if UNITY_EDITOR
        // Editor stand-ins for the voice phrases.
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || SessionRunning) return;

        if (keyboard.digit1Key.wasPressedThisFrame) HandleCommand(HighSchoolId);
        else if (keyboard.digit2Key.wasPressedThisFrame) HandleCommand(CollegeId);
        else if (keyboard.digit3Key.wasPressedThisFrame) HandleCommand(ProId);
        else if (keyboard.lKey.wasPressedThisFrame) HandleCommand(LifetimeId);
        else if (keyboard.hKey.wasPressedThisFrame) HandleCommand(LastSessionId);
        else if (keyboard.rKey.wasPressedThisFrame) HandleCommand(RepeatId);
#endif
    }

    // ── Listening ─────────────────────────────────────────────────

    private void OnPermissionGranted(string permission)
    {
        if (permission != MLPermission.VoiceInput) return;
        _permissionGranted = true;
        StartListening();
    }

    private void OnPermissionDenied(string permission)
    {
        if (permission == MLPermission.VoiceInput)
            Debug.LogWarning("[VoiceCommands] Voice input permission denied — voice commands off.");
    }

    private void StartListening()
    {
        if (_listening || !_permissionGranted || voiceConfiguration == null || SessionRunning) return;

        if (!MLVoice.VoiceEnabled)
        {
            Debug.LogWarning("[VoiceCommands] Voice input is off on the headset (Settings > Magic Leap " +
                             "Inputs > Voice > Voice Commands) — voice commands off.");
            return;
        }

        MLResult result = MLVoice.SetupVoiceIntents(voiceConfiguration);
        if (!result.IsOk)
        {
            Debug.LogWarning($"[VoiceCommands] Couldn't start voice intents ({result}).");
            return;
        }

        _listening = true;
        Debug.Log("[VoiceCommands] Listening.");
    }

    private void StopListening()
    {
        if (!_listening) return;

        MLVoice.Stop();
        _listening = false;
        Debug.Log("[VoiceCommands] Stopped listening.");
    }

    private void HandleVoiceEvent(in bool wasSuccessful, in MLVoice.IntentEvent voiceEvent)
    {
        Debug.Log($"[VoiceCommands] Event: success {wasSuccessful}, state {voiceEvent.State}, " +
                  $"id {voiceEvent.EventID}, \"{voiceEvent.EventName}\"");
        if (!wasSuccessful) return;

        lock (_heardLock)
            _heard.Enqueue((long)voiceEvent.EventID);
    }

    // ── Commands ──────────────────────────────────────────────────

    private void HandleCommand(long id)
    {
        if (SessionRunning) return;

        if (_tts.IsSpeaking || Time.time - _tts.LastSpokeTime < ignoreAfterSpeechSeconds)
        {
            Debug.Log($"[VoiceCommands] Ignored command {id} (our own speech).");
            return;
        }

        switch (id)
        {
            case HighSchoolId: SelectDifficulty(Difficulty.HighSchool); break;
            case CollegeId: SelectDifficulty(Difficulty.College); break;
            case ProId: SelectDifficulty(Difficulty.Pro); break;
            case LifetimeId: ShowLifetime(); break;
            case LastSessionId: ShowLastSession(); break;
            case RepeatId: RepeatSummary(); break;
            default:
                Debug.LogWarning($"[VoiceCommands] Unknown intent id {id} — check the configuration asset.");
                break;
        }
    }

    private void SelectDifficulty(Difficulty level)
    {
        if (difficulty == null)
        {
            Debug.LogWarning("[VoiceCommands] No DifficultySelector in the scene.");
            return;
        }

        // Already on that level still replies, so the player knows they were heard.
        if (difficulty.SetDifficulty(level))
            _tts.Speak($"{difficulty.FindLevel(level).displayName} selected");
    }

    private void ShowLifetime()
    {
        if (history == null || players == null) return;

        PlayerProfile player = players.CurrentPlayer;
        if (history.ListSessions(player).Count == 0)
            _tts.Speak("No sessions yet");
        else if (history.ReplayLifetime(player))
            _tts.Speak("Showing lifetime");
    }

    private void ShowLastSession()
    {
        if (history == null || players == null) return;

        IReadOnlyList<SessionSummary> sessions = history.ListSessions(players.CurrentPlayer);
        if (sessions.Count == 0)
            _tts.Speak("No sessions yet");
        else if (history.Replay(sessions[0]))
            _tts.Speak("Showing last session");
    }

    private void RepeatSummary()
    {
        if (summarySpeaker == null || !summarySpeaker.Repeat())
            _tts.Speak("No summary yet");
    }
}
