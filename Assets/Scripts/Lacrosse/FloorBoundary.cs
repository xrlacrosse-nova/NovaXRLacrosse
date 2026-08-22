using UnityEngine;

/// <summary>
/// Prevents this GameObject from moving below y = 0 (the floor boundary).
/// Supports both Rigidbody-based physics objects and non-physics transform objects.
/// Attach this script to any object that should be constrained above the floor.
/// </summary>
public class FloorBoundary : MonoBehaviour
{
    [Header("Floor Settings")]
    [Tooltip("The minimum Y position the object is allowed to reach.")]
    public float floorY = 0f;

    [Header("Physics Settings")]
    [Tooltip("If true, zeroes out downward velocity when the floor is hit (Rigidbody only).")]
    public bool cancelDownwardVelocity = true;

    [Tooltip("If true, a bounce impulse is applied when the floor is hit (Rigidbody only).")]
    public bool bounceOnFloor = false;

    [Range(0f, 1f)]
    [Tooltip("Fraction of vertical velocity reflected back on bounce (0 = no bounce, 1 = full bounce).")]
    public float bounciness = 0.5f;

    // Cached components
    private Rigidbody _rb;
    private bool _hasRigidbody;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _hasRigidbody = _rb != null;
    }

    private void Update()
    {
        // Handle non-physics objects (or as a safety net for physics objects)
        if (!_hasRigidbody)
        {
            ClampTransform();
        }
    }

    private void FixedUpdate()
    {
        // Handle Rigidbody-based objects in the physics step
        if (_hasRigidbody)
        {
            ClampRigidbody();
        }
    }

    /// <summary>
    /// Clamps a non-Rigidbody object's transform position at the floor.
    /// </summary>
    private void ClampTransform()
    {
        Vector3 pos = transform.position;

        if (pos.y < floorY)
        {
            pos.y = floorY;
            transform.position = pos;
        }
    }

    /// <summary>
    /// Clamps a Rigidbody object's position at the floor and optionally
    /// cancels or bounces its downward velocity.
    /// </summary>
    private void ClampRigidbody()
    {
        Vector3 pos = _rb.position;

        if (pos.y < floorY)
        {
            // Snap position to floor
            pos.y = floorY;
            _rb.MovePosition(pos);

            Vector3 vel = _rb.linearVelocity;

            // Only act if the object is still moving downward
            if (vel.y < 0f)
            {
                if (bounceOnFloor)
                {
                    // Reflect the vertical component
                    vel.y = Mathf.Abs(vel.y) * bounciness;
                }
                else if (cancelDownwardVelocity)
                {
                    // Kill vertical velocity entirely
                    vel.y = 0f;
                }

                _rb.linearVelocity = vel;
            }
        }
    }

    /// <summary>
    /// Draws a visual floor indicator in the Scene view for easy debugging.
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0.4f, 0.4f);

        Vector3 center = new Vector3(transform.position.x, floorY, transform.position.z);
        Gizmos.DrawWireCube(center, new Vector3(2f, 0.02f, 2f));

        Gizmos.color = new Color(0f, 1f, 0.4f, 0.15f);
        Gizmos.DrawCube(center, new Vector3(2f, 0.02f, 2f));
    }
}