using UnityEngine;

/// <summary>
/// Two-handed lacrosse stick grip for the shooter avatar.
///
/// Put this on the stick GameObject itself (not the avatar). Every LateUpdate (after the
/// Animator has posed the skeleton for this frame) the stick's world pose is solved from the
/// two hands:
///   - the Bottom Grip Point on the shaft is pinned to the right (butt-end) palm,
///   - the shaft is aimed through the left (top) palm,
///   - the roll around the shaft comes from a reference axis (the top hand, so the pocket
///     turns with the wrist).
///
/// Optional left hand fix (Turn Left Hand Onto Shaft): after the stick is posed, the left hand
/// bone is turned, each frame, just enough that its palm faces the shaft and its fingers run
/// across it. The clip's own pose is kept and only corrected, so it holds through the whole shot
/// instead of matching one frame. That is the only bone this script touches. Use the right hand
/// as Roll Reference with this on, so the hand fix and the stick's roll don't feed each other.
///
/// The stick is NOT reparented. Its pose is written in world space, so the OBJ's pivot and any
/// scale on the stick or the avatar's bones don't matter.
///
/// Defaults match 11747_stick_v2_L2.obj: the shaft runs along local +Z from the butt (z = 0)
/// to the head (z = 55.6), with its centerline at y = -1.818.
///
/// SETUP (in the Unity Editor):
/// 1. Select the stick GameObject. Add Component -> LacrosseStickGrip.
/// 2. Drag the avatar's right hand bone into "Right Hand Bone" (butt-end hand) and the left
///    hand bone into "Left Hand Bone" (top hand).
/// 3. With the stick selected, check the gizmos: the yellow line should run down the middle of
///    the shaft, and the green sphere should sit near the butt end.
/// 4. Drag the left hand bone into "Roll Reference" too.
/// 5. Enter Play mode and pause. Disable this component, move/rotate the stick in the Scene
///    view until it sits in both hands with the pocket facing the right way, then right-click
///    this component -> Capture Grip From Current Pose. Copy Component, exit Play mode, Paste
///    Component Values, re-enable.
/// </summary>
public class LacrosseStickGrip : MonoBehaviour
{
    [Header("Hand Bones (drag from the avatar's skeleton)")]
    [Tooltip("Hand at the butt end of the shaft. The stick is pinned to this hand.")]
    public Transform rightHandBone;

    [Tooltip("Hand further up the shaft. The shaft is aimed through this hand.")]
    public Transform leftHandBone;

    [Header("Palm Offsets (meters, in each hand bone's rotation)")]
    [Tooltip("Offset from the right hand bone (the wrist joint) to the center of the palm. " +
             "Mixamo hand bones point +Y toward the fingers.")]
    public Vector3 rightPalmOffset = new Vector3(0f, 0.08f, 0f);

    [Tooltip("Offset from the left hand bone (the wrist joint) to the center of the palm.")]
    public Vector3 leftPalmOffset = new Vector3(0f, 0.08f, 0f);

    [Header("Stick Geometry (stick's local / mesh space)")]
    [Tooltip("Local axis that runs down the shaft from the butt end toward the head.")]
    public Vector3 shaftAxisLocal = Vector3.forward;

    [Tooltip("Point on the shaft that sits in the right palm, in the stick's local (mesh) units.")]
    public Vector3 bottomGripPoint = new Vector3(0f, -1.818f, 2f);

    [Tooltip("Local axis, perpendicular to the shaft, that the pocket opens toward. For the OBJ " +
             "this is -Y; +Y is the closed back of the head.")]
    public Vector3 pocketAxisLocal = Vector3.down;

    [Tooltip("Stick length from Bottom Grip Point to the tip of the head, in the stick's local " +
             "(mesh) units. Only used for the gizmo.")]
    public float stickLength = 53.6f;

    [Header("Roll Around the Shaft")]
    [Tooltip("The pocket faces along Roll Reference Axis of this Transform. Leave empty to use " +
             "world space. Use the top (left) hand bone so the pocket turns with the wrist; a " +
             "fixed direction like the avatar root flips the pocket when the shaft points along it.")]
    public Transform rollReference;

    [Tooltip("Axis (in Roll Reference's local space, or world space if it's empty) the pocket " +
             "faces. Capture Grip From Current Pose sets this.")]
    public Vector3 rollReferenceAxis = Vector3.forward;

    [Tooltip("Extra spin around the shaft, in degrees, applied after the roll reference.")]
    [Range(-180f, 180f)]
    public float rollOffset = 0f;

    [Header("Left Hand Fix (optional)")]
    [Tooltip("Each frame, turns the left hand at the wrist so the palm faces the shaft and the " +
             "fingers run across it. Fingers and arm still come from the clip.")]
    public bool turnLeftHandOntoShaft = false;

    [Tooltip("Left hand bone's local axis pointing out of the palm. Check the blue gizmo ray: " +
             "it should point out of the palm, not the back of the hand.")]
    public Vector3 leftPalmNormalLocal = Vector3.forward;

    [Tooltip("Left hand bone's local axis pointing toward the fingertips (Mixamo: +Y). Check " +
             "the white gizmo ray.")]
    public Vector3 leftFingerAxisLocal = Vector3.up;

    [Tooltip("Extra turn of the hand around the palm direction, in degrees, after the fix.")]
    [Range(-180f, 180f)]
    public float leftHandTwist = 0f;

    [Tooltip("1 = full fix, 0 = the animation's rotation.")]
    [Range(0f, 1f)]
    public float leftHandWeight = 1f;

    // Last good pocket direction, used when the roll reference lines up with the shaft.
    private Vector3 _lastPocketDir = Vector3.up;

    void Start()
    {
        if (rightHandBone == null || leftHandBone == null)
        {
            Debug.LogError("[LacrosseStickGrip] Right Hand Bone and Left Hand Bone must both be assigned.", this);
            enabled = false;
            return;
        }

        if (shaftAxisLocal.sqrMagnitude < 0.0001f)
        {
            Debug.LogError("[LacrosseStickGrip] Shaft Axis Local can't be zero.", this);
            enabled = false;
        }
    }

    void LateUpdate()
    {
        Vector3 bottomPalm = PalmPosition(rightHandBone, rightPalmOffset);
        Vector3 topPalm = PalmPosition(leftHandBone, leftPalmOffset);

        Vector3 shaftDir = topPalm - bottomPalm;
        if (shaftDir.sqrMagnitude < 0.000001f)
            return;
        shaftDir.Normalize();

        // Which way the pocket should face, flattened onto the plane around the shaft.
        Vector3 pocketDir = Vector3.ProjectOnPlane(RollReferenceDirection(), shaftDir);
        if (pocketDir.sqrMagnitude < 0.0001f)
            pocketDir = Vector3.ProjectOnPlane(_lastPocketDir, shaftDir);
        if (pocketDir.sqrMagnitude < 0.0001f)
            return;
        pocketDir.Normalize();
        _lastPocketDir = pocketDir;

        // Map the stick's local (shaft, pocket) frame onto the world (shaft, pocket) frame.
        Quaternion worldFrame = Quaternion.LookRotation(shaftDir, pocketDir)
                              * Quaternion.AngleAxis(rollOffset, Vector3.forward);
        transform.rotation = worldFrame * Quaternion.Inverse(LocalFrame());

        // Slide the stick so its bottom grip point lands in the right palm.
        transform.position += bottomPalm - transform.TransformPoint(bottomGripPoint);

        // After the stick is posed from the animated hands, so the override doesn't feed back
        // into the shaft or roll. The Animator rewrites the bone every frame, so nothing piles up.
        if (turnLeftHandOntoShaft)
            TurnLeftHandOntoShaft(bottomPalm, shaftDir);
    }

    // Smallest turn that points the palm at the shaft, then spins the hand around the palm
    // direction so the fingers cross the shaft. Starts from this frame's animated rotation, so
    // whatever the clip does, only the misalignment is removed.
    private void TurnLeftHandOntoShaft(Vector3 shaftPoint, Vector3 shaftDir)
    {
        Quaternion animated = leftHandBone.rotation;

        // Palm should face from the wrist toward the shaft, square to it.
        Vector3 wrist = leftHandBone.position;
        Vector3 toShaft = shaftPoint + Vector3.Project(wrist - shaftPoint, shaftDir) - wrist;
        if (toShaft.sqrMagnitude < 0.000001f)
            return;
        toShaft.Normalize();

        Vector3 palm = animated * leftPalmNormalLocal.normalized;
        Quaternion swing = Quaternion.FromToRotation(palm, toShaft);

        // Fingers: square to both the shaft and the palm direction, on whichever side they
        // already point, so the hand never flips over.
        Vector3 fingers = Vector3.ProjectOnPlane(swing * animated * leftFingerAxisLocal, toShaft);
        Vector3 across = Vector3.Cross(shaftDir, toShaft);
        if (Vector3.Dot(across, fingers) < 0f)
            across = -across;
        Quaternion spin = fingers.sqrMagnitude > 0.000001f
            ? Quaternion.FromToRotation(fingers, across)
            : Quaternion.identity;

        Quaternion fixedRot = Quaternion.AngleAxis(leftHandTwist, toShaft) * spin * swing * animated;
        leftHandBone.rotation = Quaternion.Slerp(animated, fixedRot, leftHandWeight);
    }

    // ── Tuning ────────────────────────────────────────────────────

    /// <summary>
    /// Works out the palm offsets and roll from wherever the stick is right now. In Play mode:
    /// pause, disable this component, move/rotate the stick in the Scene view until it sits in
    /// both hands with the pocket facing the right way, then run this from the context menu.
    /// Play-mode values are lost on exit, so use Copy Component / Paste Component Values after.
    /// </summary>
    [ContextMenu("Capture Grip From Current Pose")]
    void CaptureGripFromCurrentPose()
    {
        if (rightHandBone == null || leftHandBone == null)
        {
            Debug.LogWarning("[LacrosseStickGrip] Assign both hand bones before capturing.", this);
            return;
        }

        // Right palm = the stick's bottom grip point, wherever it is now.
        Vector3 grip = transform.TransformPoint(bottomGripPoint);
        Vector3 shaftDir = transform.TransformDirection(shaftAxisLocal).normalized;
        rightPalmOffset = Quaternion.Inverse(rightHandBone.rotation) * (grip - rightHandBone.position);

        // Left palm = the point on the shaft closest to the left hand bone.
        Vector3 onShaft = grip + Vector3.Project(leftHandBone.position - grip, shaftDir);
        leftPalmOffset = Quaternion.Inverse(leftHandBone.rotation) * (onShaft - leftHandBone.position);

        // Roll = where the pocket faces now, stored in Roll Reference's space, so the pocket
        // turns with the reference (with the top hand, it follows the wrist).
        Vector3 pocketNow = Vector3.ProjectOnPlane(transform.TransformDirection(pocketAxisLocal), shaftDir);
        if (pocketNow.sqrMagnitude > 0.0001f)
        {
            pocketNow.Normalize();
            rollReferenceAxis = rollReference != null
                ? rollReference.InverseTransformDirection(pocketNow)
                : pocketNow;
            rollOffset = 0f;
        }

        Debug.Log($"[LacrosseStickGrip] Captured: Right Palm Offset {rightPalmOffset:F3}, " +
                  $"Left Palm Offset {leftPalmOffset:F3}, Roll Reference Axis {rollReferenceAxis:F3}. " +
                  "Copy Component now, then Paste Component Values after leaving Play mode.", this);
    }

    // ── Private helpers ───────────────────────────────────────────

    private Vector3 RollReferenceDirection()
    {
        return rollReference != null
            ? rollReference.TransformDirection(rollReferenceAxis)
            : rollReferenceAxis;
    }

    // Rotation only, not TransformPoint, so the offset stays in meters even if the rig's
    // bones carry an import scale.
    private static Vector3 PalmPosition(Transform bone, Vector3 offset)
    {
        return bone.position + bone.rotation * offset;
    }

    private Quaternion LocalFrame()
    {
        Vector3 pocket = Vector3.ProjectOnPlane(pocketAxisLocal, shaftAxisLocal);
        if (pocket.sqrMagnitude < 0.0001f)
            pocket = Vector3.ProjectOnPlane(Vector3.up, shaftAxisLocal);
        if (pocket.sqrMagnitude < 0.0001f)
            pocket = Vector3.ProjectOnPlane(Vector3.right, shaftAxisLocal);
        return Quaternion.LookRotation(shaftAxisLocal, pocket);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // Shaft centerline and grip point on the stick, drawn from local space. If these don't
        // line up with the mesh in the Scene view, Shaft Axis / Bottom Grip Point are wrong.
        Vector3 grip = transform.TransformPoint(bottomGripPoint);
        Vector3 shaftEnd = transform.TransformPoint(bottomGripPoint + shaftAxisLocal.normalized * stickLength);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(grip, shaftEnd);

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(grip, 0.025f);

        Gizmos.color = Color.magenta;
        Gizmos.DrawRay(grip, transform.TransformDirection(pocketAxisLocal).normalized * 0.15f);

        // Where the script thinks each palm is.
        Gizmos.color = Color.cyan;
        if (rightHandBone != null)
            Gizmos.DrawWireSphere(PalmPosition(rightHandBone, rightPalmOffset), 0.02f);
        if (leftHandBone != null)
        {
            Gizmos.DrawWireSphere(PalmPosition(leftHandBone, leftPalmOffset), 0.02f);

            // Left hand axes used by Turn Left Hand Onto Shaft: blue = out of the palm,
            // white = toward the fingertips.
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(leftHandBone.position, leftHandBone.rotation * leftPalmNormalLocal.normalized * 0.1f);
            Gizmos.color = Color.white;
            Gizmos.DrawRay(leftHandBone.position, leftHandBone.rotation * leftFingerAxisLocal.normalized * 0.1f);
        }
    }
#endif
}
