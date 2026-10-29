using UnityEngine;

/// <summary>
/// Spoken output for the headset, using Android's built-in text-to-speech engine
/// (android.speech.tts.TextToSpeech) through JNI. Free and offline; nothing leaves the headset.
/// Used by the post-session summary and the voice command replies
/// (Plans/Past/Spoken_Summary_And_Voice_Commands_Plan.md).
///
///   - Speak(text) cuts off anything already being said and says text. Called before the
///     engine has started, the latest text is kept and spoken once it's ready.
///   - IsSpeaking / LastSpokeTime let voice input ignore our own voice.
///   - Editor: nothing is spoken. Each line is logged as "[TTS] ..." and IsSpeaking stays true
///     for a time based on the text length, so the timing still behaves like the headset.
///
/// When speech ends is found by polling the engine's isSpeaking() while a line plays: the
/// engine's progress listener is an abstract Java class, which AndroidJavaProxy can't implement.
///
/// Setup: attach to the "Voice" GameObject. To check speech on the headset, tick
/// speakTestOnStart, build, and listen for testPhrase at app start; untick it afterwards.
/// </summary>
public class TextToSpeech : MonoBehaviour
{
    [Tooltip("Speech speed. 1 = the engine's normal rate.")]
    [Range(0.5f, 2f)]
    public float speechRate = 1f;

    [Tooltip("Says testPhrase once the engine is ready. For checking on the headset that " +
             "speech works; turn off afterwards.")]
    public bool speakTestOnStart = false;

    [Tooltip("Said at app start when speakTestOnStart is on.")]
    public string testPhrase = "Speech is working. You saved 8 of 12.";

    [Tooltip("Seconds to wait for the engine to start before warning that there is no " +
             "text-to-speech engine on this device.")]
    public float initTimeout = 5f;

    [Tooltip("Editor only: how many characters per second the fake speech 'reads', which sets " +
             "how long IsSpeaking stays true.")]
    public float editorCharsPerSecond = 15f;

    /// <summary>True once the engine has started (always true in the Editor).</summary>
    public bool IsReady { get; private set; }

    /// <summary>True from Speak() until the line finishes or Stop() is called.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>Time.time when the last line finished or was stopped.</summary>
    public float LastSpokeTime { get; private set; } = float.NegativeInfinity;

    // ── Private ───────────────────────────────────────────────────

    private string _pendingText;  // asked for before the engine was ready
    private float _speakStartTime;
    private bool _failed;         // no engine; lines are only logged

#if UNITY_ANDROID && !UNITY_EDITOR
    private const int Success = 0;      // TextToSpeech.SUCCESS
    private const int QueueFlush = 0;   // TextToSpeech.QUEUE_FLUSH
    private const int NoStatus = int.MinValue;

    // Seconds after speak() to wait for audio to start before treating the line as done
    // (an engine that never reports isSpeaking() shouldn't block voice input forever).
    private const float AudioStartTimeout = 3f;

    private AndroidJavaObject _tts;
    private InitListener _initListener; // held so the proxy isn't garbage collected
    private volatile int _initStatus = NoStatus; // written on the Android UI thread
    private float _initStartTime;
    private bool _sawAudio;
    private int _utteranceCount;

    private class InitListener : AndroidJavaProxy
    {
        private readonly TextToSpeech _owner;

        public InitListener(TextToSpeech owner)
            : base("android.speech.tts.TextToSpeech$OnInitListener")
        {
            _owner = owner;
        }

        public void onInit(int status)
        {
            _owner._initStatus = status;
        }
    }
#else
    private float _editorDuration;
#endif

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _initStartTime = Time.realtimeSinceStartup;
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _initListener = new InitListener(this);
                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, _initListener);
            }
        }
        catch (System.Exception e)
        {
            Fail($"Couldn't create the Android text-to-speech engine: {e.Message}");
        }
#else
        IsReady = true;
#endif
    }

    void Start()
    {
        if (speakTestOnStart)
            Speak(testPhrase);
    }

    void Update()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady && !_failed)
            PollInit();

        if (IsSpeaking)
        {
            bool playing = _tts.Call<bool>("isSpeaking");
            if (playing)
                _sawAudio = true;
            else if (_sawAudio || Time.time - _speakStartTime > AudioStartTimeout)
                FinishSpeaking();
        }
#else
        if (IsSpeaking && Time.time - _speakStartTime >= _editorDuration)
            FinishSpeaking();
#endif
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
            Stop();
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_tts != null)
        {
            _tts.Call<int>("stop");
            _tts.Call("shutdown");
            _tts.Dispose();
            _tts = null;
        }
#endif
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Says <paramref name="text"/>, cutting off anything already being said.
    /// </summary>
    public void Speak(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (_failed)
        {
            Debug.Log($"[TTS] (no engine) {text}");
            return;
        }

        if (!IsReady)
        {
            _pendingText = text;
            return;
        }

        Debug.Log($"[TTS] {text}");
        IsSpeaking = true;
        _speakStartTime = Time.time;

#if UNITY_ANDROID && !UNITY_EDITOR
        _sawAudio = false;
        try
        {
            _tts.Call<int>("setSpeechRate", speechRate);
            using (var parameters = new AndroidJavaObject("android.os.Bundle"))
            {
                int result = _tts.Call<int>("speak", text, QueueFlush, parameters,
                                            "nova" + (++_utteranceCount));
                if (result != Success)
                {
                    Debug.LogWarning($"[TTS] speak() returned {result}; line not spoken.");
                    FinishSpeaking();
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[TTS] speak() failed: {e.Message}");
            FinishSpeaking();
        }
#else
        _editorDuration = Mathf.Max(0.5f, text.Length / Mathf.Max(1f, editorCharsPerSecond));
#endif
    }

    /// <summary>Stops the current line (and drops one waiting for the engine).</summary>
    public void Stop()
    {
        _pendingText = null;
        if (!IsSpeaking)
            return;

#if UNITY_ANDROID && !UNITY_EDITOR
        _tts.Call<int>("stop");
#endif
        FinishSpeaking();
    }

    // ── Internals ─────────────────────────────────────────────────

    void FinishSpeaking()
    {
        IsSpeaking = false;
        LastSpokeTime = Time.time;
    }

    void Fail(string message)
    {
        _failed = true;
        _pendingText = null;
        Debug.LogWarning($"[TTS] {message} Spoken lines will only be logged.");
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void PollInit()
    {
        int status = _initStatus;
        if (status == NoStatus)
        {
            if (Time.realtimeSinceStartup - _initStartTime > initTimeout)
                Fail($"No text-to-speech engine started within {initTimeout} s.");
            return;
        }

        if (status != Success)
        {
            Fail($"Text-to-speech engine failed to start (status {status}).");
            return;
        }

        IsReady = true;
        string engine = _tts.Call<string>("getDefaultEngine");
        Debug.Log($"[TTS] Ready (engine: {engine}).");

        if (_pendingText != null)
        {
            string text = _pendingText;
            _pendingText = null;
            Speak(text);
        }
    }
#endif
}
