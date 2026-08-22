using UnityEngine;

/// <summary>
/// Attaches a lacrosse stick (or any prop) to a Mixamo avatar's hand bone at runtime.
///
/// HOW TO USE:
/// 1. Add this script to your avatar's root GameObject (the one with the Animator).
/// 2. Drag your lacrosse stick prefab/GameObject into the "Lacrosse Stick" field.
/// 3. Choose which hand to attach to (Right or Left).
/// 4. Adjust positionOffset and rotationOffset in the Inspector until the stick
///    sits correctly in the hand — use the Scene view in Play mode to tune these.
///
/// MIXAMO BONE NAMES (5-finger rig):
///   Right hand root: "mixamorig:RightHand"
///   Left  hand root: "mixamorig:LeftHand"
///   The script finds the correct bone automatically.
/// </summary>
public class LacrosseStickAttacher : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector fields
    // -------------------------------------------------------------------------

    [Header("Stick Setup")]
    [Tooltip("Drag your lacrosse stick GameObject or prefab here.")]
    public GameObject lacrosseStick;

    [Tooltip("Which hand should hold the stick?")]
    public Hand holdingHand = Hand.Right;

    [Header("Position & Rotation Offsets")]
    [Tooltip("Local position offset relative to the hand bone. Tune this in Play mode.")]
    public Vector3 positionOffset = new Vector3(0f, 0f, 0f);

    [Tooltip("Local rotation offset relative to the hand bone (Euler angles). Tune this in Play mode.")]
    public Vector3 rotationOffset = new Vector3(0f, 0f, 0f);

    [Header("Advanced")]
    [Tooltip("If true, the stick is instantiated from the assigned prefab. " +
             "If false, it re-parents the existing scene object.")]
    public bool instantiateFromPrefab = false;

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------

    public enum Hand { Right, Left }

    // Mixamo standard bone names (works for both 5-finger and standard rigs)
    private const string RIGHT_HAND_BONE = "mixamorig:RightHand";
    private const string LEFT_HAND_BONE = "mixamorig:LeftHand";

    private Transform _handBone;
    private GameObject _stickInstance;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Start()
    {
        if (lacrosseStick == null)
        {
            Debug.LogError("[LacrosseStickAttacher] No lacrosse stick assigned! " +
                           "Drag your stick GameObject into the Inspector.", this);
            return;
        }

        // 1. Find the hand bone in the avatar's skeleton
        string boneName = (holdingHand == Hand.Right) ? RIGHT_HAND_BONE : LEFT_HAND_BONE;
        _handBone = FindBoneRecursive(transform, boneName);

        if (_handBone == null)
        {
            Debug.LogError($"[LacrosseStickAttacher] Could not find bone '{boneName}'. " +
                           "Make sure this script is on the avatar root and the rig uses " +
                           "standard Mixamo bone names.", this);
            return;
        }

        // 2. Either instantiate a prefab or re-parent the existing object
        if (instantiateFromPrefab)
        {
            _stickInstance = Instantiate(lacrosseStick);
        }
        else
        {
            _stickInstance = lacrosseStick;
        }

        // 3. Parent the stick to the hand bone
        _stickInstance.transform.SetParent(_handBone, worldPositionStays: false);

        // 4. Apply the local offsets so it sits correctly in the palm
        _stickInstance.transform.localPosition = positionOffset;
        _stickInstance.transform.localRotation = Quaternion.Euler(rotationOffset);

        Debug.Log($"[LacrosseStickAttacher] '{_stickInstance.name}' attached to '{_handBone.name}'.");
    }

    // -------------------------------------------------------------------------
    // Optional: allow toggling the stick at runtime (e.g. via MagicLeap gesture)
    // -------------------------------------------------------------------------

    /// <summary>Call this to show or hide the stick at runtime.</summary>
    public void SetStickVisible(bool visible)
    {
        if (_stickInstance != null)
            _stickInstance.SetActive(visible);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Recursively searches all children of <paramref name="parent"/> for a
    /// Transform whose name matches <paramref name="boneName"/>.
    /// Works regardless of skeleton depth.
    /// </summary>
    private static Transform FindBoneRecursive(Transform parent, string boneName)
    {
        // Breadth-first via Unity's built-in search (searches the whole hierarchy)
        // GetComponentsInChildren includes the root, so we start from the root.
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (t.name == boneName)
                return t;
        }
        return null;
    }

#if UNITY_EDITOR
    // -------------------------------------------------------------------------
    // Editor helper: draw a small gizmo at the hand bone so you can see it
    // -------------------------------------------------------------------------
    private void OnDrawGizmosSelected()
    {
        if (_handBone == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(_handBone.position, 0.02f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(_handBone.position,
                        _handBone.position + _handBone.forward * 0.1f);
    }
#endif
}