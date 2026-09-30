using UnityEngine;

/// <summary>
/// Two-handed lacrosse stick grip for the shooter avatar.
///
/// Put this on the stick GameObject itself (not the avatar). Every LateUpdate (after the
/// Animator has posed the skeleton for this frame) the stick's whole world pose is solved
/// from the two hands:
///   - the Bottom Grip Point on the shaft is pinned to the right (butt-end) palm,
///   - the shaft is aimed through the left (top) palm,
///   - the roll around the shaft is taken from a reference axis, so the pocket faces a
///     stable direction instead of spinning with whatever rotation the math happened to find.
///
/// The stick is NOT reparented. Its pose is written in world space, so the OBJ's pivot, the
/// stick's scale, and any scale on the avatar's bone hierarchy don't matter. The grip point is
/// given in the stick's own local (mesh) space instead, which is what fixes the old
/// "swings around the wrong pivot" problem without a wrapper GameObject.
///
/// Defaults match 11747_stick_v2_L2.obj: the shaft runs along local +Z from the butt (z = 0)
/// to the head (z = 55.6), with its centerline at y = -1.818.
///
/// SETUP (in the Unity Editor):
/// 1. Select the stick GameObject. Add Component -> LacrosseStickGrip.
/// 2. Drag the avatar's right hand bone into "Right Hand Bone" (butt-end hand) and the left
///    hand bone into "Left Hand Bone" (top hand). For a lefty shooter that's the right way round.
/// 3. With the stick selected, check the gizmos: the yellow line should run down the middle of
///    the shaft, and the green sphere should sit near the butt end.
/// 4. Enter Play mode and pause. Move/rotate the stick in the Scene view until it sits in both
///    hands, and rotate the left hand bone until it wraps the shaft. Then right-click this
///    component -> Capture Grip From Current Pose. That fills in the palm offsets, Roll Offset,
///    and the left hand's grip rotation (and turns on Align Left Hand).
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

    [Header("Roll Around the Shaft")]
    [Tooltip("The pocket faces along Roll Reference Axis of this Transform. Leave empty to use " +
             "world space (steady, ignores wrist twist). Drag a hand bone in to have the pocket " +
             "follow that wrist's roll through the shot.")]
    public Transform rollReference;

    [Tooltip("Axis (in Roll Reference's local space, or world space if it's empty) the pocket faces.")]
    public Vector3 rollReferenceAxis = Vector3.up;

    [Tooltip("Extra spin around the shaft, in degrees, applied after the roll reference.")]
    [Range(-180f, 180f)]
    public float rollOffset = 0f;

    [Header("Left Hand Rotation Fix")]
    [Tooltip("Override the animation's left hand rotation so the hand wraps the shaft. The hand " +
             "is held at Left Hand Grip Rotation relative to the stick. Turned on by Capture Grip.")]
    public bool alignLeftHand = false;

    [Tooltip("Left hand rotation relative to the stick (Euler degrees). Set by Capture Grip.")]
    public Vector3 leftHandGripRotation = Vector3.zero;

    [Tooltip("1 = hand fully follows the grip rotation, 0 = the animation's rotation.")]
    [Range(0f, 1f)]
    public float leftHandAlignWeight = 1f;

    [Header("Body Clamp (optional)")]
    [Tooltip("If assigned, the left palm target is kept at least Min Distance From Spine away " +
             "from this bone, so the shaft can't rotate through the torso.")]
    public Transform spineBone;
    public float minDistanceFromSpine = 0.15f;

    [Header("Body Collision (optional)")]
    [Tooltip("Bottom of the torso capsule, e.g. the Hips bone. The part of the stick past the " +
             "left hand is swung around the right hand so it stays outside this capsule.")]
    public Transform bodyBottomBone;

    [Tooltip("Top of the torso capsule, e.g. the Neck bone.")]
    public Transform bodyTopBone;

    [Tooltip("Torso capsule radius in meters.")]
    [Min(0f)]
    public float bodyRadius = 0.15f;

    [Tooltip("Extra gap in meters kept between the stick and the torso, for the shaft and head thickness.")]
    [Min(0f)]
    public float stickClearance = 0.04f;

    [Tooltip("Stick length from Bottom Grip Point to the tip of the head, in the stick's local " +
             "(mesh) units. The OBJ's head tip is at z = 55.6.")]
    public float stickLength = 53.6f;

    private const int BodySamples = 10;
    private const int BodyIterations = 4;

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
        Quaternion leftHandRotation = leftHandBone.rotation;

        // With the left hand fix on, the left palm depends on the hand's rotation, which depends
        // on the stick's rotation. Solve twice: first with the animated hand, then with the
        // gripping hand. The palm is only a few cm from the wrist, so two passes are enough.
        int passes = alignLeftHand ? 2 : 1;
        for (int i = 0; i < passes; i++)
        {
            Vector3 topPalm = ClampFromSpine(leftHandBone.position + leftHandRotation * leftPalmOffset);
            if (!TrySolveRotation(bottomPalm, topPalm, out Quaternion stickRotation))
                return;
            transform.rotation = stickRotation;

            if (alignLeftHand)
                leftHandRotation = Quaternion.Slerp(leftHandBone.rotation,
                                                    stickRotation * Quaternion.Euler(leftHandGripRotation),
                                                    leftHandAlignWeight);
        }

        // Slide the stick so its bottom grip point lands in the right palm.
        transform.position += bottomPalm - transform.TransformPoint(bottomGripPoint);

        // The Animator rewrites the bone every frame, so this never accumulates.
        if (alignLeftHand)
            leftHandBone.rotation = leftHandRotation;
    }

    // Stick rotation that runs the shaft from bottomPalm through topPalm, with the pocket
    // facing the roll reference.
    private bool TrySolveRotation(Vector3 bottomPalm, Vector3 topPalm, out Quaternion rotation)
    {
        rotation = transform.rotation;

        Vector3 shaftDir = topPalm - bottomPalm;
        float handSpan = shaftDir.magnitude;
        if (handSpan < 0.001f)
            return false;
        shaftDir /= handSpan;
        shaftDir = AvoidBody(bottomPalm, shaftDir, handSpan);

        // Which way the pocket should face, flattened onto the plane around the shaft.
        Vector3 pocketDir = Vector3.ProjectOnPlane(RollReferenceDirection(), shaftDir);
        if (pocketDir.sqrMagnitude < 0.0001f)
            pocketDir = Vector3.ProjectOnPlane(_lastPocketDir, shaftDir);
        if (pocketDir.sqrMagnitude < 0.0001f)
            return false;
        pocketDir.Normalize();
        _lastPocketDir = pocketDir;

        // Map the stick's local (shaft, pocket) frame onto the world (shaft, pocket) frame.
        Quaternion worldFrame = Quaternion.LookRotation(shaftDir, pocketDir)
                              * Quaternion.AngleAxis(rollOffset, Vector3.forward);
        rotation = worldFrame * Quaternion.Inverse(LocalFrame());
        return true;
    }

    // ── Tuning ────────────────────────────────────────────────────

    /// <summary>
    /// Works out the palm offsets and roll from wherever the stick is right now. In Play mode:
    /// pause, move/rotate the stick in the Scene view until it sits in both hands with the
    /// pocket facing the right way, then run this from the component's context menu.
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

        // Roll = angle from the reference direction to where the pocket faces now.
        Vector3 pocketNow = Vector3.ProjectOnPlane(transform.TransformDirection(pocketAxisLocal), shaftDir);
        Vector3 pocketRef = Vector3.ProjectOnPlane(RollReferenceDirection(), shaftDir);
        if (pocketNow.sqrMagnitude > 0.0001f && pocketRef.sqrMagnitude > 0.0001f)
            rollOffset = Vector3.SignedAngle(pocketRef, pocketNow, shaftDir);

        // Left hand rotation relative to the stick, so the hand keeps this grip through the shot.
        leftHandGripRotation = (Quaternion.Inverse(transform.rotation) * leftHandBone.rotation).eulerAngles;
        alignLeftHand = true;

        Debug.Log($"[LacrosseStickGrip] Captured: Right Palm Offset {rightPalmOffset:F3}, " +
                  $"Left Palm Offset {leftPalmOffset:F3}, Roll Offset {rollOffset:F1}, " +
                  $"Left Hand Grip Rotation {leftHandGripRotation:F1} (Align Left Hand on). " +
                  "Copy Component now, then Paste Component Values after leaving Play mode.", this);
    }

    /// <summary>
    /// Turns on Align Left Hand, starting from the hand's current (animated) rotation, without
    /// touching anything else. Then drag Left Hand Grip Rotation's X/Y/Z in the Inspector to turn
    /// the hand live. Works while Play mode is running, since this script overrides the Animator.
    /// </summary>
    [ContextMenu("Start Left Hand Fix From Current Pose")]
    void StartLeftHandFixFromCurrentPose()
    {
        if (leftHandBone == null)
        {
            Debug.LogWarning("[LacrosseStickGrip] Assign Left Hand Bone first.", this);
            return;
        }

        leftHandGripRotation = (Quaternion.Inverse(transform.rotation) * leftHandBone.rotation).eulerAngles;
        leftHandAlignWeight = 1f;
        alignLeftHand = true;

        Debug.Log($"[LacrosseStickGrip] Left hand fix on, starting at {leftHandGripRotation:F1}. " +
                  "Drag Left Hand Grip Rotation X/Y/Z to turn the hand.", this);
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

    private Vector3 ClampFromSpine(Vector3 target)
    {
        if (spineBone == null)
            return target;

        Vector3 fromSpine = target - spineBone.position;
        float distance = fromSpine.magnitude;
        if (distance >= minDistanceFromSpine)
            return target;

        Vector3 direction = distance > 0.0001f ? fromSpine / distance : spineBone.right;
        return spineBone.position + direction * minDistanceFromSpine;
    }

    // Swings the shaft around the right palm until the part past the left hand is outside the
    // torso capsule. The part between the hands is left alone: it's wherever the animation
    // put the hands, and rotating around the right hand can't move it off the body anyway.
    private Vector3 AvoidBody(Vector3 pivot, Vector3 shaftDir, float handSpan)
    {
        if (bodyBottomBone == null || bodyTopBone == null)
            return shaftDir;

        Vector3 a = bodyBottomBone.position;
        Vector3 b = bodyTopBone.position;
        float radius = bodyRadius + stickClearance;
        float length = transform.TransformVector(shaftAxisLocal.normalized * stickLength).magnitude;
        if (length <= handSpan)
            return shaftDir;

        for (int iteration = 0; iteration < BodyIterations; iteration++)
        {
            // Find the deepest point of the overhang inside the capsule.
            float deepest = 0f;
            Vector3 deepestPoint = Vector3.zero;
            Vector3 deepestAxisPoint = Vector3.zero;
            for (int i = 0; i <= BodySamples; i++)
            {
                Vector3 p = pivot + shaftDir * Mathf.Lerp(handSpan, length, (float)i / BodySamples);
                Vector3 onAxis = ClosestPointOnSegment(p, a, b);
                float depth = radius - Vector3.Distance(p, onAxis);
                if (depth > deepest)
                {
                    deepest = depth;
                    deepestPoint = p;
                    deepestAxisPoint = onAxis;
                }
            }

            if (deepest <= 0f)
                break;

            // Push that point straight out to the capsule surface and re-aim the shaft through it.
            Vector3 away = deepestPoint - deepestAxisPoint;
            if (away.sqrMagnitude < 0.000001f)
                away = Vector3.Cross(b - a, shaftDir);
            if (away.sqrMagnitude < 0.000001f)
                break;

            Vector3 pushed = deepestAxisPoint + away.normalized * (radius + 0.005f);
            shaftDir = (pushed - pivot).normalized;
        }

        return shaftDir;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 0.000001f)
            return a;
        return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq);
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

        // Torso capsule the stick is kept out of (inner = body, outer = body + clearance).
        if (bodyBottomBone != null && bodyTopBone != null)
        {
            Vector3 a = bodyBottomBone.position;
            Vector3 b = bodyTopBone.position;
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(a, bodyRadius);
            Gizmos.DrawWireSphere(b, bodyRadius);
            Gizmos.DrawLine(a, b);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(Vector3.Lerp(a, b, 0.5f), bodyRadius + stickClearance);
        }
    }
#endif
}
