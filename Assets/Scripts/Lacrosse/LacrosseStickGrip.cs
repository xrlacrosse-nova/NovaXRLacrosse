using UnityEngine;

/// <summary>
/// Two-handed lacrosse stick grip.
///
/// Put this on the stick GameObject itself (not the avatar). On Start, the
/// stick is parented to the right hand bone and given a fixed local offset.
/// Every LateUpdate (after the Animator has posed the skeleton for this
/// frame), the stick is rotated about the right-hand pivot so a point
/// partway down its shaft points at the left hand bone - producing the look
/// of a two-handed grip without an IK rig.
///
/// SETUP (in the Unity Editor):
/// 1. Select the stick GameObject in the Hierarchy.
/// 2. Add Component -> LacrosseStickGrip.
/// 3. Drag the avatar's right hand bone Transform into "Right Hand Bone".
/// 4. Drag the avatar's left hand bone Transform into "Left Hand Bone".
/// 5. Enter Play mode and tune Right Hand Offset / Rotation, Shaft Axis, and
///    Grip Point until the stick sits naturally between both hands.
/// </summary>
public class LacrosseStickGrip : MonoBehaviour
{
    [Header("Hand Bones (drag from the avatar's skeleton)")]
    [Tooltip("The bone the stick is parented to and pivots around.")]
    public Transform rightHandBone;

    [Tooltip("The bone the shaft is aimed toward.")]
    public Transform leftHandBone;

    [Header("Right Hand Attachment")]
    [Tooltip("Local position offset from the right hand bone. Tune in Play mode.")]
    public Vector3 rightHandPositionOffset = Vector3.zero;

    [Tooltip("Local rotation offset from the right hand bone (Euler angles). Tune in Play mode.")]
    public Vector3 rightHandRotationOffset = Vector3.zero;

    [Header("Left Hand Aim")]
    [Tooltip("Local axis (in the stick's own local space) that points from the right-hand end down the shaft toward the head.")]
    public Vector3 shaftAxis = Vector3.up;

    [Tooltip("Distance along the shaft, from the right-hand end, where the left hand grips.")]
    public float leftHandGripDistance = 0.4f;

    [Header("Body Clamp (optional)")]
    [Tooltip("If assigned, the left hand aim target is kept at least Min Distance From Spine away from this bone, so the shaft can't rotate through the torso.")]
    public Transform spineBone;
    public float minDistanceFromSpine = 0.15f;

    void Start()
    {
        if (rightHandBone == null)
        {
            Debug.LogError("[LacrosseStickGrip] Right Hand Bone is not assigned.", this);
            enabled = false;
            return;
        }

        transform.SetParent(rightHandBone, worldPositionStays: false);
    }

    void LateUpdate()
    {
        // Re-anchor to the offset every frame (instead of only in Start) so the
        // shaft's roll never drifts and the offset fields stay live-tunable.
        transform.localPosition = rightHandPositionOffset;
        transform.localRotation = Quaternion.Euler(rightHandRotationOffset);

        if (leftHandBone == null || shaftAxis.sqrMagnitude < 0.0001f)
            return;

        Vector3 aimTarget = leftHandBone.position;

        if (spineBone != null)
        {
            Vector3 fromSpine = aimTarget - spineBone.position;
            float distance = fromSpine.magnitude;
            if (distance < minDistanceFromSpine)
            {
                Vector3 direction = distance > 0.0001f ? fromSpine / distance : transform.right;
                aimTarget = spineBone.position + direction * minDistanceFromSpine;
            }
        }

        Vector3 gripPointWorld = transform.TransformPoint(shaftAxis.normalized * leftHandGripDistance);
        Vector3 pivot = transform.position;

        Vector3 currentDir = (gripPointWorld - pivot).normalized;
        Vector3 desiredDir = (aimTarget - pivot).normalized;

        if (currentDir.sqrMagnitude > 0.0001f && desiredDir.sqrMagnitude > 0.0001f)
        {
            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            transform.rotation = delta * transform.rotation;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 gripPointWorld = transform.TransformPoint(shaftAxis.normalized * leftHandGripDistance);

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(gripPointWorld, 0.02f);

        if (leftHandBone != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, leftHandBone.position);
        }
    }
#endif
}
