# NovaXR Lacrosse: Goalie Training on Magic Leap 2

NovaXR Lacrosse is an Extended-Reality training app for lacrosse goalies. You wear a Magic Leap 2
headset and see a virtual goal and a virtual shooter in your real space. The shooter takes
shots at the goal, and you try to stop them with your own stick. The Magic Leap controller
is attached to the stick, so the app knows where your stick is.

When a session ends, the app shows where every shot went, which ones you saved and which
ones got past you. It also saves your results so you can track your progress over time.

## What you need

- A **Magic Leap 2** headset and controller
- A lacrosse stick with the **controller mount** attached
  - The files to 3D print it are in [`3D_Mount/`](3D_Mount/). You'll also need one
    M3 x 25 screw. See the [assembly drawing](3D_Mount/ml2_shaft_mount_assembly.svg)
    for what it looks like.
- Enough open floor space to stand in front of the virtual goal
  - Preferably 10 yards in front of you, as well as a yard to the left and right of you

## Running a training session

### 1. Set up

Take the controller out of the stick mount. On your left you'll see a **settings board**.
Point the controller at it and pull the trigger to:

- **Pick your name**, or add a new player
- **Choose a difficulty:** High school, College or Pro
- **Look at past sessions** on the History page

### 2. Start

Put the controller back in the stick mount. Then **look at the START target** next to the
shooter and hold your gaze there for 3 seconds.

### 3. Make saves

You'll see **GET READY**, then a **3, 2, 1** countdown, then **GO!** The shooter takes a set
number of shots, sometimes right-handed and sometimes left-handed, with random pauses in
between. A counter shows how many shots are left.

Every shot is on target, so each one ends in one of two ways:

- **Save:** the ball touches the virtual cylinder, which is adjacent to the controller.
  You'll see **SAVE!**, feel a light tap in the controller, and hear the crowd cheer.
- **Goal:** the ball got past you. You'll see **GOAL!**, feel a buzz, and hear the crowd boo.

### 4. Check your results

When the last shot is done, the app shows:

- **A heatmap on the goal:** colored areas showing where shots went and how many you saved
  in each spot (for example, `Top left: 2/2`).
- **Shot trails:** lines in the air showing the path of each shot.
- **A spoken summary:** a voice reads out how the session went.

The results stay up until you start the next session.

### 5. Look back at earlier sessions

Between sessions, you can say:

- **"Last session"** to see your most recent session again
- **"Lifetime"** to see all of your results combined
- **"Repeat"** to hear the spoken summary again

You can also open the **History** page on the settings board to replay any past session.

### Your data

Each player's sessions are saved **on the headset only**. Several players can share one
headset, and nothing is uploaded or exported.

---

## For developers

### Requirements

- Unity Editor **6000.2.6f2** (URP)
- Magic Leap Unity SDK **2.5.0**, XR Hands 1.5.0 and Input System 1.14.2 (these install
  automatically from `Packages/manifest.json`)

### Opening the project

1. Open the project in Unity 6000.2.6f2.
2. Open **`Assets/Scenes/HelloCube.unity`**. The name is left over from the original
   template, but this is the real training scene.
3. **To test in the Editor:** press Play, then press **Space** to start a session. Use the
   mouse to click buttons on the settings board, and press **L**, **H** or **R** in place of
   the voice commands "Lifetime", "Last session" and "Repeat".
4. **To run on the headset:** build and deploy to the Magic Leap 2 like any other Magic Leap
   Unity project. The virtual floor lines up with your real floor.

### Where the code lives

All of the project's own code is in `Assets/Scripts/Lacrosse/`:

| What it does | Scripts |
|---|---|
| Runs the session and fires shots with realistic ball flight | `RandomLauncher`, `BallFlight`, `ShooterAnimationController`, `LacrosseStickGrip` |
| Decides whether each shot was a save or a goal | `GoalDetector`, `GoalieSaveZone` |
| Controller vibration and crowd sounds | `ShotOutcomeHaptics`, `CrowdAmbience` |
| Heatmap, shot trails and spoken summary | `ShotHeatmapRecorder`, `ShotPathRecorder`, `ShotPathVisualizer`, `SessionSummarySpeaker` |
| Players and saved session history | `PlayerProfileStore`, `SessionLogger`, `SessionHistory` |
| Settings board, difficulty, gaze start and voice commands | `SettingsPage`, `RayButton`, `ControllerRayPointer`, `DifficultySelector`, `GazeStartTarget`, `VoiceCommands` |

---

# Copyright
Copyright (c) 2020-present Magic Leap, Inc. All Rights Reserved.
Use of this file is governed by the Developer Agreement, located
here: https://id.magicleap.com/terms/developer
