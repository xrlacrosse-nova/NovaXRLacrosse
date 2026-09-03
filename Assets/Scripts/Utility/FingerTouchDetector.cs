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

    [Tooltip("Log hand-tracking state and live thumb-to-fingertip distances (~2x/sec) so a " +
             "touch that isn't registering can be diagnosed from device logs (adb logcat).")]
    public bool debugLogging = false;

    /// <summary>Fired once per qualifying touch (edge-triggered, not held-down).</summary>
    public event Action TouchStarted;

    private XRHandSubsystem _subsystem;
    private bool _isTouching;
    private float _lastFireTime = -999f;
    private float _timeSinceEnable = 0f;
    private bool _warnedNoSubsystem = false;
    private float _lastDebugLogTime = -999f;
    private static readonly List<XRHandSubsystem> s_SubsystemsReuse = new();

    void Awake() => Instance = this;

    void Update()
    {
        _timeSinceEnable += Time.deltaTime;

        if (_subsystem != null && _subsystem.running) return;

        SubsystemManager.GetSubsystems(s_SubsystemsReuse);
        foreach (var s in s_SubsystemsReuse)
        {
            if (!s.running) continue;
            _subsystem = s;
            _subsystem.updatedHands += OnUpdatedHands;
            Debug.Log("[FingerTouchDetector] Hand-tracking subsystem found and running.");
            break;
        }

        // Give the subsystem a few seconds to spin up (permission grant, sensor init) before
        // warning — this fires once, not every frame, so it's safe to leave debugLogging off.
        if (_subsystem == null && !_warnedNoSubsystem && _timeSinceEnable > 5f)
        {
            _warnedNoSubsystem = true;
            Debug.LogWarning("[FingerTouchDetector] No running XRHandSubsystem found after 5s — " +
                              "hand tracking is likely not enabled/permitted on this device. Check " +
                              "the runtime Hand Tracking permission prompt was accepted, and that " +
                              "Hand Tracking is enabled under Project Settings > XR Plug-in " +
                              "Management > OpenXR (Android tab).");
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

        if (debugLogging && Time.time - _lastDebugLogTime >= 0.5f)
        {
            _lastDebugLogTime = Time.time;
            Debug.Log($"[FingerTouchDetector] left tracked={subsystem.leftHand.isTracked} " +
                      $"minDist={MinThumbTipDistance(subsystem.leftHand):F3}m | " +
                      $"right tracked={subsystem.rightHand.isTracked} " +
                      $"minDist={MinThumbTipDistance(subsystem.rightHand):F3}m | " +
                      $"startDistance={startDistance:F3}m");
        }

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

    /// <summary>Smallest current thumb-to-fingertip distance for debug logging. Returns -1 if
    /// the hand isn't tracked or no joint poses are available (not the same as "not touching" —
    /// only used for diagnostics, never for the actual trigger decision in CheckHand).</summary>
    private float MinThumbTipDistance(XRHand hand)
    {
        if (!hand.isTracked) return -1f;
        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbPose)) return -1f;

        float min = float.MaxValue;
        foreach (var finger in fingersPairedWithThumb)
        {
            if (!hand.GetJoint(GetTipJointID(finger)).TryGetPose(out var tipPose)) continue;
            min = Mathf.Min(min, Vector3.Distance(thumbPose.position, tipPose.position));
        }

        return min == float.MaxValue ? -1f : min;
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
