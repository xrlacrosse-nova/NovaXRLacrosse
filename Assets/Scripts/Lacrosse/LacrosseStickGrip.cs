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
/// Optional left hand fix: after the stick is posed, the left hand bone's rotation can be
/// overridden (Align Left Hand) relative to the stick, the forearm, or the clip's own rotation
/// (Left Hand Rotation Space). That is the only bone this script touches. Tune it in Play mode;
/// Inspector edits apply right away, even while paused.
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

    /// <summary>What Left Hand Grip Rotation is measured against.</summary>
    public enum LeftHandRotationSpace
    {
        /// <summary>Fixed rotation relative to the stick; the hand follows the stick.</summary>
        RelativeToStick,
        /// <summary>Replaces the wrist's local rotation (relative to the forearm).</summary>
        RelativeToForearm,
        /// <summary>Extra turn on top of the clip's rotation, in the hand's own axes. (0,0,0) = clip.</summary>
        OffsetFromAnimation,
    }

    [Header("Left Hand Fix (optional)")]
    [Tooltip("Overrides the left hand bone's rotation every frame with Left Hand Grip Rotation. " +
             "Only turns the hand at the wrist; fingers and arm still come from the clip. Must be " +
             "ticked or the numbers below do nothing.")]
    public bool alignLeftHand = false;

    [Tooltip("What Left Hand Grip Rotation is relative to. Relative To Stick: hand locked to the " +
             "stick. Relative To Forearm: a fixed wrist angle. Offset From Animation: a turn added " +
             "on top of the clip.")]
    public LeftHandRotationSpace leftHandRotationSpace = LeftHandRotationSpace.RelativeToStick;

    [Tooltip("Left hand rotation (Euler degrees), in Left Hand Rotation Space. Only applies in " +
             "Play mode; changes show right away, paused or not.")]
    public Vector3 leftHandGripRotation = Vector3.zero;

    [Tooltip("1 = hand fully follows the grip rotation, 0 = the animation's rotation.")]
    [Range(0f, 1f)]
    public float leftHandAlignWeight = 1f;

    // Last good pocket direction, used when the roll reference lines up with the shaft.
    private Vector3 _lastPocketDir = Vector3.up;

    // The clip's left hand rotation this frame, before the fix. Lets Inspector edits re-apply
    // the fix while paused, when LateUpdate doesn't run and the Animator doesn't rewrite the bone.
    private Quaternion _animatedLeftHandRotation;
    private bool _hasAnimatedLeftHand;
    private bool _warnedFixOff;

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
        _animatedLeftHandRotation = leftHandBone.rotation;
        _hasAnimatedLeftHand = true;
        if (alignLeftHand)
            ApplyLeftHandFix();
    }

    // Always starts from this frame's clip rotation, so applying it twice (LateUpdate, then an
    // Inspector edit while paused) gives the same result.
    private void ApplyLeftHandFix()
    {
        Quaternion grip = Quaternion.Euler(leftHandGripRotation);
        Quaternion target;
        switch (leftHandRotationSpace)
        {
            case LeftHandRotationSpace.RelativeToForearm:
                target = leftHandBone.parent != null ? leftHandBone.parent.rotation * grip : grip;
                break;
            case LeftHandRotationSpace.OffsetFromAnimation:
                target = _animatedLeftHandRotation * grip;
                break;
            default:
                target = transform.rotation * grip;
                break;
        }
        leftHandBone.rotation = Quaternion.Slerp(_animatedLeftHandRotation, target, leftHandAlignWeight);
    }

    // Inspector edits in Play mode: re-apply now so they show even while paused, and say so if
    // the fix is off (the usual reason "changing the numbers does nothing").
    void OnValidate()
    {
        if (!Application.isPlaying || leftHandBone == null || !_hasAnimatedLeftHand)
            return;

        if (alignLeftHand)
        {
            ApplyLeftHandFix();
            _warnedFixOff = false;
        }
        else
        {
            leftHandBone.rotation = _animatedLeftHandRotation;
            if (!_warnedFixOff && (leftHandGripRotation != Vector3.zero || leftHandRotationSpace != LeftHandRotationSpace.RelativeToStick))
            {
                Debug.LogWarning("[LacrosseStickGrip] Align Left Hand is off, so Left Hand Grip " +
                                 "Rotation does nothing. Tick it (or run Start Left Hand Fix From " +
                                 "Current Pose).", this);
                _warnedFixOff = true;
            }
        }
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

    /// <summary>
    /// Turns on Align Left Hand starting from the hand's current rotation (in the chosen Left
    /// Hand Rotation Space), so nothing jumps. Then drag Left Hand Grip Rotation's X/Y/Z in the
    /// Inspector to turn the hand live, in Play mode, paused or running.
    /// </summary>
    [ContextMenu("Start Left Hand Fix From Current Pose")]
    void StartLeftHandFixFromCurrentPose()
    {
        if (leftHandBone == null)
        {
            Debug.LogWarning("[LacrosseStickGrip] Assign Left Hand Bone first.", this);
            return;
        }

        // With the fix already on, the bone holds last frame's override; read it as-is so the
        // captured value is what's on screen.
        Quaternion current = leftHandBone.rotation;
        Quaternion captured;
        switch (leftHandRotationSpace)
        {
            case LeftHandRotationSpace.RelativeToForearm:
                captured = leftHandBone.localRotation;
                break;
            case LeftHandRotationSpace.OffsetFromAnimation:
                captured = _hasAnimatedLeftHand
                    ? Quaternion.Inverse(_animatedLeftHandRotation) * current
                    : Quaternion.identity;
                break;
            default:
                captured = Quaternion.Inverse(transform.rotation) * current;
                break;
        }
        leftHandGripRotation = WrapEuler(captured.eulerAngles);
        leftHandAlignWeight = 1f;
        alignLeftHand = true;

        Debug.Log($"[LacrosseStickGrip] Left hand fix on ({leftHandRotationSpace}), starting at " +
                  $"{leftHandGripRotation:F1}. Drag Left Hand Grip Rotation X/Y/Z to turn the hand. " +
                  "Copy Component before leaving Play mode.", this);
    }

    // 0..360 → -180..180, so small turns read as small numbers in the Inspector.
    private static Vector3 WrapEuler(Vector3 e)
    {
        return new Vector3(Mathf.DeltaAngle(0f, e.x), Mathf.DeltaAngle(0f, e.y), Mathf.DeltaAngle(0f, e.z));
    }

    /// <summary>
    /// Which side of the head a ball launched with this velocity leaves through, for the launch
    /// log: the open face (pocket side, what a real release looks like), off the scoop, through a
    /// sidewall, or back through the net. The launch itself doesn't use this; the ball is always
    /// aimed at its target. A sidewall or net exit means the pocket isn't facing the goal at
    /// release: tune Roll Reference Axis / Roll Offset, or the shooter's Release Normalized Time.
    /// </summary>
    public string DescribeExit(Vector3 worldVelocity)
    {
        Vector3 v = transform.InverseTransformDirection(worldVelocity).normalized;
        Vector3 shaft = shaftAxisLocal.normalized;
        Vector3 pocket = LocalFrame() * Vector3.up;
        Vector3 side = Vector3.Cross(shaft, pocket);

        float alongShaft = Vector3.Dot(v, shaft);
        float outOfPocket = Vector3.Dot(v, pocket);
        float sideways = Vector3.Dot(v, side);

        string exit;
        if (Mathf.Abs(sideways) >= Mathf.Abs(outOfPocket) && Mathf.Abs(sideways) >= Mathf.Abs(alongShaft))
            exit = "SIDEWALL";
        else if (Mathf.Abs(alongShaft) > Mathf.Abs(outOfPocket))
            exit = alongShaft > 0f ? "scoop" : "BACK DOWN THE SHAFT";
        else
            exit = outOfPocket > 0f ? "open face" : "BACK OF THE NET";

        float faceAngle = Mathf.Acos(Mathf.Clamp(outOfPocket, -1f, 1f)) * Mathf.Rad2Deg;
        return $"{exit} ({faceAngle:F0}° off the pocket's facing)";
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
            Gizmos.DrawWireSphere(PalmPosition(leftHandBone, leftPalmOffset), 0.02f);
    }
#endif
}
