using UnityEngine;

/// <summary>
/// Crowd audio for a training session. One looping clip is always playing from the start of the
/// pre-start countdown until the session ends:
///   - Cheer: starts when the session starts (RandomLauncher.OnSessionStarted) and plays until a
///     goal is scored.
///   - Boos: replaces the cheer when a shot scores (GoalDetector.OnGoalScored) and keeps playing
///     until a save (GoalDetector.OnSaved) switches back to the cheer.
///   - Silence: the audio stops when the session ends (RandomLauncher.OnSessionEnded).
///
/// Setup: attach to its own GameObject (e.g. "CrowdAudio") and drag in the two clips. The
/// AudioSource is created at runtime. The RandomLauncher and GoalDetector are found
/// automatically (assumes one of each in the scene). Both clips should be loop-friendly.
/// </summary>
public class CrowdAmbience : MonoBehaviour
{
    [Tooltip("Loops from session start, and again after every save. Assets/Audio/CrowdCheers.wav")]
    public AudioClip saveCheer;

    [Tooltip("Loops after a goal is scored, until the next save or the end of the session. " +
             "Assets/Audio/CrowdBoos.wav")]
    public AudioClip goalGroan;

    private const float Volume = 0.8f;

    // ── Private ───────────────────────────────────────────────────

    private RandomLauncher _launcher;
    private GoalDetector _detector;
    private AudioSource _source;
    private bool _sessionActive;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _launcher = FindFirstObjectByType<RandomLauncher>();
        _detector = FindFirstObjectByType<GoalDetector>();

        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 0f; // 2D — reads as "the whole environment"
        _source.volume = Volume;
    }

    void OnEnable()
    {
        if (_launcher != null)
        {
            _launcher.OnSessionStarted += HandleSessionStarted;
            _launcher.OnSessionEnded += HandleSessionEnded;
        }
        else
        {
            Debug.LogWarning("[CrowdAmbience] No RandomLauncher found — crowd audio will never start.");
        }

        if (_detector != null)
        {
            _detector.OnSaved += HandleSaved;
            _detector.OnGoalScored += HandleGoalScored;
        }
        else
        {
            Debug.LogWarning("[CrowdAmbience] No GoalDetector found — crowd audio will never switch.");
        }
    }

    void OnDisable()
    {
        if (_launcher != null)
        {
            _launcher.OnSessionStarted -= HandleSessionStarted;
            _launcher.OnSessionEnded -= HandleSessionEnded;
        }

        if (_detector != null)
        {
            _detector.OnSaved -= HandleSaved;
            _detector.OnGoalScored -= HandleGoalScored;
        }

        _sessionActive = false;
        _source.Stop();
    }

    // ── Event handlers ────────────────────────────────────────────

    void HandleSessionStarted()
    {
        _sessionActive = true;
        Play(saveCheer);
    }

    void HandleSessionEnded()
    {
        _sessionActive = false;
        _source.Stop();
    }

    void HandleSaved(Vector3 _)
    {
        if (_sessionActive)
            Play(saveCheer);
    }

    void HandleGoalScored(Vector3 _)
    {
        if (_sessionActive)
            Play(goalGroan);
    }

    // ── Audio ─────────────────────────────────────────────────────

    /// <summary>
    /// Switches the loop to <paramref name="clip"/>. Does nothing if it's already the clip
    /// playing, so back-to-back goals (or saves) don't restart it.
    /// </summary>
    void Play(AudioClip clip)
    {
        if (clip == null)
            return;

        if (_source.isPlaying && _source.clip == clip)
            return;

        _source.clip = clip;
        _source.Play();
    }
}
