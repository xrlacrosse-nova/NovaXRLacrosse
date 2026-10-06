using UnityEngine;
using MagicLeap.Examples;

/// <summary>
/// Vibrates the ML2 controller when a shot ends, with a distinct pattern for each outcome:
///   - Save: a light, short tap ("you got it").
///   - Goal: a stronger, longer buzz ("that one got past you").
/// Subscribes to GoalDetector's OnSaved / OnGoalScored, so it fires exactly once per shot.
///
/// Setup: attach to the ball GameObject that has GoalDetector (same one as RandomLauncher).
/// Requires an InputActionManager in the scene for MagicLeapController; without one it logs a
/// warning once and does nothing.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class ShotOutcomeHaptics : MonoBehaviour
{
    [Header("Save (light tap)")]
    [Tooltip("Vibration strength when the goalie saves a shot, from 0 to 1.")]
    [Range(0f, 1f)]
    public float saveAmplitude = 0.3f;

    [Tooltip("Vibration length (seconds) when the goalie saves a shot.")]
    [Min(0f)]
    public float saveDuration = 0.08f;

    [Header("Goal (heavy buzz)")]
    [Tooltip("Vibration strength when a shot scores on the goalie, from 0 to 1.")]
    [Range(0f, 1f)]
    public float goalAmplitude = 0.9f;

    [Tooltip("Vibration length (seconds) when a shot scores on the goalie.")]
    [Min(0f)]
    public float goalDuration = 0.35f;

    [Header("Debug")]
    [Tooltip("Log each haptic impulse sent — handy for confirming events fire during the on-device test.")]
    public bool logImpulses = false;

    // ── Private ───────────────────────────────────────────────────

    private GoalDetector _goalDetector;
    private bool _warnedUnavailable = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
    }

    void OnEnable()
    {
        _goalDetector.OnSaved += HandleSaved;
        _goalDetector.OnGoalScored += HandleGoalScored;
    }

    void OnDisable()
    {
        _goalDetector.OnSaved -= HandleSaved;
        _goalDetector.OnGoalScored -= HandleGoalScored;
    }

    // ── Event handlers ────────────────────────────────────────────

    void HandleSaved(Vector3 _) => Pulse(saveAmplitude, saveDuration, "save");

    void HandleGoalScored(Vector3 _) => Pulse(goalAmplitude, goalDuration, "goal");

    // ── Haptics ───────────────────────────────────────────────────

    [ContextMenu("Test Save Haptic")]
    void TestSave() => Pulse(saveAmplitude, saveDuration, "save (test)");

    [ContextMenu("Test Goal Haptic")]
    void TestGoal() => Pulse(goalAmplitude, goalDuration, "goal (test)");

    void Pulse(float amplitude, float duration, string label)
    {
        // Same graceful-failure pattern as RandomLauncher: MagicLeapController.Instance throws if
        // there's no InputActionManager in the scene (e.g. Editor testing without the ML rig).
        bool sent;
        try
        {
            sent = MagicLeapController.Instance.SendHapticImpulse(amplitude, duration);
        }
        catch (System.NullReferenceException)
        {
            sent = false;
        }

        if (!sent)
        {
            if (!_warnedUnavailable)
            {
                Debug.LogWarning("[ShotOutcomeHaptics] No controller haptics available (no InputActionManager " +
                                 "in scene, or no 'Haptics' action in the Controller map) — haptics disabled.");
                _warnedUnavailable = true;
            }
            return;
        }

        if (logImpulses)
            Debug.Log($"[ShotOutcomeHaptics] {label}: amplitude {amplitude:0.00}, duration {duration:0.00}s");
    }
}
