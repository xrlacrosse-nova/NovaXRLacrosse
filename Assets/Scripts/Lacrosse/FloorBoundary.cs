using System;
using System.Collections;
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
    [Tooltip("If true, zeroes out all velocity (not just vertical) when the floor is hit (Rigidbody only).")]
    public bool cancelDownwardVelocity = true;

    [Tooltip("If true, a bounce impulse is applied when the floor is hit (Rigidbody only).")]
    public bool bounceOnFloor = false;

    [Range(0f, 1f)]
    [Tooltip("Fraction of vertical velocity reflected back on bounce (0 = no bounce, 1 = full bounce).")]
    public float bounciness = 0.5f;

    [Header("Despawn Settings")]
    [Tooltip("If true, the object is hidden and disabled a short time after it comes to rest on the floor.")]
    public bool despawnAfterLanding = true;

    [Tooltip("Seconds to wait after coming to rest before despawning.")]
    public float despawnDelay = 1f;

    // Cached components
    private Rigidbody _rb;
    private bool _hasRigidbody;
    private Renderer _renderer;
    private Collider _collider;
    private Coroutine _despawnRoutine;

    /// <summary>Raised once the object actually despawns (after <see cref="despawnDelay"/> elapses).</summary>
    public event Action OnDespawned;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _hasRigidbody = _rb != null;
        _renderer = GetComponent<Renderer>();
        _collider = GetComponent<Collider>();
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
                    _rb.linearVelocity = vel;
                }
                else if (cancelDownwardVelocity)
                {
                    // Kill all velocity, not just vertical, so the ball comes to a
                    // dead stop instead of sliding across the floor on its leftover
                    // horizontal speed.
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;

                    if (despawnAfterLanding && _despawnRoutine == null)
                        _despawnRoutine = StartCoroutine(DespawnAfterDelay());
                }
            }
        }
    }

    /// <summary>
    /// Waits <see cref="despawnDelay"/> seconds after the object has come to rest,
    /// then hides it and disables its physics until <see cref="CancelDespawn"/> is called.
    /// </summary>
    private IEnumerator DespawnAfterDelay()
    {
        yield return new WaitForSeconds(despawnDelay);

        if (_renderer != null) _renderer.enabled = false;
        if (_collider != null) _collider.enabled = false;
        if (_hasRigidbody) _rb.isKinematic = true;

        _despawnRoutine = null;
        OnDespawned?.Invoke();
    }

    /// <summary>
    /// Cancels any pending or already-applied despawn and makes the object visible/active
    /// again. Call this before relaunching the ball so it reappears for the next shot.
    /// </summary>
    public void CancelDespawn()
    {
        if (_despawnRoutine != null)
        {
            StopCoroutine(_despawnRoutine);
            _despawnRoutine = null;
        }

        if (_renderer != null) _renderer.enabled = true;
        if (_collider != null) _collider.enabled = true;
        if (_hasRigidbody) _rb.isKinematic = false;
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