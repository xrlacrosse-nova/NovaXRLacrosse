using UnityEngine;

/// <summary>
/// The one place UI colors live (see Plans/Past/UI_Design_Plan.md). Magic Leap 2 is an
/// additive display, so every token is bright: dark values render as transparent.
///
/// Color is data: Goal and Save mean goal and save everywhere, and are never used for
/// decoration. Red/green is hard to tell apart for some color-vision deficiencies, so shape
/// backs it up (saves are dots, goals are an ×). Field markings stay plain white / pale
/// yellow and don't use these tokens.
/// </summary>
public static class UiPalette
{
    /// <summary>#FF0000 (same as Color.red). Goals against. Outcome only.</summary>
    public static readonly Color Goal = Hex(0xFF0000);

    /// <summary>#00FF00 (same as Color.green). Saves. Outcome only.</summary>
    public static readonly Color Save = Hex(0x00FF00);

    /// <summary>#EEF3F8. All text, the countdown, the cage frame on the readout, and anything
    /// that isn't an outcome.</summary>
    public static readonly Color Chalk = Hex(0xEEF3F8);

    /// <summary>#6E7A86. Dim structure (quadrant crosshair, secondary labels). Shows as a faint
    /// ghost line on an additive display.</summary>
    public static readonly Color Smoke = Hex(0x6E7A86);

    /// <summary>Shots wide of the cage. Same as Smoke: a miss isn't a goalie decision, so it
    /// stays quiet.</summary>
    public static readonly Color Miss = Smoke;

    /// <summary>#FFFF00. Midpoint (50% saved) of the save-rate gradient only. A straight red →
    /// green fade passes through a dim olive that nearly vanishes on an additive display.</summary>
    public static readonly Color Mixed = Hex(0xFFFF00);

    /// <summary>#FF8C00 orange. Goal, colorblind-safe (ShotHeatmapRecorder.colorblindSafe).</summary>
    public static readonly Color GoalSafe = Hex(0xFF8C00);

    /// <summary>#3399FF. Save, colorblind-safe. A bright blue on purpose: dark blue nearly
    /// vanishes on an additive display.</summary>
    public static readonly Color SaveSafe = Hex(0x3399FF);

    /// <summary>Color for a save rate (0 = all goals, 1 = all saves): Goal → Mixed → Save, or
    /// GoalSafe → Chalk → SaveSafe when colorblindSafe. Shared by the heatmap gradient and its
    /// legend, so they always match.</summary>
    public static Color SaveRateRamp(float saveRate, bool colorblindSafe)
    {
        Color low = colorblindSafe ? GoalSafe : Goal;
        Color mid = colorblindSafe ? Chalk : Mixed;
        Color high = colorblindSafe ? SaveSafe : Save;

        // Two halves through a bright midpoint, so the middle never goes dim.
        float t = Mathf.Clamp01(saveRate);
        return t < 0.5f ? Color.Lerp(low, mid, t * 2f) : Color.Lerp(mid, high, t * 2f - 1f);
    }

    private static Color Hex(int rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f);
    }
}
