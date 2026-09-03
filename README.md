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
training scene (it has both `RandomLauncher` and `FingerTouchDetector` wired in).
`Assets/Scenes/Practice_Unity.unity` is an earlier scene still using the legacy
`BallLauncher` component described below; it wasn't updated when the flow moved to
`RandomLauncher`.

### Core systems (`Assets/Scripts/Lacrosse/`)

- **`RandomLauncher`** — the main session driver. Idle until a start trigger fires,
  then runs a pre-start countdown (`GET READY` → `3, 2, 1` → `GO!`) followed by a fixed
  number of shots, spaced by a randomized interval. Two aim modes: `RandomInQuadrant`
  (a different random point inside the quadrant each shot) or `FixedPoint`
  (a deterministic point, tunable by `quadrantDepth` from center toward the corner).
  Fires `OnSessionStarted`/`OnSessionEnded` events other systems (like the heatmap)
  hook into.
- **`GoalDetector`** — single source of truth for when the ball crosses the goal gate
  plane, make or miss. Everything else (`RandomLauncher`'s falling behavior,
  `BallDisappear`, `ShotHeatmapRecorder`) subscribes to its `OnPlaneCrossed`/
  `OnGoalScored` events instead of re-implementing plane-crossing detection.
- **`Quadrant` / `QuadrantMath`** — shared enum (`TopLeft`/`TopRight`/`BottomLeft`/
  `BottomRight`) and aim-point math, used by both launcher implementations so quadrant
  logic only needs fixing in one place.
- **`ShotHeatmapRecorder`** — records where every shot in a session crossed the goal
  plane, then shows a save/score heatmap (world-space UI dots, color-coded) once the
  session ends; clears automatically when the next session starts.
- **`FloorBoundary`** — generic "don't fall below the floor" clamp with optional bounce
  and auto-despawn-after-landing, used by the current `RandomLauncher` flow.
- **`LacrossBallPhysics`** — a more physically-detailed alternative to `FloorBoundary`:
  manual gravity, sweep-cast anti-tunneling (so a fast ball doesn't skip through thin
  or animated colliders), and surface-velocity-aware bounce/friction/rest handling.
- **`BallLauncher` / `BallDisappear`** — the earlier single-shot launcher + linger-then-
  deactivate pair (used by `Practice_Unity.unity`). `RandomLauncher` is the actively
  developed replacement; these remain for the older scene rather than being deleted
  outright.

### Session start inputs

A `RandomLauncher` session can be started any of these ways — all wired as independent
subscribers, so any one of them (or several, if all are present) can fire without
double-starting a session that's already running:

- **Controller trigger press** — `MagicLeapController` (`Assets/Scripts/Utility/`), a
  singleton wrapping the OpenXR `Controller` action map from
  `Assets/MagicLeapInput.inputactions`.
- **Two fingertips touching, either hand** — `FingerTouchDetector`
  (`Assets/Scripts/Utility/`). Thumb touching any of index/middle/ring/little counts,
  not just the standard thumb+index pinch OpenXR defines — reads raw joint distances
  from `com.unity.xr.hands`' `XRHandSubsystem` rather than the fixed-pair `Pinch`
  input action. See `Plans/Current/Finger_Startup.md` for the design notes, hysteresis/
  cooldown tuning, and on-device setup steps.
- **Space bar** — Editor Play mode only, compiled out of device builds. Lets the whole
  start → countdown → multi-shot flow be verified without a headset connected.

### Other example content

The rest of `Assets/Scripts/` (outside `Lacrosse/`) and most other scenes under
`Assets/Scenes/` are the original Magic Leap SDK example content described in
Overview above — eye tracking, meshing, spatial anchors, marker tracking, planes,
pixel sensors, facial expression, and hand-tracking visualizers — kept as reference
and as the source for utilities the training project reuses (`MagicLeapController`,
the `com.unity.xr.hands` visualizer pattern `FingerTouchDetector` borrows from).

# Copyright
Copyright (c) 2020-present Magic Leap, Inc. All Rights Reserved.
Use of this file is governed by the Developer Agreement, located
here: https://id.magicleap.com/terms/developer
