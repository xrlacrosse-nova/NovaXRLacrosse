using UnityEngine;

/// <summary>
/// Two-handed lacrosse stick grip for the shooter avatar.
///
/// Put this on the stick GameObject itself (not the avatar). Every LateUpdate (after the
/// Animator has posed the skeleton for this frame) the stick's world pose is solved from the
/// two hands:
///   - the Bottom Grip Point on the shaft is pinned to the bottom (butt-end) palm,
///   - the shaft is aimed through the top palm,
///   - the roll around the shaft comes from a reference axis (the top hand, so the pocket
///     turns with the wrist).
///
/// Two grip sets: the flat fields are the left-handed shot (right hand at the butt, left hand
/// on top), Right Handed Grip is the mirrored shot (left hand at the butt, right hand on top).
/// Right Handed picks the active set; ShooterAnimationController sets it per shot. The bones'
/// local axes aren't mirror images of each other, so each set has its own offsets and roll.
///
/// Optional top hand fix: after the stick is posed, the top hand bone's rotation can be
/// overridden (Align Left Hand / Align Top Hand) relative to the stick, the forearm, or the
/// clip's own rotation. That is the only bone this script touches. Tune it in Play mode;
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
/// 6. Right-handed shot: in Right Handed Grip, drag the LEFT hand bone into Bottom Hand Bone,
///    the RIGHT hand bone into Top Hand Bone and Roll Reference. Then, in Play mode, shooter ->
///    Mirror Stick Grip To Right Hand fills it in from the left-handed grip. To touch it up,
///    repeat step 5 paused on a right-handed shot; Capture writes into the active set.
/// </summary>
public class LacrosseStickGrip : MonoBehaviour
{
    [Header("Handedness")]
    [Tooltip("Uses Right Handed Grip instead of the left-handed fields. Set per shot by " +
             "ShooterAnimationController; tick by hand to preview or tune the right-handed grip.")]
    public bool rightHanded = false;

    [Header("Left-Handed Shot: Hand Bones (drag from the avatar's skeleton)")]
    [Tooltip("Hand at the butt end of the shaft. The stick is pinned to this hand.")]
    public Transform rightHandBone;

    [Tooltip("Hand further up the shaft. The shaft is aimed through this hand.")]
    public Transform leftHandBone;

    [Header("Left-Handed Shot: Palm Offsets (meters, in each hand bone's rotation)")]
    [Tooltip("Offset from the right hand bone (the wrist joint) to the center of the palm. " +
             "Mixamo hand bones point +Y toward the fingers.")]
    public Vector3 rightPalmOffset = new Vector3(0f, 0.08f, 0f);

    [Tooltip("Offset from the left hand bone (the wrist joint) to the center of the palm.")]
    public Vector3 leftPalmOffset = new Vector3(0f, 0.08f, 0f);

    [Header("Stick Geometry (stick's local / mesh space, both hands)")]
    [Tooltip("Local axis that runs down the shaft from the butt end toward the head.")]
    public Vector3 shaftAxisLocal = Vector3.forward;

    [Tooltip("Point on the shaft that sits in the bottom palm, in the stick's local (mesh) units.")]
    public Vector3 bottomGripPoint = new Vector3(0f, -1.818f, 2f);

    [Tooltip("Local axis, perpendicular to the shaft, that the pocket opens toward. For the OBJ " +
             "this is -Y; +Y is the closed back of the head.")]
    public Vector3 pocketAxisLocal = Vector3.down;

    [Tooltip("Stick length from Bottom Grip Point to the tip of the head, in the stick's local " +
             "(mesh) units. Only used for the gizmo.")]
    public float stickLength = 53.6f;

    [Header("Left-Handed Shot: Roll Around the Shaft")]
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

    /// <summary>What the top hand's grip rotation is measured against.</summary>
    public enum HandRotationSpace
    {
        /// <summary>Fixed rotation relative to the stick; the hand follows the stick.</summary>
        RelativeToStick,
        /// <summary>Replaces the wrist's local rotation (relative to the forearm).</summary>
        RelativeToForearm,
        /// <summary>Extra turn on top of the clip's rotation, in the hand's own axes. (0,0,0) = clip.</summary>
        OffsetFromAnimation,
    }

    [Header("Left-Handed Shot: Left Hand Fix (optional)")]
    [Tooltip("Overrides the left hand bone's rotation every frame with Left Hand Grip Rotation. " +
             "Only turns the hand at the wrist; fingers and arm still come from the clip. Must be " +
             "ticked or the numbers below do nothing.")]
    public bool alignLeftHand = false;

    [Tooltip("What Left Hand Grip Rotation is relative to. Relative To Stick: hand locked to the " +
             "stick. Relative To Forearm: a fixed wrist angle. Offset From Animation: a turn added " +
             "on top of the clip.")]
    public HandRotationSpace leftHandRotationSpace = HandRotationSpace.RelativeToStick;

    [Tooltip("Left hand rotation (Euler degrees), in Left Hand Rotation Space. Only applies in " +
             "Play mode; changes show right away, paused or not.")]
    public Vector3 leftHandGripRotation = Vector3.zero;

    [Tooltip("1 = hand fully follows the grip rotation, 0 = the animation's rotation.")]
    [Range(0f, 1f)]
    public float leftHandAlignWeight = 1f;

    /// <summary>The grip for the mirrored (right-handed) shot: left hand at the butt, right hand
    /// on top. Same meaning as the left-handed fields, with bottom/top instead of right/left.</summary>
    [System.Serializable]
    public class RightHandedGrip
    {
        [Tooltip("Hand at the butt end in a right-handed shot: the avatar's LEFT hand bone.")]
        public Transform bottomHandBone;

        [Tooltip("Hand further up the shaft in a right-handed shot: the avatar's RIGHT hand bone.")]
        public Transform topHandBone;

        [Tooltip("Offset from the bottom hand bone (the wrist joint) to the center of the palm.")]
        public Vector3 bottomPalmOffset = new Vector3(0f, 0.08f, 0f);

        [Tooltip("Offset from the top hand bone (the wrist joint) to the center of the palm.")]
        public Vector3 topPalmOffset = new Vector3(0f, 0.08f, 0f);

        [Tooltip("The pocket faces along Roll Reference Axis of this Transform. Use the top " +
                 "(right) hand bone, like the left-handed grip uses its top hand.")]
        public Transform rollReference;

        [Tooltip("Axis (in Roll Reference's local space) the pocket faces. Capture Grip From " +
                 "Current Pose sets this.")]
        public Vector3 rollReferenceAxis = Vector3.forward;

        [Tooltip("Extra spin around the shaft, in degrees, applied after the roll reference.")]
        [Range(-180f, 180f)]
        public float rollOffset = 0f;

        [Tooltip("Overrides the top (right) hand bone's rotation every frame with Top Hand Grip " +
                 "Rotation. Same as Align Left Hand, for the right-handed shot.")]
        public bool alignTopHand = false;

        [Tooltip("What Top Hand Grip Rotation is relative to.")]
        public HandRotationSpace topHandRotationSpace = HandRotationSpace.RelativeToStick;

        [Tooltip("Top hand rotation (Euler degrees), in Top Hand Rotation Space.")]
        public Vector3 topHandGripRotation = Vector3.zero;

        [Tooltip("1 = hand fully follows the grip rotation, 0 = the animation's rotation.")]
        [Range(0f, 1f)]
        public float topHandAlignWeight = 1f;
    }

    [Header("Right-Handed Shot")]
    public RightHandedGrip rightGrip = new RightHandedGrip();

    // Last good pocket direction, used when the roll reference lines up with the shaft.
    private Vector3 _lastPocketDir = Vector3.up;

    // The clip's top hand rotation this frame, before the fix, and which bone it belongs to. Lets
    // Inspector edits re-apply the fix while paused, when LateUpdate doesn't run and the Animator
    // doesn't rewrite the bone.
    private Quaternion _animatedTopHandRotation;
    private Transform _animatedTopHand;
    private bool _warnedFixOff;
    private bool _warnedNoRightGrip;

    // ── Active grip set ───────────────────────────────────────────

    private bool HasRightGrip =>
        rightGrip != null && rightGrip.bottomHandBone != null && rightGrip.topHandBone != null;

    // Right Handed without its bones falls back to the left-handed grip (warned once).
    private bool UseRight => rightHanded && HasRightGrip;

    private Transform BottomBone => UseRight ? rightGrip.bottomHandBone : rightHandBone;
    private Transform TopBone => UseRight ? rightGrip.topHandBone : leftHandBone;
    private Transform RollRef => UseRight ? rightGrip.rollReference : rollReference;

    private Vector3 BottomPalmOffset
    {
        get => UseRight ? rightGrip.bottomPalmOffset : rightPalmOffset;
        set { if (UseRight) rightGrip.bottomPalmOffset = value; else rightPalmOffset = value; }
    }

    private Vector3 TopPalmOffset
    {
        get => UseRight ? rightGrip.topPalmOffset : leftPalmOffset;
        set { if (UseRight) rightGrip.topPalmOffset = value; else leftPalmOffset = value; }
    }

    private Vector3 RollAxis
    {
        get => UseRight ? rightGrip.rollReferenceAxis : rollReferenceAxis;
        set { if (UseRight) rightGrip.rollReferenceAxis = value; else rollReferenceAxis = value; }
    }

    private float RollOffset
    {
        get => UseRight ? rightGrip.rollOffset : rollOffset;
        set { if (UseRight) rightGrip.rollOffset = value; else rollOffset = value; }
    }

    private bool AlignTop
    {
        get => UseRight ? rightGrip.alignTopHand : alignLeftHand;
        set { if (UseRight) rightGrip.alignTopHand = value; else alignLeftHand = value; }
    }

    private HandRotationSpace TopRotationSpace =>
        UseRight ? rightGrip.topHandRotationSpace : leftHandRotationSpace;

    private Vector3 TopGripRotation
    {
        get => UseRight ? rightGrip.topHandGripRotation : leftHandGripRotation;
        set { if (UseRight) rightGrip.topHandGripRotation = value; else leftHandGripRotation = value; }
    }

    private float TopAlignWeight
    {
        get => UseRight ? rightGrip.topHandAlignWeight : leftHandAlignWeight;
        set { if (UseRight) rightGrip.topHandAlignWeight = value; else leftHandAlignWeight = value; }
    }

    // ── Lifecycle ─────────────────────────────────────────────────

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
        if (rightHanded && !HasRightGrip && !_warnedNoRightGrip)
        {
            Debug.LogWarning("[LacrosseStickGrip] Right Handed is on but Right Handed Grip's Bottom/Top " +
                             "Hand Bone aren't both assigned — using the left-handed grip.", this);
            _warnedNoRightGrip = true;
        }

        Solve();
    }

    /// <summary>Poses the stick from the active set's hands as they are right now, then applies
    /// the top hand fix.</summary>
    private void Solve()
    {
        Transform bottomBone = BottomBone;
        Transform topBone = TopBone;

        Vector3 bottomPalm = PalmPosition(bottomBone, BottomPalmOffset);
        Vector3 topPalm = PalmPosition(topBone, TopPalmOffset);

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
                              * Quaternion.AngleAxis(RollOffset, Vector3.forward);
        transform.rotation = worldFrame * Quaternion.Inverse(LocalFrame());

        // Slide the stick so its bottom grip point lands in the bottom palm.
        transform.position += bottomPalm - transform.TransformPoint(bottomGripPoint);

        // After the stick is posed from the animated hands, so the override doesn't feed back
        // into the shaft or roll. The Animator rewrites the bone every frame, so nothing piles up.
        _animatedTopHandRotation = topBone.rotation;
        _animatedTopHand = topBone;
        if (AlignTop)
            ApplyTopHandFix();
    }

    // Always starts from this frame's clip rotation, so applying it twice (LateUpdate, then an
    // Inspector edit while paused) gives the same result.
    private void ApplyTopHandFix()
    {
        TopBone.rotation = Quaternion.Slerp(_animatedTopHandRotation, TopHandTarget(), TopAlignWeight);
    }

    // The top hand's full-weight fix rotation this frame, in world space.
    private Quaternion TopHandTarget()
    {
        Transform topBone = TopBone;
        Quaternion grip = Quaternion.Euler(TopGripRotation);
        switch (TopRotationSpace)
        {
            case HandRotationSpace.RelativeToForearm:
                return topBone.parent != null ? topBone.parent.rotation * grip : grip;
            case HandRotationSpace.OffsetFromAnimation:
                return _animatedTopHandRotation * grip;
            default:
                return transform.rotation * grip;
        }
    }

    // Inspector edits in Play mode: re-apply now so they show even while paused, and say so if
    // the fix is off (the usual reason "changing the numbers does nothing").
    void OnValidate()
    {
        // Only while the cached rotation belongs to the active set's top hand (not right after
        // Right Handed was toggled).
        if (!Application.isPlaying || _animatedTopHand == null || _animatedTopHand != TopBone)
            return;

        if (AlignTop)
        {
            ApplyTopHandFix();
            _warnedFixOff = false;
        }
        else
        {
            _animatedTopHand.rotation = _animatedTopHandRotation;
            if (!_warnedFixOff && (TopGripRotation != Vector3.zero || TopRotationSpace != HandRotationSpace.RelativeToStick))
            {
                string fix = UseRight ? "Align Top Hand" : "Align Left Hand";
                Debug.LogWarning($"[LacrosseStickGrip] {fix} is off, so its grip rotation does " +
                                 "nothing. Tick it (or run Start Top Hand Fix From Current Pose).", this);
                _warnedFixOff = true;
            }
        }
    }

    // ── Tuning ────────────────────────────────────────────────────

    /// <summary>
    /// Works out the active set's palm offsets and roll from wherever the stick is right now. In
    /// Play mode: pause, disable this component, move/rotate the stick in the Scene view until it
    /// sits in both hands with the pocket facing the right way, then run this from the context
    /// menu. Play-mode values are lost on exit, so use Copy Component / Paste Component Values after.
    /// </summary>
    [ContextMenu("Capture Grip From Current Pose")]
    void CaptureGripFromCurrentPose()
    {
        Transform bottomBone = BottomBone;
        Transform topBone = TopBone;
        if (bottomBone == null || topBone == null)
        {
            Debug.LogWarning("[LacrosseStickGrip] Assign both hand bones before capturing.", this);
            return;
        }

        // Bottom palm = the stick's bottom grip point, wherever it is now.
        Vector3 grip = transform.TransformPoint(bottomGripPoint);
        Vector3 shaftDir = transform.TransformDirection(shaftAxisLocal).normalized;
        BottomPalmOffset = Quaternion.Inverse(bottomBone.rotation) * (grip - bottomBone.position);

        // Top palm = the point on the shaft closest to the top hand bone.
        Vector3 onShaft = grip + Vector3.Project(topBone.position - grip, shaftDir);
        TopPalmOffset = Quaternion.Inverse(topBone.rotation) * (onShaft - topBone.position);

        // Roll = where the pocket faces now, stored in Roll Reference's space, so the pocket
        // turns with the reference (with the top hand, it follows the wrist).
        Vector3 pocketNow = Vector3.ProjectOnPlane(transform.TransformDirection(pocketAxisLocal), shaftDir);
        if (pocketNow.sqrMagnitude > 0.0001f)
        {
            pocketNow.Normalize();
            Transform rollRef = RollRef;
            RollAxis = rollRef != null ? rollRef.InverseTransformDirection(pocketNow) : pocketNow;
            RollOffset = 0f;
        }

        string set = UseRight ? "right-handed" : "left-handed";
        Debug.Log($"[LacrosseStickGrip] Captured ({set} grip): Bottom Palm Offset {BottomPalmOffset:F3}, " +
                  $"Top Palm Offset {TopPalmOffset:F3}, Roll Reference Axis {RollAxis:F3}. " +
                  "Copy Component now, then Paste Component Values after leaving Play mode.", this);
    }

    /// <summary>
    /// Turns on the active set's top hand fix (Align Left Hand / Align Top Hand) starting from
    /// the hand's current rotation (in its rotation space), so nothing jumps. Then drag the grip
    /// rotation's X/Y/Z in the Inspector to turn the hand live, in Play mode, paused or running.
    /// </summary>
    [ContextMenu("Start Top Hand Fix From Current Pose")]
    void StartTopHandFixFromCurrentPose()
    {
        Transform topBone = TopBone;
        if (topBone == null)
        {
            Debug.LogWarning("[LacrosseStickGrip] Assign the top hand bone first.", this);
            return;
        }

        // With the fix already on, the bone holds last frame's override; read it as-is so the
        // captured value is what's on screen.
        Quaternion current = topBone.rotation;
        Quaternion captured;
        switch (TopRotationSpace)
        {
            case HandRotationSpace.RelativeToForearm:
                captured = topBone.localRotation;
                break;
            case HandRotationSpace.OffsetFromAnimation:
                captured = _animatedTopHand == topBone
                    ? Quaternion.Inverse(_animatedTopHandRotation) * current
                    : Quaternion.identity;
                break;
            default:
                captured = Quaternion.Inverse(transform.rotation) * current;
                break;
        }
        TopGripRotation = WrapEuler(captured.eulerAngles);
        TopAlignWeight = 1f;
        AlignTop = true;

        string hand = UseRight ? "Top (right) hand" : "Left hand";
        Debug.Log($"[LacrosseStickGrip] {hand} fix on ({TopRotationSpace}), starting at " +
                  $"{TopGripRotation:F1}. Drag its grip rotation X/Y/Z to turn the hand. " +
                  "Copy Component before leaving Play mode.", this);
    }

    /// <summary>
    /// Fills Right Handed Grip by mirroring the left-handed grip. poseLeft / poseRight put the
    /// avatar in the same frame left- and right-handed (Idle / IdleRight). The left-handed stick
    /// is solved, reflected across mirrorRoot's center plane (its local YZ plane, the plane
    /// Humanoid mirroring reflects across), and captured against the right-handed pose, like
    /// Capture Grip From Current Pose. The top hand fix is mirrored too. A starting point: the
    /// retarget isn't perfectly symmetric, so touch up with Capture if needed. Ends right-handed.
    /// </summary>
    public bool MirrorLeftGripOntoRight(Transform mirrorRoot, System.Action poseLeft, System.Action poseRight)
    {
        if (rightHandBone == null || leftHandBone == null || !HasRightGrip)
        {
            Debug.LogWarning("[LacrosseStickGrip] Mirroring needs both left-handed hand bones and Right " +
                             "Handed Grip's Bottom/Top Hand Bone.", this);
            return false;
        }

        // Left-handed stick and left hand fix, as they are on screen in the left-handed pose.
        rightHanded = false;
        poseLeft();
        Solve();
        Vector3 grip = transform.TransformPoint(bottomGripPoint);
        Vector3 shaftDir = transform.TransformDirection(shaftAxisLocal).normalized;
        Vector3 pocketDir = transform.TransformDirection(pocketAxisLocal);
        Quaternion leftAnimated = _animatedTopHandRotation;
        Quaternion leftTarget = TopHandTarget();

        // Reflected across the avatar's center plane.
        Vector3 n = mirrorRoot.right;
        Vector3 o = mirrorRoot.position;
        grip -= 2f * Vector3.Dot(grip - o, n) * n;
        shaftDir = MirrorDirection(shaftDir, n);
        pocketDir = Vector3.ProjectOnPlane(MirrorDirection(pocketDir, n), shaftDir);
        if (pocketDir.sqrMagnitude < 0.0001f)
        {
            rightHanded = false;
            Debug.LogWarning("[LacrosseStickGrip] Couldn't mirror the pocket direction.", this);
            return false;
        }
        pocketDir.Normalize();

        // Captured against the right-handed pose (same math as Capture Grip From Current Pose).
        rightHanded = true;
        poseRight();
        Transform bottom = rightGrip.bottomHandBone;
        Transform top = rightGrip.topHandBone;
        rightGrip.bottomPalmOffset = Quaternion.Inverse(bottom.rotation) * (grip - bottom.position);
        Vector3 onShaft = grip + Vector3.Project(top.position - grip, shaftDir);
        rightGrip.topPalmOffset = Quaternion.Inverse(top.rotation) * (onShaft - top.position);
        rightGrip.rollReferenceAxis = rightGrip.rollReference != null
            ? rightGrip.rollReference.InverseTransformDirection(pocketDir)
            : pocketDir;
        rightGrip.rollOffset = 0f;

        // Top hand fix: mirror the left hand's world-space turn away from the clip and put it on
        // the right hand, stored in the same rotation space as the left hand's.
        rightGrip.alignTopHand = alignLeftHand;
        rightGrip.topHandRotationSpace = leftHandRotationSpace;
        rightGrip.topHandAlignWeight = leftHandAlignWeight;
        if (alignLeftHand)
        {
            (leftTarget * Quaternion.Inverse(leftAnimated)).ToAngleAxis(out float angle, out Vector3 axis);
            Quaternion rightAnimated = top.rotation;
            Quaternion target = Quaternion.AngleAxis(-angle, MirrorDirection(axis, n)) * rightAnimated;

            Quaternion stickRotation = Quaternion.LookRotation(shaftDir, pocketDir) * Quaternion.Inverse(LocalFrame());
            Quaternion captured;
            switch (leftHandRotationSpace)
            {
                case HandRotationSpace.RelativeToForearm:
                    captured = top.parent != null ? Quaternion.Inverse(top.parent.rotation) * target : target;
                    break;
                case HandRotationSpace.OffsetFromAnimation:
                    captured = Quaternion.Inverse(rightAnimated) * target;
                    break;
                default:
                    captured = Quaternion.Inverse(stickRotation) * target;
                    break;
            }
            rightGrip.topHandGripRotation = WrapEuler(captured.eulerAngles);
        }

        // Show the result now, even while paused.
        Solve();

        Debug.Log($"[LacrosseStickGrip] Mirrored the left-handed grip: Bottom Palm Offset " +
                  $"{rightGrip.bottomPalmOffset:F3}, Top Palm Offset {rightGrip.topPalmOffset:F3}, Roll " +
                  $"Reference Axis {rightGrip.rollReferenceAxis:F3}, Align Top Hand {rightGrip.alignTopHand}. " +
                  "Copy Component now, then Paste Component Values after leaving Play mode.", this);
        return true;
    }

    // A direction reflected across the plane with normal n.
    private static Vector3 MirrorDirection(Vector3 v, Vector3 n)
    {
        return v - 2f * Vector3.Dot(v, n) * n;
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
        Transform rollRef = RollRef;
        return rollRef != null
            ? rollRef.TransformDirection(RollAxis)
            : RollAxis;
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

        // Where the script thinks each palm of the active set is.
        Gizmos.color = Color.cyan;
        if (BottomBone != null)
            Gizmos.DrawWireSphere(PalmPosition(BottomBone, BottomPalmOffset), 0.02f);
        if (TopBone != null)
            Gizmos.DrawWireSphere(PalmPosition(TopBone, TopPalmOffset), 0.02f);
    }
#endif
}
