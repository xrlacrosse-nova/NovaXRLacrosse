using UnityEngine;
using System;

/// <summary>
/// LacrosseEvents
/// Decoupled event bus for the lacrosse simulation.
/// Any system can subscribe without tight coupling to the ball or shooter.
///
/// Usage example (subscribe in TrainingManager.cs):
///   LacrosseEvents.OnGoalScored += (pos, mph) => Debug.Log($"Goal! {mph:F1} mph");
///
/// IMPORTANT – memory leak prevention:
///   Always unsubscribe in OnDestroy if your subscriber is a MonoBehaviour:
///
///     private void OnDestroy()
///     {
///         LacrosseEvents.OnGoalScored -= HandleGoalScored;
///         LacrosseEvents.OnSaved      -= HandleSave;
///         // etc.
///     }
///
///   Alternatively, call LacrosseEvents.ClearAllListeners() during scene teardown
///   (e.g. from a SceneManager.sceneUnloaded callback) to wipe all subscribers at once.
///   Be careful with ClearAllListeners in multi-scene setups — it removes ALL
///   subscribers, including those on persistent objects.
/// </summary>
public static class LacrosseEvents
{
    // ── Ball outcome events ───────────────────────────────────────────

    /// <summary>Ball crossed the goal line. Args: (impactWorldPos, speedMPH)</summary>
    public static Action<Vector3, float> OnGoalScored;

    /// <summary>Ball hit a goal post. Args: (impactWorldPos)</summary>
    public static Action<Vector3> OnPostHit;

    /// <summary>Ball was saved by the goalkeeper. Args: (impactWorldPos, speedMPH)</summary>
    public static Action<Vector3, float> OnSaved;

    // ── Shooter state events ──────────────────────────────────────────

    /// <summary>
    /// Fired just before the shooter releases, giving the goalie a reaction window.
    /// Args: (targetWorldPos, secondsUntilRelease)
    /// </summary>
    public static Action<Vector3, float> OnShotAnticipation;

    /// <summary>Fired when the shooter begins a new aiming cycle.</summary>
    public static Action<LacrosseShooterAnimator.ShotType> OnAimingStarted;

    // ── Training / session events ─────────────────────────────────────

    /// <summary>Training session started.</summary>
    public static Action OnSessionStarted;

    /// <summary>Training session ended. Args: (shotsTotal, saveCount, goalCount)</summary>
    public static Action<int, int, int> OnSessionEnded;

    // ── Utility ───────────────────────────────────────────────────────

    /// <summary>
    /// Clear all subscribers.
    ///
    /// Best called from a SceneManager.sceneUnloaded handler to prevent stale
    /// delegates after scene transitions. In multi-scene / additive loading setups,
    /// prefer per-subscriber OnDestroy unsubscription instead so persistent-object
    /// listeners are not accidentally removed.
    /// </summary>
    public static void ClearAllListeners()
    {
        OnGoalScored = null;
        OnPostHit = null;
        OnSaved = null;
        OnShotAnticipation = null;
        OnAimingStarted = null;
        OnSessionStarted = null;
        OnSessionEnded = null;
    }
}