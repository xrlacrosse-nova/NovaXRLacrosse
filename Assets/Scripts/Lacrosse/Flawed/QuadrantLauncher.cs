using System.Collections;
using UnityEngine;

/// <summary>
/// Launches the lacrosse ball toward a randomly-chosen point inside one of the
/// four quadrants of the GoalDetector's gate plane.
///
/// Quadrant layout (as seen from the launcher / shooter's perspective):
///
///     TopLeft  | TopRight
///   -----------+-----------
///   BottomLeft | BottomRight
///
/// Usage:
///   1. Attach this script to the ball GameObject (same object as GoalDetector).
///   2. Assign the Inspector fields.
///   3. Call  launcher.SetTargetQuadrant(Quadrant.TopRight)  (or whichever).
///   4. Call  launcher.Launch()  to fire.
/// </summary>
[RequireComponent(typeof(GoalDetector))]
public class QuadrantLauncher : MonoBehaviour
{
    // ── Quadrant enum ─────────────────────────────────────────────

    public enum Quadrant
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    // ── Inspector ─────────────────────────────────────────────────

    [Header("Launch Origin")]
    [Tooltip("World-space position the ball is reset to before each launch. " +
             "Typically somewhere in front of the goal (positive Z).")]
    public Vector3 launchOrigin = new Vector3(0f, 1f, 5f);

    [Header("Launch Speed")]
    [Tooltip("Speed of the ball in m/s.")]
    [Range(1f, 40f)]
    public float launchSpeed = 15f;

    [Header("Random Scatter Inside Quadrant")]
    [Tooltip("How much of each half-extent to use for random sampling. " +
             "1 = full quadrant, 0 = always aim at the quadrant centre.")]
    [Range(0f, 1f)]
    public float scatter = 0.85f;

    [Header("Target Quadrant")]
    [Tooltip("Which quadrant the next launch will aim at. " +
             "Override at runtime via SetTargetQuadrant().")]
    public Quadrant targetQuadrant = Quadrant.TopRight;

    // ── internal refs ─────────────────────────────────────────────

    private GoalDetector _goalDetector;

    // ── lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _goalDetector = GetComponent<GoalDetector>();
    }

    // ── public API ────────────────────────────────────────────────

    /// <summary>
    /// Sets the quadrant the next call to Launch() will aim at.
    /// </summary>
    public void SetTargetQuadrant(Quadrant quadrant)
    {
        targetQuadrant = quadrant;
        Debug.Log($"[BallLauncher] Target quadrant set to: {quadrant}");
    }

    /// <summary>
    /// Resets the ball to launchOrigin and moves it toward a random point
    /// inside the currently selected quadrant.
    /// </summary>
    public void Launch()
    {
        StopAllCoroutines();

        transform.position = launchOrigin;
        _goalDetector.ResetState();

        Vector3 target = SamplePointInQuadrant(targetQuadrant);

        _goalDetector.OnBallLaunched();

        Debug.Log($"[BallLauncher] Launched toward {targetQuadrant} " +
                  $"target=({target.x:F2},{target.y:F2},{target.z:F2})");

        StartCoroutine(MoveBall(target));
    }

    // ── movement ──────────────────────────────────────────────────

    /// <summary>
    /// Moves the ball in a straight line toward the target at launchSpeed.
    /// No physics or gravity involved.
    /// </summary>
    private IEnumerator MoveBall(Vector3 target)
    {
        while (Vector3.Distance(transform.position, target) > 0.01f)
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                target,
                launchSpeed * Time.deltaTime
            );
            yield return null;
        }

        transform.position = target;
    }

    // ── quadrant sampling ─────────────────────────────────────────

    /// <summary>
    /// Returns a world-space point randomly sampled within the requested
    /// quadrant of the GoalDetector's gate rectangle.
    /// </summary>
    private Vector3 SamplePointInQuadrant(Quadrant quadrant)
    {
        Vector3 gateCenter = _goalDetector.goalGateCenter;
        Vector2 halfSize = _goalDetector.goalGateHalfSize;

        float qHalfX = halfSize.x * 0.5f;
        float qHalfY = halfSize.y * 0.5f;

        float centreX, centreY;
        switch (quadrant)
        {
            case Quadrant.TopLeft:
                centreX = gateCenter.x - qHalfX;
                centreY = gateCenter.y + qHalfY;
                break;
            case Quadrant.TopRight:
                centreX = gateCenter.x + qHalfX;
                centreY = gateCenter.y + qHalfY;
                break;
            case Quadrant.BottomLeft:
                centreX = gateCenter.x - qHalfX;
                centreY = gateCenter.y - qHalfY;
                break;
            default: // BottomRight
                centreX = gateCenter.x + qHalfX;
                centreY = gateCenter.y - qHalfY;
                break;
        }

        float offsetX = Random.Range(-qHalfX, qHalfX) * scatter;
        float offsetY = Random.Range(-qHalfY, qHalfY) * scatter;

        return new Vector3(centreX + offsetX, centreY + offsetY, gateCenter.z);
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (_goalDetector == null)
            _goalDetector = GetComponent<GoalDetector>();
        if (_goalDetector == null) return;

        Vector3 gateCenter = _goalDetector.goalGateCenter;
        Vector2 halfSize = _goalDetector.goalGateHalfSize;

        // Draw dividing lines to show quadrant boundaries
        Gizmos.color = Color.green;

        // Vertical centre line
        Gizmos.DrawLine(
            new Vector3(gateCenter.x, gateCenter.y - halfSize.y, gateCenter.z),
            new Vector3(gateCenter.x, gateCenter.y + halfSize.y, gateCenter.z));

        // Horizontal centre line
        Gizmos.DrawLine(
            new Vector3(gateCenter.x - halfSize.x, gateCenter.y, gateCenter.z),
            new Vector3(gateCenter.x + halfSize.x, gateCenter.y, gateCenter.z));

        // Highlight the currently-selected quadrant in magenta
        Gizmos.color = new Color(1f, 0f, 1f, 0.35f);
        Vector3 qSize = new Vector3(halfSize.x, halfSize.y, 0.02f);
        Vector3 qCentre = QuadrantGizmoCentre(targetQuadrant, gateCenter, halfSize);
        Gizmos.DrawCube(qCentre, qSize);

        // Launch origin → quadrant centre arrow
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(launchOrigin, qCentre);
        Gizmos.DrawSphere(launchOrigin, 0.06f);
    }

    private Vector3 QuadrantGizmoCentre(Quadrant q, Vector3 gateCenter, Vector2 halfSize)
    {
        float qHalfX = halfSize.x * 0.5f;
        float qHalfY = halfSize.y * 0.5f;
        switch (q)
        {
            case Quadrant.TopLeft: return new Vector3(gateCenter.x - qHalfX, gateCenter.y + qHalfY, gateCenter.z);
            case Quadrant.TopRight: return new Vector3(gateCenter.x + qHalfX, gateCenter.y + qHalfY, gateCenter.z);
            case Quadrant.BottomLeft: return new Vector3(gateCenter.x - qHalfX, gateCenter.y - qHalfY, gateCenter.z);
            default: return new Vector3(gateCenter.x + qHalfX, gateCenter.y - qHalfY, gateCenter.z);
        }
    }
}