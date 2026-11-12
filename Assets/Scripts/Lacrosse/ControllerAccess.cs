using MagicLeap.Examples;

/// <summary>
/// One place for getting the Magic Leap controller. MagicLeapController.Instance throws a
/// NullReferenceException when there's no InputActionManager in the scene (e.g. Editor testing
/// without the ML rig). It doesn't cache a failed attempt, so callers can simply retry later.
/// Used by ControllerRayPointer and ShotOutcomeHaptics.
/// </summary>
public static class ControllerAccess
{
    /// <summary>The controller, or false (and null) if there's no ML rig input yet.</summary>
    public static bool TryGetController(out MagicLeapController controller)
    {
        try
        {
            controller = MagicLeapController.Instance;
        }
        catch (System.NullReferenceException)
        {
            controller = null;
        }
        return controller != null;
    }
}
