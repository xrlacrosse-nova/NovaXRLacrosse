using System.Collections;
using UnityEngine;

/// <summary>
/// Crowd audio for a training session:
///   - Ambient bed: a looping crowd murmur that fades in when a session starts
///     (RandomLauncher.OnSessionStarted) and fades out when it ends (OnSessionEnded).
///   - Reaction stingers: a cheer when the goalie saves a shot (GoalDetector.OnSaved) and a
///     groan when a shot scores (GoalDetector.OnGoalScored), layered on top of the bed.
///
/// Stingers are 2D by default. Assign StandsPoint to play them spatially from that position
/// instead, so they sound like they come from the stands.
///
/// Setup: attach to its own GameObject (e.g. "CrowdAudio") and drag in the clips. The
/// AudioSources are created at runtime, so none need adding by hand. Launcher and Detector are
/// found automatically if left empty (assumes one ball in the scene).
/// </summary>
public class CrowdAmbience : MonoBehaviour
{
    [Header("Event Sources")]
    [Tooltip("Session start/end source. Leave empty to find the RandomLauncher in the scene.")]
    public RandomLauncher launcher;

    [Tooltip("Save/goal source. Leave empty to find the GoalDetector in the scene.")]
    public GoalDetector detector;

    [Header("Ambient Bed")]
    [Tooltip("Looping crowd murmur played for the length of a session. Leave empty to disable.")]
    public AudioClip ambientLoop;

    [Tooltip("Ambient bed volume once fully faded in.")]
    [Range(0f, 1f)]
    public float ambientVolume = 0.4f;

    [Tooltip("Seconds to fade the ambient bed in at session start.")]
    [Min(0f)]
    public float fadeInTime = 1.5f;

    [Tooltip("Seconds to fade the ambient bed out at session end.")]
    [Min(0f)]
    public float fadeOutTime = 2.5f;

    [Header("Reaction Stingers")]
    [Tooltip("Played when the goalie saves a shot. Leave empty to disable.")]
    public AudioClip saveCheer;

    [Tooltip("Played when a shot scores on the goalie. Leave empty to disable.")]
    public AudioClip goalGroan;

    [Range(0f, 1f)]
    public float saveCheerVolume = 0.8f;

    [Range(0f, 1f)]
    public float goalGroanVolume = 0.8f;

    [Tooltip("Optional. If set, stingers play spatially from this position (e.g. the stands) " +
             "instead of as 2D sound.")]
    public Transform standsPoint;

    // ── Private ───────────────────────────────────────────────────

    private AudioSource _ambientSource;
    private AudioSource _stingerSource;
    private Coroutine _fade;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        if (launcher == null)
            launcher = FindFirstObjectByType<RandomLauncher>();
        if (detector == null)
            detector = FindFirstObjectByType<GoalDetector>();

        _ambientSource = CreateSource(loop: true);
        _stingerSource = CreateSource(loop: false);
    }

    void OnEnable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted += HandleSessionStarted;
            launcher.OnSessionEnded += HandleSessionEnded;
        }
        else
        {
            Debug.LogWarning("[CrowdAmbience] No RandomLauncher found — ambient bed will never start.");
        }

        if (detector != null)
        {
            detector.OnSaved += HandleSaved;
            detector.OnGoalScored += HandleGoalScored;
        }
        else
        {
            Debug.LogWarning("[CrowdAmbience] No GoalDetector found — reaction stingers are disabled.");
        }
    }

    void OnDisable()
    {
        if (launcher != null)
        {
            launcher.OnSessionStarted -= HandleSessionStarted;
            launcher.OnSessionEnded -= HandleSessionEnded;
        }

        if (detector != null)
        {
            detector.OnSaved -= HandleSaved;
            detector.OnGoalScored -= HandleGoalScored;
        }

        StopFade();
        _ambientSource.Stop();
    }

    // ── Event handlers ────────────────────────────────────────────

    void HandleSessionStarted()
    {
        if (ambientLoop == null)
            return;

        // Fade from wherever the bed currently is, so a session starting mid-fade-out doesn't
        // restart the loop or jump in volume.
        if (!_ambientSource.isPlaying)
        {
            _ambientSource.clip = ambientLoop;
            _ambientSource.volume = 0f;
            _ambientSource.Play();
        }
        StartFade(ambientVolume, fadeInTime, stopWhenDone: false);
    }

    void HandleSessionEnded()
    {
        if (_ambientSource.isPlaying)
            StartFade(0f, fadeOutTime, stopWhenDone: true);
    }

    void HandleSaved(Vector3 _) => PlayStinger(saveCheer, saveCheerVolume);

    void HandleGoalScored(Vector3 _) => PlayStinger(goalGroan, goalGroanVolume);

    // ── Audio ─────────────────────────────────────────────────────

    AudioSource CreateSource(bool loop)
    {
        var source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f; // 2D — reads as "the whole environment"
        return source;
    }

    void PlayStinger(AudioClip clip, float volume)
    {
        if (clip == null)
            return;

        if (standsPoint != null)
            AudioSource.PlayClipAtPoint(clip, standsPoint.position, volume);
        else
            _stingerSource.PlayOneShot(clip, volume);
    }

    void StartFade(float targetVolume, float duration, bool stopWhenDone)
    {
        StopFade();
        _fade = StartCoroutine(Fade(targetVolume, duration, stopWhenDone));
    }

    void StopFade()
    {
        if (_fade != null)
        {
            StopCoroutine(_fade);
            _fade = null;
        }
    }

    IEnumerator Fade(float targetVolume, float duration, bool stopWhenDone)
    {
        float startVolume = _ambientSource.volume;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            _ambientSource.volume = Mathf.Lerp(startVolume, targetVolume, t / duration);
            yield return null;
        }

        _ambientSource.volume = targetVolume;
        if (stopWhenDone)
            _ambientSource.Stop();
        _fade = null;
    }
}
