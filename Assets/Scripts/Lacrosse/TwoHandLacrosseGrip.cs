using UnityEngine;

/// <summary>
/// Two-handed lacrosse stick grip.
///
/// The stick's top end is parented to the right hand bone (same technique as
/// LacrosseStickAttacher). Every frame, after the Animator has posed the
/// skeleton, the stick is rotated so a point partway down its shaft aims
/// toward the left hand bone - giving the look of a genuine two-handed grip
/// without needing an IK rig.
///
/// NOTE: This class was referenced by the scene (by GUID, with field values
/// already configured on the GameObject) but no implementation was checked
/// into the project. The behavior below was reconstructed from the field
/// names/values already saved in the scene. Tune localShaftAxis in the
/// Inspector if the grip doesn't line up for this particular stick mesh.
/// </summary>
public class TwoHandLacrosseGrip : MonoBehaviour
{
    [Header("Hand Bones")]
    public Transform rightHandBone;
    public Transform leftHandBone;

    [Header("Right Hand (parents the stick)")]
    public Vector3 rightPosOffset = Vector3.zero;
    public Vector3 rightRotOffset = Vector3.zero;

    [Header("Left Hand (aims the shaft toward the left hand)")]
    [Tooltip("Normalized position (0-1) along the shaft, measured from the right-hand end, that the left hand grips.")]
    [Range(0f, 1f)]
    public float leftGripPoint = 0.75f;

    [Tooltip("Length of the stick shaft, used with leftGripPoint to find how far down the shaft the left hand grip point sits.")]
    public float stickLength = 1f;

    [Header("Body Clamp")]
    [Tooltip("Optional. Keeps the left hand grip target from pulling the shaft through the torso.")]
    public Transform spineBone;
    public float minDistanceFromSpine = 0.15f;

    [Header("Advanced")]
    [Tooltip("Local axis (before any rotation) that points from the right-hand end of the stick down the shaft.")]
    public Vector3 localShaftAxis = Vector3.up;

    private void Start()
    {
        if (rightHandBone == null)
        {
            Debug.LogError("[TwoHandLacrosseGrip] No right hand bone assigned!", this);
            enabled = false;
            return;
        }

        transform.SetParent(rightHandBone, worldPositionStays: false);
        transform.localPosition = rightPosOffset;
        transform.localRotation = Quaternion.Euler(rightRotOffset);
    }

    private void LateUpdate()
    {
        if (leftHandBone == null) return;

        Vector3 target = leftHandBone.position;

        // Keep the grip target from pulling the shaft through the torso.
        if (spineBone != null)
        {
            Vector3 fromSpine = target - spineBone.position;
            float distance = fromSpine.magnitude;
            if (distance < minDistanceFromSpine)
            {
                Vector3 direction = distance > 0.0001f ? fromSpine / distance : transform.right;
                target = spineBone.position + direction * minDistanceFromSpine;
            }
        }

        // Rotate the shaft so the grip point (leftGripPoint * stickLength from
        // the right-hand end) lands on the (possibly clamped) left hand target.
        Vector3 gripPointLocalOffset = localShaftAxis.normalized * (stickLength * leftGripPoint);
        Vector3 pivot = transform.position;
        Vector3 currentDir = (transform.TransformPoint(gripPointLocalOffset) - pivot).normalized;
        Vector3 desiredDir = (target - pivot).normalized;

        if (currentDir.sqrMagnitude > 0.0001f && desiredDir.sqrMagnitude > 0.0001f)
        {
            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            transform.rotation = delta * transform.rotation;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 gripPointLocalOffset = localShaftAxis.normalized * (stickLength * leftGripPoint);

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.TransformPoint(gripPointLocalOffset), 0.02f);

        if (leftHandBone != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, leftHandBone.position);
        }
    }
#endif
}
