# Native orc motion preview

For the verbal reconstruction brief and reusable constraints for other enemies,
read [Enemy animation: reconstruction brief and reusable lessons](ENEMY_ANIMATION.md).
Repository agents must also follow the
[scoped enemy-animation instructions](../.github/instructions/enemy-animation.instructions.md).

The approved original warrior-orc artwork and Hammer study run both in a
**native Godot development scene** and in normal perspective siege combat,
not an embedded browser. Enemy collision radii and average damage per second
are unchanged. Orc damage is now synchronized to the three visible contacts.

## Run

From the repository root, with the Godot .NET editor/runtime installed:

```sh
dotnet build godot/TermCity.Godot.csproj -c Debug
godot --path godot res://OrcMotionStudy.tscn
```

Coordinate builds with any other session using this shared worktree. Never
rebuild the assembly beneath a running user preview.

The scene is Debug-only and is not the game's default scene. It has the existing
terminal frames, local font and tooltip treatment, a native-resolution MSAA
viewport, a 30-fps inspection scrubber, playback, orbit/zoom, front/side views,
and frontmost whole-part box picking. Choose walking, the full Hammer combo,
arrow/blade/crushing death, or one of the six approved reference poses.
The combo still includes the inserted inspection frame and ends at frame 112.

The death clips include the original body and released weapon, but not the
browser's incoming-projectile/impact markers. Their labels identify the cause;
actual game hit events will supply those effects during the later combat stage.

## Authoring and native tracks

- [source.html](../tools/orc-motion/source.html) preserves the approved original
  procedural model, IK, elbow-plane constraints and grip-roll solver.
- [export.js](../tools/orc-motion/export.js) evaluates that source locally and
  exports stable original part identities, positions, quaternions, signed scales
  and arm diagnostics at 120 Hz.
- [export.py](../tools/orc-motion/export.py) uses a temporary isolated headless
  Chrome profile. It does not upload code/video or alter an existing browser.
- [hammer.orcmotion](../godot/Assets/OrcMotion/hammer.orcmotion) contains the
  compressed native tracks. Its adjacent JSON manifest fingerprints both source
  and asset.
- [OrcMotionClip.cs](../src/TermCity.Core/Rendering/OrcMotionClip.cs) validates and
  samples the tracks in C#. Arm directions interpolate with fixed .56/.56 bone
  lengths; visible arm/bracer/hand parts remain anchored to those joints.
- [OrcMotionStudy.cs](../godot/OrcMotionStudy.cs) converts authored +Z facing to
  Godot -Z facing by rotation, not a handedness-changing reflection, and sends
  the voxel boxes to color-grouped `MultiMesh` instances.

This bake avoids running the iterative browser grip solve per actor per frame.
It is a fidelity reference, not yet a crowd-performance solution: the asset
contains every original part at every sample. Later production integration can
factor rigid parts onto shared bone tracks or a native skeleton, using this
preview as the parity oracle rather than re-authoring approved motion by eye.

The original front/side videos and paired reference photographs are **not**
included in the repository. The committed authoring page's optional photographic
review needs the locally retained `orc-swing-pose-01-front.jpg`/`-side.jpg` through
pose 06 alongside it; geometry generation and the native preview do not need
those personal reference images.

Regenerate after an intentional authoring change:

```sh
python3 tools/orc-motion/export.py tools/orc-motion/source.html \
  godot/Assets/OrcMotion/hammer.orcmotion
```

On macOS the exporter defaults to installed Google Chrome; use `--chrome` to
select an existing compatible Chromium executable on another machine.

## Validation

```sh
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj \
  --filter FullyQualifiedName~OrcMotionClipTests
godot --audio-driver Dummy --path godot res://OrcMotionStudy.tscn -- \
  --orc-preview-smoke --orc-preview-capture=/absolute/path/orc-preview.png
```

The native smoke checks every clip, rotation, picking and 390px layout before
capturing and exiting. Inspect the capture; compilation alone does not establish
visual fidelity. Focused tests check original artwork/clip presence, approved
wrist angles, centered overhead load, fixed bones, actual interpolated mesh
endpoints, clamping and malformed assets.

The first native validation passed seven focused tests, a zero-warning Debug
build and the graphical smoke. The normal and narrow captures were inspected.
The smoke also compares every part's C# transform with its Godot conversion:
nonuniform scales apply along the part's local axes, not the global axes.
No gameplay/crowd frame-budget claim is made for this isolated development scene.

## Normal-game integration

[PerspectiveCity.Orcs.cs](../godot/PerspectiveCity.Orcs.cs) feeds the same
sampled boxes into the existing siege color pages and actor cache. Every
living body/weapon part participates in the existing nearest-part picking.
The six-foot world height is retained; the authoring study is not imported
at its arbitrary display scale. Travel distance drives the walking cycle;
the existing combat presentation fraction drives the combo and death tracks.
Arrow, blade and crushing deaths retain the original four-second motion;
oil/fall use the crushing track, with the existing charred oil tint.

[OrcCombatAnimation.cs](../src/TermCity.Core/Simulation/OrcCombatAnimation.cs)
owns the 3.733333-second authored combo and its three contacts at 1.043333, 1.884131
and 3.023333 authored seconds (including the study's inserted frame). Normal combat
plays this at 1.25x, taking 2.986667 simulation seconds, with contacts at
0.834667, 1.507305 and 2.418667 seconds. Walking, deaths and the development
preview retain their original reference timing. The torso-led ground smash is
the last contact. Each hit deals:

```
old damage per hit * (authored combo duration / playback rate) / (3 * old attack interval)
```

Thus a completed combo preserves the prior average DPS: at 1.25x, each blow
deals 80% of its previous combo-hit damage. Wall attacks and soldier melee use the
same clock; the first hit waits for its visible contact. Leaving combat cancels
the unfinished combo. Pause/speed behavior follows the simulation. Saves and
worker/presentation snapshots retain authored phase, including cycle-wrap interpolation;
older saves without the new fields start a neutral combo on contact.

Orcs stop 1.40-1.50 world units from the actual wall face, rather than walking
up to the old body-radius melee distance. This clears the measured 1.118-unit
forward body/hand envelope while the hammer's 1.552-unit outer reach still
reaches the target. Towers, gates and custom walls use their actual oriented
contact surfaces. Existing too-close attackers physically step back before
starting another blow, respecting dry terrain and crowd spacing. Retreat
updates the saved cardinal route rather than offsetting only the rendered model.
An already embedded legacy contact may escape outward but cannot move deeper
into another wall. Soldier/orc melee uses a shared increased engagement reach,
including the human body radius, so neither side continually chases into the
other's swing space. Goblin/troll collision radii and reach are unchanged.

When a retreat needs to change axes at a corner, the orc first completes a
body-clear alignment step to a grid waypoint. The full alignment path is checked
against terrain, walls and other attackers; the turn adds a cardinal saved-route
waypoint. This prevents a partial sideways step from reversing on tiny contact
coordinate differences instead of backing away. The reproduced saved siege went
from 98 reversals to one ordinary turn into combat over the same 60-second run.

Scheduled waves preferentially place successive orcs at different valid route
depths on the selected approach side. The seeded placement remains reproducible,
and still enforces body separation from orcs, goblins and trolls. If a desired
depth has no free slot, placement may use another reachable depth rather than
discarding the attacker. Different arrival times start real combos at different
times; no fake visual phase offset or change to damage cadence is applied.

The simulation does not load the visual asset. Contact times are authored
constants tied to the approved tempo track; regenerate/revalidate both if the
choreography changes. Deaths are presentation-only and no longer apply damage.

For the in-game graphical validation, load a 250-resident fixture containing
orcs at the walls:

```sh
TERMCITY_ORC_CAPTURE=/absolute/path/orcs.png godot --audio-driver Dummy \
  --path godot res://OrcCombatValidation.tscn -- --load /absolute/path/siege.json
```

This Debug scene uses the actual main-game renderer and checks combo poses,
selection/picking, rotation, all three original deaths, unchanged terrain
geometry and the existing active-swarm performance budgets. It exits rather
than leaving a competing performance window open.

Integration validation on this workstation:

- The corner/stagger follow-up passed 136 focused orc/crowd/garrison/presentation
  tests, including the saved-route corner regression, deterministic varied spawn
  depths and actual staggered combo starts. Both saved-corner and fresh-wave
  native graphical runs passed. Six-orc actor-worst/frame-p90 measured
  4.0ms/29.5ms; the fresh three-orc wave measured 3.0ms/17.9ms, within the
  unchanged 12ms/60ms budgets. The graphical garrison regression also passed
  with 120 attackers: actor-worst 6.8ms, frame p90 46.7ms, no city rebuilds.
- The stand-off update passed 133 focused orc/crowd/garrison/presentation
  tests, including four-sided, gate/tower, fresh approach, embedded-save and
  per-retreat-tick save round-trips. The measured native body swing envelope
  is explicitly checked against the configured clearance. Final graphical
  six-orc validation measured actor-worst 3.6ms and frame p90 23.9ms;
  the 120-enemy garrison smoke measured 6.7ms/33.5ms. Both passed the
  unchanged 12ms actor and 60ms frame-p90 budgets without city rebuilds.
- The 1.25x pacing update passed 31 focused motion/combat/presentation tests,
  a zero-warning Debug build and the in-game orc smoke. Six-orc actor-worst
  was 3.6ms and frame p90 22.6ms, within the unchanged 12ms/60ms budgets.
- 243 focused orc/siege/garrison/snapshot/responsiveness regressions passed.
  The earlier inclusive run additionally found the existing
  `TowerReservationsLeaveGridAlignedHumanAccessAndGatesHaveRealDecks`
  gate-deck assertion failure in untouched wall artwork; that one test was
  explicitly excluded from the final focused rerun, not fixed or weakened.
- Zero-warning Debug build; in-game 250-resident/six-orc graphical validation
  passed all seven inspected combo frames, actor picking, rotation and three deaths.
  Southern-wall actor-worst was 3.6ms against 12ms; frame p90 23.3ms against 60ms.
  The northern-wall run measured 5.2ms/30.0ms respectively, also within budgets.
- The standard large random-map perspective smoke passed: cold 362.9ms,
  frame p90 24.0ms, actual minimap click-to-render 180.7ms, selection/picking
  .1ms and road-preview worst 14.1ms.
- Graphical garrison smoke passed the 120-enemy run: actor-worst 9.9ms against
  12ms and frame p90 32.0ms against 60ms, with no city rebuilds.
- Godot reported shutdown-only ObjectDB leak warnings (one in the dedicated
  orc validation, two in the existing garrison smoke); no startup, rendering
  or gameplay errors were reported by those passing runs.
