using System;
using UnityEngine;

/// <summary>
/// One shot on the heatmap: where it reached the gate plane, normalized to the gate ([-1, 1],
/// QuadrantMath.NormalizeToGate), whether it scored, and which session it came from (higher =
/// newer). Shared by ShotHeatmapRecorder and Plans/Future/Reviewed/Player_Session_History_Plan.md.
/// No misses: every shot is on target, so each one is a goal or a save.
/// </summary>
[Serializable]
public struct RecordedShot
{
    public Vector2 normalized;
    public bool scored;
    public int session;

    public RecordedShot(Vector2 normalized, bool scored, int session)
    {
        this.normalized = normalized;
        this.scored = scored;
        this.session = session;
    }
}
