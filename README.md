# Magic Leap Unity Examples

## Overview
This project contains example scenes demonstrating how to use Magic Leap features with the Magic Leap Unity SDK package, and has been configured to help the user quickly jump in and start developing for the Magic Leap 2.

The files in this project can change or even be removed from one release to another. If you're planning on depending or modifying these assets for your own project, we recommend that you duplicate the files, change the names and move them out of the Assets/MagicLeap folder. This will avoid issues like your changes being deleted when you upgrade to a new unitypackage.

## Compatible with
- Unity Editor 2022.3+
- Magic Leap Unity SDK 2.5.0

---

## Lacrosse Goalie Training

Built on top of the example scaffolding above, this repo is a Magic Leap 2 goalie
reaction-training simulator: a ball fires at a random (or fixed) point in one quadrant
of a virtual goal, and the goalie's job is to save it. The active scene is
**`Assets/Scenes/HelloCube.unity`** — despite the placeholder name, this is the real
training scene (it has `RandomLauncher` wired in).
`Assets/Scenes/Practice_Unity.unity` is an earlier scene still using the legacy
`BallLauncher` component described below; it wasn't updated when the flow moved to
`RandomLauncher`.

The project itself is on Unity Editor **6000.2.6f2**.

### How a session works

Press the controller trigger (or **Space** in Editor Play mode) to start. After a
`GET READY` → `3, 2, 1` → `GO!` countdown, a shooter avatar fires a fixed number of shots
at randomized intervals. Each shot ends exactly once, as a **save** (the goalie's stick
touches the ball), a **goal**, or a **miss**. When the session ends, the results are shown
until the next session starts. Nothing is shown during play.

### Features (`Assets/Scripts/Lacrosse/`)

- **Shots:** `RandomLauncher` runs the session. Each shot goes to a random point in a
  quadrant, or to a fixed point (`quadrantDepth`). `ShooterAnimationController` times each
  launch to the avatar's release frame.
- **Outcome detection:** `GoalDetector` decides when the ball crosses the goal plane, and
  `GoalieSaveZone` (on the stick) registers saves. Other systems subscribe to its events.
- **Feedback during play:** a GOAL! / SAVE! cue, controller haptics
  (`ShotOutcomeHaptics`), and crowd audio that cheers after saves and boos after goals
  (`CrowdAmbience`).
- **Results after the session:**
  - `ShotHeatmapRecorder` shows where each shot crossed the goal, with saves as dots, goals
    as an ×, and save counts per corner in plain words (`Shot Heatmap: 6 of 8 saved`,
    `Top left: 2/2`).
  - `ShotPathRecorder` / `ShotPathVisualizer` show each shot's flight path as a 3D streak
    in the world, along the route the ball actually flew.
- **Field markings:** white men's-field lines on the floor around the goal (goal line, goal
  line extended, crease, goalie arc and restraining line), built in the scene under
  `FieldMarkings`. Visual only; no script.
- **Shooter avatar:** `LacrosseStickGrip` keeps the shooter's stick in both hands through the
  shooting animation.

### Design docs

`Plans/` holds the design and investigation docs. `Plans/Current/` and `Plans/Future/` are
in progress or proposed, and `Plans/Past/` are finished. Check the relevant plan before
changing a system: plans record what's been tried and ruled out, and the Editor wiring each
feature needs.

# Copyright
Copyright (c) 2020-present Magic Leap, Inc. All Rights Reserved.
Use of this file is governed by the Developer Agreement, located
here: https://id.magicleap.com/terms/developer
