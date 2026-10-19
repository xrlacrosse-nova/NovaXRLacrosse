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

    private static Color Hex(int rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f);
    }
}
