using UnityEngine;

/// <summary>
/// The one place UI colors live (see Plans/Current/UI_Design_Plan.md, "Color: the cage, not a
/// scoreboard"). Magic Leap 2 is an additive display, so every token is bright: dark values
/// render as transparent.
///
/// Color is data: Pipe and Ice mean goal and save everywhere, and are never used for
/// decoration. Field markings stay plain white / pale yellow and don't use these tokens.
/// </summary>
public static class UiPalette
{
    /// <summary>#FF6A1A. Goals against (the ball beat you into the pipes), and the cage frame
    /// on the readout. Outcome only.</summary>
    public static readonly Color Pipe = Hex(0xFF6A1A);

    /// <summary>#5CE1FF. Saves. Opposite Pipe on the blue–orange axis, so the pair stays
    /// distinct under the common color-vision deficiencies.</summary>
    public static readonly Color Ice = Hex(0x5CE1FF);

    /// <summary>#EEF3F8. All text, the countdown, and anything that isn't an outcome.</summary>
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
