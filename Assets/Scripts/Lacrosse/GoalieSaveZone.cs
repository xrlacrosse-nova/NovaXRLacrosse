using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The goalie's stick head, as far as the simulation is concerned: an invisible trigger volume
/// that registers a save when the ball touches it mid-shot. It only detects — it tells
/// GoalDetector (the single source of truth for shot outcomes) via TryRegisterSave(), and
/// GoalDetector's OnSaved event drives everything else (popup, heatmap dot, despawn, next shot).
///
/// Setup (all in the Unity Editor — see Plans/Current/Save_Zone_Accuracy_Plan.md):
///   1. On the stick-head object under the ML Rig's Controller (currently `Cylinder`), add a
///      MeshCollider using the oval stick-head mesh, with Convex and Is Trigger ticked, so the
///      save area matches the visible head exactly. (Any trigger Collider works; a convex
///      MeshCollider is the one that matches the oval.)
///   2. Add a Rigidbody: Is Kinematic on, Use Gravity off. (A moving collider without a
///      Rigidbody is treated as static; the ball's own Rigidbody is what makes trigger events fire.)
///   3. Add this component to the same GameObject. Ball can be left empty — it's auto-found.
///   4. Keep only that one Collider on the GameObject (remove any test BoxCollider).
///   5. Pocket (Future_Plan/Reviewed/Stick_Pocket_Save_Zone_Plan.md): at Play start the
///      MeshCollider's mesh is swapped for a built volume: the oval face plus the sidewalls and
///      the bag behind it, so a ball into the bag or off the sidewall of a twisted/tilted head
///      still saves. The visible oval isn't touched, and the front of the head doesn't grow.
///      Press Play, select the object and check the green gizmo from the side: the bulge must be
///      on the net side (away from the shooter). If not, flip Pocket Side. Tune the depths live
///      in Play mode, then re-enter them after stopping (Play-mode changes are lost).
///      Build Pocket Volume off = the flat oval collider, as before.
///
/// How a save is found: each physics step, a sphere the size of the ball (plus saveMargin) is
/// swept along the ball's path since the last step and tested against this collider, so a ball
/// that only clips the rim still counts and a fast ball can't skip through. A saved ball is moved
/// back to where it touched the head, so it freezes on the stick, not behind it.
///
/// Runs before GoalDetector (execution order), so in a step where the ball passes through the
/// head and the goal plane, the save wins. A save only counts while a shot is in flight, so
/// waving the stick through the idle ball does nothing.
/// </summary>
[DefaultExecutionOrder(-20)]
public class GoalieSaveZone : MonoBehaviour
{
    [Header("Ball")]
    [Tooltip("The ball's GoalDetector. Leave empty to auto-find the one in the scene.")]
    public GoalDetector ball;

    [Header("Detection")]
    [Tooltip("The ball moves ~0.36 m per physics step at 18 m/s — more than the stick head is thick — " +
             "so a plain trigger can be skipped entirely. With this on, the ball's path between " +
             "physics steps is also checked against the zone. Leave on unless debugging.")]
    public bool useSweepCheck = true;

    [Tooltip("Extra reach added around the stick head, in meters, on top of the ball's own radius. " +
             "A little generosity so a ball that visibly grazes the rim counts. 0 = exact.")]
    [Range(0f, 0.05f)]
    public float saveMargin = 0.015f;

    [Tooltip("Also account for the stick moving between physics steps: the ball's path is checked " +
             "relative to the stick, so a fast swing through the ball's path still saves. " +
             "Leave on unless debugging.")]
    public bool sweepStickMotion = true;

    public enum PocketSide { Plus = 1, Minus = -1 }

    [Header("Pocket")]
    [Tooltip("Give the save area depth: the oval face plus the sidewalls and the bag behind it, built " +
             "at Play start onto this MeshCollider. Off = the flat oval collider exactly as it is.")]
    public bool buildPocketVolume = true;

    [Tooltip("How far the sidewalls stand behind the face, all the way around the rim, in meters.")]
    [Range(0f, 0.1f)]
    public float sidewallDepth = 0.04f;

    [Tooltip("How deep the bag hangs behind the face at its deepest point, in meters.")]
    [Range(0f, 0.2f)]
    public float pocketDepth = 0.09f;

    [Tooltip("Where the bag's deepest point sits along the head (local z, the long axis), in meters. " +
             "The oval runs about -0.18 to +0.24; 0.03 is its middle.")]
    [Range(-0.2f, 0.25f)]
    public float pocketCenter = 0.03f;

    [Tooltip("Size of the bottom of the bag relative to the face (1 = as big as the face).")]
    [Range(0.05f, 1f)]
    public float pocketSize = 0.5f;

    [Tooltip("Which side of the face the net hangs on. Check the gizmo in Play mode: the bulge must " +
             "be on the net side (away from the shooter). Flip if it's on the shooter's side.")]
    public PocketSide pocketSide = PocketSide.Plus;

    [Header("Debug")]
    [Tooltip("Log each registered save (where on the head it hit), and on each goal how close the " +
             "ball came to the head. GoalDetector always logs its own SAVE / GOAL line.")]
    public bool showDebugLogs = true;

    // ── Private ───────────────────────────────────────────────────

    // Used if the ball has no SphereCollider to read its size from (6.35 cm lacrosse ball).
    private const float DefaultBallRadius = 0.032f;

    // Points sampled along each step's path for the closest-approach log (debug only).
    private const int ApproachSamples = 12;

    private Collider _zoneCollider;
    private Rigidbody _ballRb;
    private float _ballRadius = DefaultBallRadius;

    // Hits from the sphere sweep. The sweep also returns the ball's own collider, the floor, the
    // goal, etc., so the buffer is roomy and only hits on _zoneCollider are kept.
    private readonly RaycastHit[] _sweepHits = new RaycastHit[16];

    // ball position and stick pose at the previous physics step, for the sweep check
    private Vector3 _prevBallPos;
    private Matrix4x4 _prevZoneWorldToLocal;
    private bool _hasPrevBallPos = false;

    // Closest the ball's center came to the zone this shot (debug log on a goal).
    private float _closestCenterDistance = float.PositiveInfinity;
    private bool _canMeasureApproach = true;  // ClosestPoint needs a primitive or convex mesh

    // Pocket volume: the visible oval it's built from, the built mesh, and a flag set by
    // OnValidate so Inspector tweaks are applied on the next physics step (not inside OnValidate).
    private const int MaxOutlinePoints = 64;  // keeps the convex hull well under PhysX's 255 limit
    private MeshCollider _meshCollider;
    private Mesh _faceMesh;
    private Mesh _pocketMesh;
    private bool _pocketDirty = false;

    // ── Lifecycle ─────────────────────────────────────────────────

    void Awake()
    {
        _zoneCollider = GetComponent<Collider>();
        ValidateSetup();
        SetupPocket();
    }

    void Start()
    {
        if (ball == null)
            ball = FindFirstObjectByType<GoalDetector>();

        if (ball != null)
            _ballRb = ball.GetComponent<Rigidbody>();

        if (ball == null || _ballRb == null)
        {
            Debug.LogWarning("[GoalieSaveZone] Couldn't find the ball (a GoalDetector with a Rigidbody) — " +
                             "saves are disabled. Assign the Ball field.");
            enabled = false;
            return;
        }

        _ballRadius = ReadBallRadius();
        ball.OnGoalScored += HandleGoalScored;
    }

    void OnDestroy()
    {
        if (ball != null)
            ball.OnGoalScored -= HandleGoalScored;

        if (_pocketMesh != null)
            Destroy(_pocketMesh);
    }

    void OnValidate()
    {
        // Rebuilt on the next physics step, so a Pocket tweak in Play mode applies live.
        if (Application.isPlaying)
            _pocketDirty = true;
    }

    /// <summary>The ball's world radius from its SphereCollider, so the sweep is exactly as wide
    /// as the ball the player sees.</summary>
    private float ReadBallRadius()
    {
        SphereCollider sphere = _ballRb.GetComponentInChildren<SphereCollider>();
        if (sphere == null)
        {
            Debug.LogWarning($"[GoalieSaveZone] The ball has no SphereCollider — using a {DefaultBallRadius * 100f:F1} cm " +
                             "radius for the save sweep.");
            return DefaultBallRadius;
        }

        Vector3 s = sphere.transform.lossyScale;
        return sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }

    /// <summary>Catches the likely hand-wiring mistakes at Play time.</summary>
    private void ValidateSetup()
    {
        if (_zoneCollider == null)
        {
            Debug.LogWarning("[GoalieSaveZone] No Collider on this GameObject — add a MeshCollider using the " +
                             "stick-head mesh (Convex + Is Trigger on).");
            return;
        }

        if (GetComponents<Collider>().Length > 1)
            Debug.LogWarning($"[GoalieSaveZone] More than one Collider on this GameObject — only " +
                             $"{_zoneCollider.GetType().Name} is used as the save area. Remove the others " +
                             "(e.g. a test BoxCollider) and keep the MeshCollider.");

        if (_zoneCollider is MeshCollider meshCol && !meshCol.convex)
        {
            Debug.LogWarning("[GoalieSaveZone] The MeshCollider isn't Convex — tick Convex. A non-convex mesh " +
                             "can't be a trigger, so saves won't register.");
            _canMeasureApproach = false;
        }

        if (!_zoneCollider.isTrigger)
            Debug.LogWarning("[GoalieSaveZone] The Collider isn't a trigger — tick Is Trigger, or the zone " +
                             "will physically knock the ball around instead of saving it.");

        Rigidbody zoneRb = GetComponent<Rigidbody>();
        if (zoneRb == null)
            Debug.LogWarning("[GoalieSaveZone] No Rigidbody on this GameObject — add one (Is Kinematic on, " +
                             "Use Gravity off); without it the moving zone isn't reliably detected.");
        else if (!zoneRb.isKinematic)
            Debug.LogWarning("[GoalieSaveZone] The Rigidbody isn't kinematic — tick Is Kinematic, since the " +
                             "controller's tracked pose moves this object, not physics.");
    }

#if UNITY_EDITOR
    void Update()
    {
        // Editor-only: press S to save the shot that's currently in flight, so the whole
        // save -> despawn -> next-shot loop can be verified without a headset or stick.
        // Compiled out of device builds.
        if (Keyboard.current != null && Keyboard.current.sKey.wasPressedThisFrame)
        {
            if (!TryRegister(_ballRb.position, "Editor test key") && showDebugLogs)
                Debug.Log("[GoalieSaveZone] Test key ignored — no shot in flight.");
        }
    }
#endif

    void FixedUpdate()
    {
        if (_pocketDirty)
        {
            _pocketDirty = false;
            RebuildPocket();
        }

        if (!useSweepCheck || _zoneCollider == null) return;

        // Only track the ball while a shot is live. Clearing this between shots also stops the
        // teleport back to the launch origin from being read as a fast pass through the zone.
        if (!ball.ShotInFlight)
        {
            _hasPrevBallPos = false;
            _closestCenterDistance = float.PositiveInfinity;
            return;
        }

        Vector3 pos = _ballRb.position;

        if (_hasPrevBallPos && CheckStepForSave(pos, "sweep"))
            return;

        _prevBallPos = pos;
        _prevZoneWorldToLocal = transform.worldToLocalMatrix;
        _hasPrevBallPos = true;
    }

    /// <summary>Checks the last physics step (ball from _prevBallPos to <paramref name="pos"/>)
    /// against the stick head and registers a save if they touched. Returns true if a save was
    /// registered. Called from FixedUpdate, and from the trigger callback so a touch gets the
    /// same exact contact point.</summary>
    private bool CheckStepForSave(Vector3 pos, string how)
    {
        // The controller is moved by its tracked pose every frame, but Unity only syncs
        // transform changes to the physics scene at the start of a physics step, so
        // without this the queries below would test against a stale zone pose.
        Physics.SyncTransforms();

        // Where the ball started this step, as seen from the stick: its old position in the
        // stick's old pose, carried into the stick's current pose. Sweeping from there to the
        // ball's current position is the ball's path relative to the stick, so a stick swung
        // through the ball's path between two steps is caught. With a still stick this is just
        // _prevBallPos.
        Vector3 relStart = sweepStickMotion
            ? transform.localToWorldMatrix.MultiplyPoint3x4(_prevZoneWorldToLocal.MultiplyPoint3x4(_prevBallPos))
            : _prevBallPos;

        if (showDebugLogs)
            TrackClosestApproach(relStart, pos);

        if (!FindContact(relStart, pos, out float t, out Vector3 onStick, out bool startedTouching))
            return false;

        // The relative path isn't a real world path, so the ball's real center at the touch is
        // found by how far along the step it happened. That's what's recorded (shot path,
        // heatmap projection, the goal-plane check in TryRegisterSave).
        Vector3 worldContact = Vector3.Lerp(_prevBallPos, pos, t);

        if (!TryRegister(worldContact, startedTouching ? how + " (already touching)" : how))
            return false;

        // Shown against the stick where it is now, so the frozen ball sits on the head even
        // if the stick has moved on since the touch.
        SnapBallTo(onStick);
        return true;
    }

    /// <summary>Finds where a ball-sized sphere (plus saveMargin) moving from start to end first
    /// touches the stick head. <paramref name="t"/> is how far along the path (0-1) and
    /// <paramref name="onStick"/> the ball's center there.
    /// Two cases:
    ///  - Already touching at the start (e.g. the stick moved onto the ball's old position):
    ///    a sweep can't report that reliably, so it's checked first, directly.
    ///  - Otherwise a sphere sweep. It also hits the ball itself, the floor and the goal, so only
    ///    hits on this zone's collider count. A hit at distance 0 is the already-touching case
    ///    again (its hit point is meaningless then).</summary>
    private bool FindContact(Vector3 start, Vector3 end, out float t, out Vector3 onStick,
                             out bool startedTouching)
    {
        float reach = _ballRadius + saveMargin;

        if (_canMeasureApproach && Vector3.Distance(_zoneCollider.ClosestPoint(start), start) <= reach)
        {
            t = 0f;
            onStick = start;
            startedTouching = true;
            return true;
        }

        t = 0f;
        onStick = default;
        startedTouching = false;

        Vector3 segment = end - start;
        float length = segment.magnitude;
        if (length < 0.0001f) return false;

        Vector3 dir = segment / length;
        int count = Physics.SphereCastNonAlloc(start, reach, dir, _sweepHits,
                                               length, ~0, QueryTriggerInteraction.Collide);

        float best = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            if (_sweepHits[i].collider == _zoneCollider && _sweepHits[i].distance < best)
                best = _sweepHits[i].distance;
        }

        if (float.IsPositiveInfinity(best)) return false;

        best = Mathf.Max(0f, best);
        t = best / length;
        onStick = start + dir * best;
        startedTouching = best <= 0f;
        return true;
    }

    /// <summary>Puts the saved ball where it touched the stick head. The physics step has already
    /// carried it past the head (up to ~36 cm at full speed), and the save freeze stops it where it
    /// is, so without this it would hang in the air behind the stick. Moves both the Rigidbody and
    /// the transform, the same teleport RandomLauncher uses, so interpolation can't put it back.
    /// The freeze has already made it kinematic, which can still be moved.</summary>
    private void SnapBallTo(Vector3 center)
    {
        _ballRb.position = center;
        _ballRb.transform.position = center;
    }

    /// <summary>Debug only: the closest the ball's center came to the zone along this step's path,
    /// sampled at a few points. Good to about a centimeter, which is plenty for a log.</summary>
    private void TrackClosestApproach(Vector3 from, Vector3 to)
    {
        if (!_canMeasureApproach) return;

        for (int i = 0; i <= ApproachSamples; i++)
        {
            Vector3 p = Vector3.Lerp(from, to, i / (float)ApproachSamples);
            float d = Vector3.Distance(_zoneCollider.ClosestPoint(p), p);  // 0 if p is inside
            if (d < _closestCenterDistance)
                _closestCenterDistance = d;
        }
    }

    /// <summary>Logs how close a goal came to being saved, as the gap between the ball's edge and
    /// the stick head, so a tester's "I touched that" can be checked against the numbers. Runs
    /// after this step's sweep (execution order), so the final step is included.</summary>
    private void HandleGoalScored(Vector3 crossingPos)
    {
        if (!showDebugLogs) return;

        if (!useSweepCheck || !_canMeasureApproach || float.IsPositiveInfinity(_closestCenterDistance))
        {
            Debug.Log("[GoalieSaveZone] GOAL - closest approach not measured");
            return;
        }

        float gapCm = (_closestCenterDistance - _ballRadius) * 100f;
        float marginCm = saveMargin * 100f;
        Debug.Log(gapCm > 0f
            ? $"[GoalieSaveZone] GOAL - ball's edge passed {gapCm:F1} cm from the stick head (save reach {marginCm:F1} cm)"
            : $"[GoalieSaveZone] GOAL - ball overlapped the stick head by {-gapCm:F1} cm but no save registered");
    }

    // ── Trigger contact ───────────────────────────────────────────

    // Stay as well as Enter: covers the ball already overlapping the zone when a shot starts.
    // Both are cheap — anything that isn't a live shot is rejected on the first line.
    void OnTriggerEnter(Collider other) => HandleContact(other);
    void OnTriggerStay(Collider other) => HandleContact(other);

    private void HandleContact(Collider other)
    {
        // Unity still sends trigger callbacks to a disabled component (e.g. Start found no ball).
        if (!enabled || _ballRb == null || !ball.ShotInFlight) return;

        // Identify the ball by its Rigidbody rather than a tag/layer (the project has none),
        // so nothing else in the scene — the shooter's stick, the floor — can register a save.
        if (other.attachedRigidbody != _ballRb) return;

        Vector3 pos = _ballRb.position;

        // Prefer the exact touch point from this step's path. The ball's end-of-step position
        // can already be past the stick, or past the goal plane even though the touch wasn't.
        // The sweep ends at this exact overlap with at least the ball's radius, so it always
        // finds the touch; if it still returns false, GoalDetector refused the save (touch past
        // the goal plane), and falling back to another point could wrongly overturn that.
        if (useSweepCheck && _hasPrevBallPos)
        {
            CheckStepForSave(pos, "touch");
            return;
        }

        // No path to check (sweep off, or the first step of the shot): use the point on the head
        // nearest the ball, which is where they're touching.
        Vector3 contact = _canMeasureApproach ? _zoneCollider.ClosestPoint(pos) : pos;
        TryRegister(contact, "touch");
    }

    // ── Registering ───────────────────────────────────────────────

    /// <summary>Asks GoalDetector to record a save. It refuses (returns false) unless a shot is
    /// in flight and unresolved, so it's safe to call every physics step. This never despawns
    /// the ball itself: RandomLauncher does that in response to OnSaved, after the heatmap has
    /// recorded the shot, so the last shot's dot isn't lost.</summary>
    private bool TryRegister(Vector3 contactPos, string how)
    {
        bool saved = ball.TryRegisterSave(contactPos, _ballRb.linearVelocity);

        if (saved && showDebugLogs)
        {
            // Local space of the stick head (x = across the head, z = along it), in cm, so a save
            // near the rim or a corner is easy to spot in the logs.
            Vector3 local = transform.InverseTransformPoint(contactPos) * 100f;
            Debug.Log($"[GoalieSaveZone] SAVE via {how} | on head (cm) x {local.x:F1}, y {local.y:F1}, z {local.z:F1} | " +
                      $"world ({contactPos.x:F2}, {contactPos.y:F2}, {contactPos.z:F2})");
        }

        return saved;
    }

    // ── Pocket volume ─────────────────────────────────────────────

    /// <summary>Remembers the oval the pocket is built from (the visible mesh, or the collider's
    /// own mesh if there's no MeshFilter) and builds the volume.</summary>
    private void SetupPocket()
    {
        _meshCollider = _zoneCollider as MeshCollider;
        if (_meshCollider == null)
        {
            if (buildPocketVolume && _zoneCollider != null)
                Debug.LogWarning($"[GoalieSaveZone] The save area is a {_zoneCollider.GetType().Name}, not a " +
                                 "MeshCollider — the pocket volume can't be built. Use the oval MeshCollider.");
            return;
        }

        MeshFilter filter = GetComponent<MeshFilter>();
        _faceMesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh : _meshCollider.sharedMesh;
        RebuildPocket();
    }

    /// <summary>Puts the face + sidewall + bag volume on the MeshCollider, or the plain oval if
    /// Build Pocket Volume is off. Play mode only: in Edit mode it would write a generated mesh
    /// into the scene.</summary>
    [ContextMenu("Rebuild pocket")]
    public void RebuildPocket()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[GoalieSaveZone] The pocket is built in Play mode only.");
            return;
        }

        if (_meshCollider == null || _faceMesh == null) return;

        Mesh built = null;
        if (buildPocketVolume)
        {
            if (!_faceMesh.isReadable)
                Debug.LogWarning("[GoalieSaveZone] The oval mesh isn't readable (Read/Write off) — can't build " +
                                 "the pocket, using the flat oval.");
            else
                built = BuildPocketMesh();
        }

        _meshCollider.sharedMesh = built != null ? built : _faceMesh;

        if (_pocketMesh != null)
            Destroy(_pocketMesh);
        _pocketMesh = built;

        if (showDebugLogs)
        {
            Debug.Log(built != null
                ? $"[GoalieSaveZone] Pocket built: sidewall {sidewallDepth * 100f:F1} cm, bag {pocketDepth * 100f:F1} cm " +
                  $"at z {pocketCenter * 100f:F1} cm, size {pocketSize:F2}, side {pocketSide}"
                : "[GoalieSaveZone] Pocket off — save area is the flat oval.");
        }
    }

    /// <summary>Builds the pocket as three rings of the oval's outline, all in this object's local
    /// space: the front of the face (unchanged), the same outline pushed back by sidewallDepth
    /// toward pocketSide, and the outline shrunk by pocketSize around pocketCenter, pushed back by
    /// pocketDepth. Capped and joined into a closed mesh; the collider takes its convex hull, which
    /// fills the bag's hollow (a ball into the bag is a save).</summary>
    private Mesh BuildPocketMesh()
    {
        Vector3[] faceVerts = _faceMesh.vertices;
        if (faceVerts.Length < 3) return null;

        // The face's thinnest axis is its normal (y today); the longest of the other two runs
        // along the head (z today); the last one is across it.
        Bounds b = _faceMesh.bounds;
        Vector3 size = b.size;
        int n = 0;
        for (int i = 1; i < 3; i++)
            if (size[i] < size[n]) n = i;
        int l = (n + 1) % 3, w = (n + 2) % 3;
        if (size[w] > size[l]) (l, w) = (w, l);

        // Meters to local units, so a later rescale doesn't silently change the depths.
        Vector3 scale = transform.lossyScale;
        float scaleN = Mathf.Max(Mathf.Abs(scale[n]), 1e-6f);
        float scaleL = Mathf.Max(Mathf.Abs(scale[l]), 1e-6f);

        // The face's outline seen face-on (x = across, y = along).
        var projected = new List<Vector2>(faceVerts.Length);
        foreach (Vector3 v in faceVerts)
            projected.Add(new Vector2(v[w], v[l]));

        List<Vector2> outline = ConvexHull2D(projected);
        if (outline.Count < 3) return null;
        outline = Downsample(outline, MaxOutlinePoints);
        int m = outline.Count;

        Vector2 centroid = Vector2.zero;
        foreach (Vector2 p in outline) centroid += p;
        centroid /= m;

        int side = (int)pocketSide;
        float frontN = side > 0 ? b.min[n] : b.max[n];  // shooter-side surface: stays where it is
        float backN = side > 0 ? b.max[n] : b.min[n];   // net-side surface: the pocket grows from here
        float sidewallN = backN + side * sidewallDepth / scaleN;
        float bagN = backN + side * pocketDepth / scaleN;

        Vector2 bagCenter = new Vector2(centroid.x, Mathf.Clamp(pocketCenter / scaleL, b.min[l], b.max[l]));

        var verts = new Vector3[3 * m];
        for (int i = 0; i < m; i++)
        {
            verts[i] = ToLocal(outline[i], frontN, n, l, w);
            verts[m + i] = ToLocal(outline[i], sidewallN, n, l, w);
            verts[2 * m + i] = ToLocal(bagCenter + (outline[i] - bagCenter) * pocketSize, bagN, n, l, w);
        }

        var tris = new List<int>(12 * m);
        for (int i = 1; i < m - 1; i++)
        {
            tris.Add(0); tris.Add(i); tris.Add(i + 1);                     // front cap
            tris.Add(2 * m); tris.Add(2 * m + i); tris.Add(2 * m + i + 1); // bag cap
        }
        for (int ring = 0; ring < 2; ring++)
        {
            for (int i = 0; i < m; i++)
            {
                int a = ring * m + i, c = ring * m + (i + 1) % m;
                int d = (ring + 1) * m + (i + 1) % m, e = (ring + 1) * m + i;
                tris.Add(a); tris.Add(c); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(e);
            }
        }

        // Wind every triangle outward (for the gizmo's shading; the collider doesn't care).
        Vector3 middle = Vector3.zero;
        foreach (Vector3 v in verts) middle += v;
        middle /= verts.Length;
        for (int t = 0; t < tris.Count; t += 3)
        {
            Vector3 v0 = verts[tris[t]], v1 = verts[tris[t + 1]], v2 = verts[tris[t + 2]];
            if (Vector3.Dot(Vector3.Cross(v1 - v0, v2 - v0), (v0 + v1 + v2) / 3f - middle) < 0f)
                (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
        }

        var mesh = new Mesh { name = "Stick pocket save zone" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Vector3 ToLocal(Vector2 p, float normal, int n, int l, int w)
    {
        Vector3 v = default;
        v[w] = p.x;
        v[l] = p.y;
        v[n] = normal;
        return v;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) =>
        (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    /// <summary>Convex outline of a set of 2D points, counter-clockwise (monotone chain).
    /// Duplicate and in-line points are dropped.</summary>
    private static List<Vector2> ConvexHull2D(List<Vector2> points)
    {
        points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var hull = new List<Vector2>(points.Count + 1);

        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            for (int k = 0; k < points.Count; k++)
            {
                Vector2 p = points[pass == 0 ? k : points.Count - 1 - k];
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-10f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);  // the last point is the first of the other half
        }
        return hull;
    }

    /// <summary>Evenly spaced subset of the outline, so a finely tessellated oval still cooks
    /// into a small convex hull.</summary>
    private static List<Vector2> Downsample(List<Vector2> outline, int max)
    {
        if (outline.Count <= max) return outline;
        var result = new List<Vector2>(max);
        for (int i = 0; i < max; i++)
            result.Add(outline[i * outline.Count / max]);
        return result;
    }

    // ── Gizmos ────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        Collider col = _zoneCollider != null ? _zoneCollider : GetComponent<Collider>();
        if (col == null) return;

        Color outline = new Color(0f, 1f, 0.4f, 0.9f);
        Color fill = new Color(0f, 1f, 0.4f, 0.12f);

        Matrix4x4 previousMatrix = Gizmos.matrix;

        // Draw in local space so the shape follows the zone's rotation, like the collider does.
        Gizmos.matrix = transform.localToWorldMatrix;

        if (col is BoxCollider box)
        {
            Gizmos.color = fill;
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = outline;
            Gizmos.DrawWireCube(box.center, box.size);
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.color = fill;
            Gizmos.DrawSphere(sphere.center, sphere.radius);
            Gizmos.color = outline;
            Gizmos.DrawWireSphere(sphere.center, sphere.radius);
        }
        else if (col is CapsuleCollider capsule)
        {
            DrawCapsuleGizmo(capsule, fill, outline);
        }
        else if (col is MeshCollider meshCol && meshCol.sharedMesh != null)
        {
            Gizmos.color = fill;
            Gizmos.DrawMesh(meshCol.sharedMesh);
            Gizmos.color = outline;
            Gizmos.DrawWireMesh(meshCol.sharedMesh);
        }
        else
        {
            Gizmos.matrix = previousMatrix;
            Gizmos.color = outline;
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
        }

        Gizmos.matrix = previousMatrix;
    }

    /// <summary>Approximates a capsule as two end-cap spheres joined by wire lines along its axis,
    /// since Gizmos has no built-in capsule primitive. Called with Gizmos.matrix already set to
    /// local space, so this only deals with the collider's own center/radius/height/direction.</summary>
    private static void DrawCapsuleGizmo(CapsuleCollider capsule, Color fill, Color outline)
    {
        Vector3 axis = capsule.direction switch
        {
            0 => Vector3.right,
            2 => Vector3.forward,
            _ => Vector3.up,
        };

        float halfLine = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
        Vector3 capA = capsule.center + axis * halfLine;
        Vector3 capB = capsule.center - axis * halfLine;

        Gizmos.color = fill;
        Gizmos.DrawSphere(capA, capsule.radius);
        Gizmos.DrawSphere(capB, capsule.radius);

        Gizmos.color = outline;
        Gizmos.DrawWireSphere(capA, capsule.radius);
        Gizmos.DrawWireSphere(capB, capsule.radius);

        // Four lines around the cylinder connecting the two caps, so the capsule's length reads
        // clearly even though the caps themselves are drawn as full spheres rather than hemispheres.
        Vector3 perp1 = Vector3.Cross(axis, Vector3.up).sqrMagnitude > 0.001f
            ? Vector3.Cross(axis, Vector3.up).normalized
            : Vector3.Cross(axis, Vector3.right).normalized;
        Vector3 perp2 = Vector3.Cross(axis, perp1).normalized;

        foreach (Vector3 dir in new[] { perp1, -perp1, perp2, -perp2 })
        {
            Gizmos.DrawLine(capA + dir * capsule.radius, capB + dir * capsule.radius);
        }
    }
}
