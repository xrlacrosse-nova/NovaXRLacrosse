# NovaXR Lacrosse — Goalie Training on Magic Leap 2

A Magic Leap 2 reaction-training simulator for lacrosse goalies. A virtual shooter fires
on-target shots at a random point in one corner of a virtual goal, and the goalie tries to
save them with their real stick, which has the ML2 controller mounted on it. After each
session the headset shows where shots went and which ones got past, and saves the results
for that player.

## Requirements

- Unity Editor **6000.2.6f2** (URP)
- Magic Leap Unity SDK **2.5.0** (`com.magicleap.unitysdk`), plus `com.unity.xr.hands`
  1.5.0 and `com.unity.inputsystem` 1.14.2 (installed through `Packages/manifest.json`)
- A Magic Leap 2 headset and controller for on-device play

## Running it

1. Open the project in Unity 6000.2.6f2.
2. Open **`Assets/Scenes/HelloCube.unity`**. Despite the placeholder name, this is the real
   training scene.
3. **In the Editor:** enter Play mode and press **Space** to start a session. Mouse clicks
   stand in for the controller ray on the settings board, and the keys **L**, **H** and
   **R** stand in for the voice commands.
4. **On the headset:** build and deploy to the Magic Leap 2 as for any Magic Leap Unity SDK
   project. The virtual floor lines up with the real floor.

## How a session works

1. **Setup (between sessions).** Take the controller out of the stick mount and point it at
   the settings board on the left. Pull the trigger to pick or add a player, choose a
   difficulty (High school, College or Pro), or browse past sessions.
2. **Start.** Put the controller back in the mount and look at the **START** target beside
   the shooter for 3 seconds. The controller trigger does not start a session, so a stray
   pull can't start one early.
3. **Shots.** After `GET READY` → `3, 2, 1` → `GO!`, the shooter avatar takes a fixed number
   of shots at random intervals, left- or right-handed. A label shows how many shots are left.
   Every shot is on target and ends as a **save** (the stick touches the ball) or a **goal**.
4. **Feedback during play.** A GOAL! / SAVE! cue, a controller tap (save) or buzz (goal),
   and a crowd that cheers saves and boos goals.
5. **Results.** When the session ends, a heatmap on the goal shows where shots crossed and
   how often each area was saved (`Top left: 2/2`), 3D streaks show each shot's flight path,
   and a voice reads a short summary. Results stay up until the next session starts.
6. **Review.** Between sessions, say **"Lifetime"**, **"Last session"** or **"Repeat"**, or
   use the board's History page to replay a past session.

## Features (`Assets/Scripts/Lacrosse/`)

- **Shots:** `RandomLauncher` runs the session and solves each launch so the ball arcs onto
  its aim point under gravity and air drag (`BallFlight`). `ShooterAnimationController`
  launches each shot on the avatar's release frame, and `LacrosseStickGrip` keeps the stick
  in the shooter's hands.
- **Outcome detection:** `GoalDetector` is the single source of truth for the ball crossing
  the goal plane, and `GoalieSaveZone` on the stick registers saves.
- **Feedback:** `ShotOutcomeHaptics` (controller haptics) and `CrowdAmbience` (crowd audio).
- **Results:** `ShotHeatmapRecorder` (color-coded heatmap with a colorblind-safe option),
  `ShotPathRecorder` / `ShotPathVisualizer` (flight-path streaks) and
  `SessionSummarySpeaker` (spoken summary through `TextToSpeech`).
- **Players and history:** `PlayerProfileStore`, `SessionLogger` and `SessionHistory` keep
  several players on one headset and save each session as JSON on the device. Data stays on
  the headset; there is no export.
- **Setup and input:** `SettingsPage` / `RayButton` / `ControllerRayPointer` (settings
  board), `DifficultySelector` (fixed, player-chosen difficulty), `GazeStartTarget` (gaze
  start) and `VoiceCommands`.
- **Field markings:** white men's-field lines around the goal, built in the scene. Visual
  only; no script.

`Assets/Scenes/Practice_Unity.unity` is an older scene that still uses the legacy
`BallLauncher`. Scripts and scenes outside `Lacrosse/` are original Magic Leap SDK example
content, kept as reference and for reusable utilities.

## Project docs

The project docs (`docs/`: PRD, architecture, tasks, decisions, progress) and the
per-feature design docs (`Plans/`) are kept locally and are not in this repository.

---

## About the Magic Leap examples

This project started from Magic Leap's Unity examples project, which demonstrates Magic
Leap features with the Magic Leap Unity SDK package. Those files can change or be removed
from one SDK release to the next. If you depend on or modify them, duplicate them, rename
them and move them out of the `Assets/MagicLeap` folder so an SDK upgrade doesn't overwrite
your changes.

# Copyright
Copyright (c) 2020-present Magic Leap, Inc. All Rights Reserved.
Use of this file is governed by the Developer Agreement, located
here: https://id.magicleap.com/terms/developer
