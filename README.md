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

Goalie save detection (a stick-mounted trigger zone that registers a touch on the ball
mid-flight as a save) is in active development — see `Plans/Saving_Plan.md` for the design
and current status before relying on it.

# Copyright
Copyright (c) 2020-present Magic Leap, Inc. All Rights Reserved.
Use of this file is governed by the Developer Agreement, located
here: https://id.magicleap.com/terms/developer
