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
/// 4. Enter Play mode and tune (all fields update live): palm offsets so the cyan spheres sit
///    inside each palm, then Roll Offset so the pocket faces the right way.
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

    [Header("Body Clamp (optional)")]
    [Tooltip("If assigned, the left palm target is kept at least Min Distance From Spine away " +
             "from this bone, so the shaft can't rotate through the torso.")]
    public Transform spineBone;
    public float minDistanceFromSpine = 0.15f;

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
        Vector3 topPalm = ClampFromSpine(PalmPosition(leftHandBone, leftPalmOffset));

        Vector3 shaftDir = topPalm - bottomPalm;
        if (shaftDir.sqrMagnitude < 0.000001f)
            return;
        shaftDir.Normalize();

        // Which way the pocket should face, flattened onto the plane around the shaft.
        Vector3 reference = rollReference != null
            ? rollReference.TransformDirection(rollReferenceAxis)
            : rollReferenceAxis;
        Vector3 pocketDir = Vector3.ProjectOnPlane(reference, shaftDir);
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
    }

    // ── Private helpers ───────────────────────────────────────────

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
        Vector3 shaftEnd = transform.TransformPoint(bottomGripPoint + shaftAxisLocal.normalized * 50f);

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
