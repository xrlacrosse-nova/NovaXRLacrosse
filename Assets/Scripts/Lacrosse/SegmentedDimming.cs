using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes;
using MagicLeap.OpenXR.Features;

/// <summary>
/// Turns on Magic Leap 2 segmented dimming for the scene (Plans/Current/UI_Design_Plan.md,
/// Phase 3 item 8). Switches the blend mode to AlphaBlend on start and restores the previous
/// mode on destroy. Based on Assets/Scripts/SegmentedDimmerExample.cs.
///
/// In AlphaBlend the headset dims the real room behind every pixel by that pixel's alpha, so a
/// black, partly transparent Image behind the heatmap readout darkens the room there without
/// showing a visible color. Everything else that writes alpha dims the room too, so the camera
/// background must clear to alpha 0 or the whole view goes dark.
///
/// Needs: the "Magic Leap 2 Rendering Extensions" OpenXR feature on, and Settings > Display >
/// Segmented Dimming on in the headset. Has no visible effect in the Editor (it can't be previewed).
/// Setup: add to any GameObject in the scene.
/// </summary>
public class SegmentedDimming : MonoBehaviour
{
    private MagicLeapRenderingExtensionsFeature _rendering;
    private XrEnvironmentBlendMode _previousBlendMode;

    void Start()
    {
        _rendering = OpenXRSettings.Instance != null
            ? OpenXRSettings.Instance.GetFeature<MagicLeapRenderingExtensionsFeature>()
            : null;

        if (_rendering == null || !_rendering.enabled)
        {
            Debug.LogWarning("[SegmentedDimming] Magic Leap 2 Rendering Extensions feature not found or " +
                             "disabled — segmented dimming is off.");
            _rendering = null;
            return;
        }

        // Capture before switching, so OnDestroy restores what was there (the SDK sample reads
        // it after switching, which makes its restore a no-op).
        _previousBlendMode = _rendering.BlendMode;
        _rendering.BlendMode = XrEnvironmentBlendMode.AlphaBlend;

        Debug.Log($"[SegmentedDimming] Blend mode {_previousBlendMode} -> {_rendering.BlendMode}. " +
                  $"Headset dimmer setting on: {IsDimmerEnabledInSettings()}");
    }

    void OnApplicationPause(bool pause)
    {
        // The headset setting can be toggled while the app is paused.
        if (!pause && _rendering != null)
            Debug.Log($"[SegmentedDimming] Headset dimmer setting on: {IsDimmerEnabledInSettings()}");
    }

    void OnDestroy()
    {
        if (_rendering != null)
            _rendering.BlendMode = _previousBlendMode;
    }

    /// <summary>Reads Settings > Display > Segmented Dimming on the headset. Always false in the Editor.</summary>
    private static bool IsDimmerEnabledInSettings()
    {
        if (Application.isEditor)
            return false;

        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var context = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        using var systemSettings = new AndroidJavaClass("android.provider.Settings$System");
        using var resolver = context.Call<AndroidJavaObject>("getContentResolver");

        // The 3-argument overload returns the default (0) instead of throwing if the setting is missing.
        return systemSettings.CallStatic<int>("getInt", resolver, "is_segmented_dimmer_enabled", 0) > 0;
    }
}
