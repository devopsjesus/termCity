---
description: "Use when creating or modifying orc, goblin, troll, defender or other enemy artwork, skeletal motion, weapon grips, combat animation timing, attack spacing or wave approaches."
applyTo: "godot/PerspectiveCity*.cs,godot/*Artwork.cs,godot/*Motion*.cs,godot/*Combat*.cs,godot/*Smoke.cs,godot/*Orc*.cs,godot/*Goblin*.cs,godot/*Troll*.cs,src/TermCity.Core/Rendering/*Motion*.cs,src/TermCity.Core/Rendering/*Animation*.cs,src/TermCity.Core/Rendering/*Artwork*.cs,src/TermCity.Core/Rendering/Siege*.cs,src/TermCity.Core/Simulation/*Siege*.cs,src/TermCity.Core/Simulation/Settlement*.cs,src/TermCity.Core/Simulation/*Combat*.cs,tests/TermCity.Tests/*Orc*.cs,tests/TermCity.Tests/*Goblin*.cs,tests/TermCity.Tests/*Troll*.cs,tests/TermCity.Tests/*Siege*.cs,tests/TermCity.Tests/*Garrison*.cs,tools/orc-motion/**"
---

# Enemy artwork and animation

Read [the enemy animation guide](../../docs/ENEMY_ANIMATION.md) before changing
an enemy rig or attack. It contains a verbal reconstruction brief for the
approved orc combo, applicable anatomy rules, failure modes and validation.
[The native orc integration guide](../../docs/ORC_MOTION_PREVIEW.md) documents
the current implementation, regeneration and smoke commands.

## Required workflow

- Preserve species identity and approved artwork/motion. Reuse the rigging
  principles, not an identical orc silhouette, choreography or speed for
  goblins/trolls. Obtain approval for substantial choreography changes.
- Review readable front and side key poses before connecting a new sequence.
  Then inspect the actual transitions, anticipation, contacts and recovery;
  passing endpoint tests does not establish believable motion.
- Work from original/reference-authorized material locally. Personal reference
  footage/images are not repository assets and must not be uploaded or committed.
- Coordinate shared files, builds and graphical benchmark windows with other
  sessions. Never rebuild underneath an existing user preview or close it
  without permission. A browser/native study is not main-game integration.

## Rig and weapon constraints

- Use fixed-length bones and connected shoulder-girdle motion. Derive upper-arm
  orientation from the elbow bending plane; keep forearm pronation and grip
  roll separate from humeral rotation. Do not repair a wrist fold with an
  unrelated hand or upper-arm spin.
- As an elbow opens outward, solve the forearm, wrist and grip together.
  Preserve the shaft grip center; a hand may roll around the handle without
  turning the weapon. In a two-handed hold, solve both hands from the weapon
  frame, not from the newly rolled first palm.
- Keep orientation frames continuous. Avoid reference-axis threshold switches,
  quaternion sign jumps and nearly-straight elbow cross-product singularities.
  Do not hide reversals by increasing facing smoothing.
- Preserve real bone lengths and visible joint attachments between samples,
  not just at authored frames. Apply nonuniform scales in part-local axes;
  handle reflections as signed scale, not a malformed rotation.

## Gameplay and world integration

- Use simulation contact events and the existing presentation clock, including
  pause, speed, save/load, snapshots and wrap interpolation. Keep damage on the
  visible contacts for both wall and soldier combat.
- Do not silently change DPS when changing clip speed or impact count. For
  preserved DPS, damage per contact is `old DPS * new cycle seconds / contacts`.
  Keep authored phase separate from playback rate.
- Measure body/weapon sweep in world space. Stop far enough from actual
  oriented target surfaces to expose the swing; use shared soldier/orc reach.
  Fix physical positions/routes, not a render-only backwards offset.
- At corners, finish a safe cardinal alignment before changing retreat axes.
  Check the full alignment path, preserve route validity and test save/load
  during retreat. Legacy overlap may escape outward, never enter deeper.
- Stagger real, seeded, body-clear approach distances to separate arrivals.
  Preserve dry-land reachability, crowd separation, wave counts and warnings;
  do not fake variety with animation phases disconnected from damage.
- Preserve world height, existing siege batching/pages, offscreen rig reuse,
  full-part frontmost picking and cached city geometry. Avoid a node per
  finger/armor piece or a whole-city rebuild for pose/selection updates.

## Completion gates

- Verify finite transforms, lengths, mesh endpoints, grip centers, wrist angles,
  actual striking-face orientation, clearance, floor contact and temporal
  continuity. Inspect native front/side captures, rotation, tilt and zoom.
- Reproduce bugs over time: a loaded corner attacker must settle into combat,
  not alternate steps indefinitely. A staggered wave must actually arrive and
  start its combos at different times, not merely have different spawn labels.
- Run focused motion/combat/save/snapshot/crowd tests and relevant native
  graphical smokes with existing budgets. Test populated combat and relevant
  large-map interactions, not only the isolated preview or buffered camera pans.
  Report failed checks, existing warnings and near-limit findings honestly;
  never weaken a benchmark to declare completion.
