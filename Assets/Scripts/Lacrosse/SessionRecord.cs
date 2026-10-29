using System;
using System.Collections.Generic;
using UnityEngine;

// Saved session data (Plans/Current/Player_Session_History_Plan.md, "Data model"). Plain
// JsonUtility classes: public fields and List<>, no dictionaries. Every file carries a
// schemaVersion so later fields can be added and old files still load. Positions are
// gate-relative (minus GoalDetector.goalGateCenter), so old replays stay lined up with the goal
// if the gate center is retuned.

/// <summary>One finished session: one file under players/&lt;id&gt;/sessions/.</summary>
[Serializable]
public class SessionRecord
{
    public int schemaVersion = 1;
    public string sessionId, playerId, startedUtc, endedUtc;
    public SessionSettings settings;
    public int saves, goals;             // no misses: every shot is on target
    public List<ShotRecord> shots = new List<ShotRecord>();
}

/// <summary>The launcher settings the session ran with, copied at its start.</summary>
[Serializable]
public class SessionSettings
{
    public string aimMode;
    public float launchSpeed, launchSpeedVariance, edgePadding, quadrantDepth;
    public int shotsPerSession;
    public string difficulty;            // Difficulty enum name, "" without a DifficultySelector
}

/// <summary>One recorded shot. Shots that despawn without crossing the gate plane or being
/// saved aren't recorded, the same as on the heatmap and path streaks.</summary>
[Serializable]
public class ShotRecord
{
    public const string Save = "Save";
    public const string Goal = "Goal";
    public const string LeftHand = "Left";
    public const string RightHand = "Right";

    public int index;
    public Quadrant aimedQuadrant;       // quadrant the launcher aimed at
    public Quadrant resultQuadrant;      // quadrant of the gate-plane point (= ShotPath.quadrant)
    public Vector2 aimNormalized;        // where the launcher aimed, [-1, 1]
    public Vector2 resultNormalized;     // gate-plane crossing / projected save point, [-1, 1]
    public string outcome;               // Save | Goal
    public string shooterHand;           // Left | Right; "" = no shooter avatar, or a file from before it was saved
    public float launchSpeed;            // release speed, m/s
    public float launchTime;             // seconds since session start
    public Vector3[] path;               // gate-relative, launch → gate plane (or the stick for a save)
    public Vector3[] projectedTail;      // gate-relative, saves only: stick → gate plane
}

/// <summary>One line in index.json, so the history list loads without opening every session.</summary>
[Serializable]
public class SessionSummary
{
    public string sessionId, file, startedUtc, difficulty;
    public int shots, saves, goals;
    public int[] savesPerQuadrant = new int[4], shotsPerQuadrant = new int[4];
}

/// <summary>index.json: every session's summary, oldest first.</summary>
[Serializable]
public class SessionIndex
{
    public int schemaVersion = 1;
    public List<SessionSummary> sessions = new List<SessionSummary>();
}
