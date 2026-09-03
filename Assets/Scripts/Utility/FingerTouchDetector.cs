using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Fires a start-trigger event when two fingertips on the same hand come within
/// StartDistance of each other (any tracked pair, not just OpenXR's fixed
/// thumb+index Pinch action — see Plans/Current/Finger_Startup.md). Hysteresis
/// (StartDistance vs ReleaseDistance) and a cooldown keep a held touch from firing
/// repeatedly.
/// </summary>
public class FingerTouchDetector : MonoBehaviour
{
    public static FingerTouchDetector Instance { get; private set; }

    [Tooltip("Fingertip pairs to test, per hand. Thumb-vs-other-finger covers every " +
             "ergonomically natural touch (thumb+index, thumb+middle, etc.) without " +
             "the cost of testing all 10 combinations per hand.")]
    public XRHandFingerID[] fingersPairedWithThumb =
    {
        XRHandFingerID.Index, XRHandFingerID.Middle, XRHandFingerID.Ring, XRHandFingerID.Little
    };

    [Tooltip("Fingertips closer than this (meters) count as touching.")]
    public float startDistance = 0.02f;

    [Tooltip("Fingertips must separate past this (meters) before another touch can " +
             "fire. Must be > StartDistance — this hysteresis gap stops a held touch " +
             "from re-triggering on tracking jitter.")]
    public float releaseDistance = 0.035f;

    [Tooltip("Minimum seconds between fired events, regardless of finger state.")]
    public float cooldown = 0.5f;

    /// <summary>Fired once per qualifying touch (edge-triggered, not held-down).</summary>
    public event Action TouchStarted;

    private XRHandSubsystem _subsystem;
    private bool _isTouching;
    private float _lastFireTime = -999f;
    private static readonly List<XRHandSubsystem> s_SubsystemsReuse = new();

    void Awake() => Instance = this;

    void Update()
    {
        if (_subsystem != null && _subsystem.running) return;

        SubsystemManager.GetSubsystems(s_SubsystemsReuse);
        foreach (var s in s_SubsystemsReuse)
        {
            if (!s.running) continue;
            _subsystem = s;
            _subsystem.updatedHands += OnUpdatedHands;
            break;
        }
    }

    void OnDestroy()
    {
        if (_subsystem != null)
            _subsystem.updatedHands -= OnUpdatedHands;
        if (Instance == this)
            Instance = null;
    }

    private void OnUpdatedHands(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags,
                                 XRHandSubsystem.UpdateType updateType)
    {
        if (updateType != XRHandSubsystem.UpdateType.Dynamic) return;

        bool touchingThisUpdate = CheckHand(subsystem.leftHand) || CheckHand(subsystem.rightHand);

        if (touchingThisUpdate && !_isTouching && Time.time - _lastFireTime >= cooldown)
        {
            _isTouching = true;
            _lastFireTime = Time.time;
            TouchStarted?.Invoke();
        }
        else if (!touchingThisUpdate)
        {
            // Only clear once separated past ReleaseDistance — CheckHand already
            // applies the right threshold depending on current state.
            _isTouching = false;
        }
    }

    private bool CheckHand(XRHand hand)
    {
        if (!hand.isTracked) return false;
        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbPose)) return false;

        float threshold = _isTouching ? releaseDistance : startDistance;

        foreach (var finger in fingersPairedWithThumb)
        {
            if (!hand.GetJoint(GetTipJointID(finger)).TryGetPose(out var tipPose)) continue;

            if (Vector3.Distance(thumbPose.position, tipPose.position) <= threshold)
                return true;
        }

        return false;
    }

    // Explicit switch rather than walking a finger's joint chain — the *Tip IDs are
    // named directly on XRHandJointID, so this is unambiguous.
    private static XRHandJointID GetTipJointID(XRHandFingerID finger) => finger switch
    {
        XRHandFingerID.Thumb => XRHandJointID.ThumbTip,
        XRHandFingerID.Index => XRHandJointID.IndexTip,
        XRHandFingerID.Middle => XRHandJointID.MiddleTip,
        XRHandFingerID.Ring => XRHandJointID.RingTip,
        XRHandFingerID.Little => XRHandJointID.LittleTip,
        _ => XRHandJointID.Invalid
    };
}
