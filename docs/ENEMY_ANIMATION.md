# Enemy animation: reconstruction brief and reusable lessons

This is the design brief and engineering checklist for future enemy motion.
The approved orc is the reference example, not a requirement that every species
use the same body, weapon or three-blow combo. Keep goblins distinct and agile,
orcs solid and forceful, and trolls appropriately massive; approve their poses
and pacing independently.

## Verbal brief: the approved right-handed hammer combo

Use the **character's anatomical left/right**, not the viewer's. The orc holds
the lower end of the handle in its **right hand**. The left hand is free during
the two sweeping blows, then joins above the right for the overhead smash.
Think of a heavy head following a continuous arc: the body creates the motion,
the elbow guides the arm, and the wrist does not steer the weight by folding
sideways. Feet stay planted in the fighting stance, knees flex with the load,
and shoulders remain attached to the chest.

The following can be used as an authoring prompt without the original video:

1. **Load behind the right shoulder.** Start from the low ready hold and lift
   into a raised, rearward-loaded hammer pose. The shaft points back behind
   the orc rather than being held straight out to its side. A small backward
   body lean and torso turn prepare the weight. Keep the free left hand clear
   of the weapon, not reaching for a two-handed hold yet.
2. **Diagonal forehand to the left.** Drive the torso forward and turn toward
   anatomical left. The right arm delivers the head diagonally down and across
   the front of the body, opening toward a long reach without locking the
   elbow. The follow-through ends low on the left with a clearly forward-bent
   torso. The hammer head can remain above the wrist in this low pose; do not
   force it below the hand by breaking the wrist.
3. **Carry the head left and behind.** Let the heavy head continue left/rear
   briefly while the elbow begins organizing the return. The left arm swings
   behind the body to provide room. Roll the forearm and handle progressively
   while keeping the wrist largely continuous with the forearm. Do not lift
   a still-sideways head high to clear an incorrectly posed arm.
4. **Load the return across the chest.** Bring the right arm into the
   cross-body preparation while the torso is still turned left. The elbow
   starts the reversal and the forearm follows; this is not a sudden axial
   upper-arm twist. Blend small joint changes over the preceding frames.
5. **Extended backhand to the right.** Unwind the torso toward anatomical
   right and sweep the hammer across to a nearly extended right arm. During
   this sweeping return, the handle is approximately horizontal and the
   striking-face planes remain approximately vertical. Keep wrist side-bend
   restrained; the head's orientation comes from the coordinated arm and
   forearm roll, not a detached hand rotation.
6. **Drop and gather at the body center.** From the extended rightward finish,
   lower the hammer arm and sweep inward toward the waist/body center as the
   torso leans forward. The right hand must flow through the bottom of this
   arc directly into the next rise: **do not stop and wait for the left hand**.
   The left hand approaches the moving shaft and catches it just after the
   right hand begins rising, while both hands are still near the body center.
7. **Close the two-handed grip during the rise.** Once attached, slide the left
   hand down until it sits snugly above the right hand, without intersecting
   the gloves. Draw both hands upward together. Open both elbows outward as
   needed; their placement must determine the forearm and wrist pose. The
   right hand may roll around the handle while preserving its grip center
   and the weapon orientation. Keep the hands and forearms clear of the face.
8. **Complete a centered overhead load.** Raise the shoulder girdles naturally
   with the arms. Bring the hammer **upright and directly above the crown**,
   not merely somewhere high in front or off to one side. Verify this in
   both front and side views: the head center is vertically above the orc's
   head center in world space. By the start of the downswing, the striking
   face points straight ahead in the orc's facing direction. The torso leans
   back slightly, about seven degrees in this approved rig, and the upward
   motion slows markedly as the weight reaches the top.
9. **Torso initiates; arms follow.** Only after the centered overhead load is
   complete, start the body leaning forward. Initially retain the overhead
   arm pose relative to the torso, so the body carries the arms; then let the
   main arm sweep follow after a short lead. Accelerate over the crown and
   forward, keeping both hands attached and the elbows more extended than
   tucked. Pitch the striking face from forward toward downward without
   adding sideways yaw. Reach the ground with a flat face, a strong forward
   torso bend and flexed knees. Do not make the arms strike first while an
   upright body catches up afterward.
10. **Slow two-handed recovery and release.** Let the impact settle. Lift the
    head slowly with both hands while the torso straightens. Keep the weapon
    heavy and the grips connected; do not snap immediately to the ready pose.
    The left fingers open before that hand slides away and returns to rest.
    The right hand retains the weapon as the loop returns to ready.

For a similar enemy, preserve this causal order--load, body-led delivery,
follow-through, continuous transition, gathered overhead load, body-led smash,
slow recovery--while choosing that enemy's own proportions and approved poses.
Do not copy the orc's hammer choreography wholesale onto a spear fighter or
an archer.

### Walking and death motion

- Drive gait from actual travel distance and blend into the planted attack
  stance. Do not advance footsteps while the actor is stationary or inherit
  the study's display-only forward translation as gameplay movement.
- **Arrow death:** release the weapon, bring both hands toward the wound,
  then let the clutch go and collapse with loose, gravity-led arms.
- **Blade death:** recoil/stagger and fall to the side with a coherent torso
  turn; shoulders, elbows and wrists follow the fall.
- **Crushing death:** knees give way and the body folds, with the arms losing
  support and settling loosely. Keep the body and released weapon above the
  ground. These are stylized reactions, not a physics/ragdoll simulation.

## Reference implementation and timing

The original pose generators and grip solver are preserved in
[source.html](../tools/orc-motion/source.html). Its six `hammerReviewPoses`
are the approved reference poses for the first two swings. Later corrections
live in `overheadHammerPose`, `heldHammerPose` and `elbowDrivenHammerGrip`;
do not reconstruct the finished animation from only the six pose definitions
or resurrect superseded experimentation.

The source has three distinct time domains:

| Domain | Meaning |
| --- | --- |
| Authored joint-key time | Pose/torso/IK keys in the original procedural study |
| Baked clip/inspection time | Tempo-remapped poses plus the inserted midpoint inspection frame; 3.733333 seconds at reference rate, frames 0-112 at 30 fps |
| Simulation time | The clip phase advances at 1.25x; one combo takes 2.986667 simulation seconds, before the game's Slow/Medium/Fast time scaling |

`OrcAttackSeconds` stores the baked clip phase, not elapsed wall-clock time or
the raw joint-key time. Contacts are at clip times 1.043333, 1.884131 and
3.023333 seconds, corresponding to simulation times 0.834667, 1.507305 and
2.418667 at 1.25x. The development preview intentionally keeps reference speed.
Read [OrcCombatAnimation.cs](../src/TermCity.Core/Simulation/OrcCombatAnimation.cs)
for the authoritative constants; use the [native guide](ORC_MOTION_PREVIEW.md)
for export, sampling, rendering and validation commands.

## Lessons to carry into other enemy rigs

### Anatomy and continuity, not independent mesh spins

- A shoulder can rotate, but the humerus, elbow hinge, forearm and grip must
  form one coherent chain. Elbow flexion determines the bending plane;
  pronation/supination belongs to the forearm. Keep connected clavicle motion
  rather than translating a detached shoulder socket to gain reach.
- A visibly bent wrist is often an elbow-placement/grip-frame problem. Open
  the elbow and solve the wrist/forearm together. In the orc correction, the
  overhead wrist fold fell from roughly 110 degrees to roughly 19 degrees
  without changing the hammer trajectory or the other hand.
- The weapon and palm are different frames. A grip can roll around the handle
  while its shaft contact stays fixed. Derive the support hand from the weapon
  frame, not from a first-hand palm frame that has just been rolled.
- Threshold-changing helper axes can flip a mesh even when joint positions
  move smoothly. Use continuous orientation references/transport, quaternion
  sign continuity and elbow-plane coupling. Fade out an ill-conditioned hinge
  frame near straight-arm extension instead of normalizing a near-zero cross
  product. Preserve real anatomical shoulder movement; do not blanket-freeze
  every upper-arm rotation.
- Inspect local-axis meanings before measuring wrist angles: a bone's local Y
  and a palm's forward local Z are not interchangeable. Transform both vectors
  into the same space before comparing them. This is a simplified rig, not a
  universal human-range or torque model.

### Native fidelity and performance

- Preserve fixed bone lengths after interpolation, then anchor visible upper
  arms, forearms, bracers and hands to the resulting joints. Correct endpoint
  lengths alone do not prevent stretched/detached rendered parts between keys.
- Convert facing/handedness explicitly. The study faces +Z; Godot actors face
  -Z. Rotate the finished model for that conversion. Do not accidentally mirror
  handedness, turn a reflection into a quaternion or apply nonuniform scale
  in global axes. Compare transformed corners numerically and visually.
- Establish parity in a native development preview before wiring real combat.
  The current export uses 120-Hz original-part tracks and native C# sampling,
  not a live browser or an iterative grip solver per enemy per frame. Keep the
  preserved source, bake and fingerprints consistent when reauthoring.
- Feed parts through existing siege batching, actor caching and picking. A
  selection/pose update must not rebuild the city; do not add a scene node for
  every finger or armor plate. Preserve whole-visible-model frontmost picking.

### Combat, spacing and crowd behavior

- Damage must use the visible contacts and the same simulation/presentation
  clock for both wall and soldier combat. Keep pause, speed changes, save/load,
  worker snapshots and cycle wrap consistent. Do not substitute an unrelated
  visual loop or trigger damage merely because the cooldown reaches zero.
- For unchanged balance, `damage per contact = previous DPS * new cycle
  duration / contact count`. A 25% faster clip has an 80%-length cycle, so each
  blow must deal 80% of its former combo damage to keep DPS. Do not apply speed
  twice, or speed up walking/deaths when only attacks were requested.
- Choose stand-off from the actual scaled sweep and oriented target surface,
  not just collision radius or a target-cell center. The current orc body/hand
  reach is about 1.118 world units, weapon outer reach about 1.552, and wall
  stand-off/reach 1.40/1.50. These are **orc-specific reference values**, not
  numbers to copy blindly to a troll or another weapon.
- Move an overly close attacker physically and maintain its saved route.
  Never just pull its rendered body backward while damage/picking remain at
  the old location. Check terrain, walls and swept crowd separation, including
  an old embedded contact that needs to escape without penetrating farther.
- Diagnose corner jitter from positions, target IDs, route indices and
  headings over many ticks. The reported orc was alternating sideways steps
  against the same target, not continuously changing targets. Completing a
  safe grid alignment and adding the turn waypoint fixed it; extra facing
  smoothing or target-locking alone would only hide the symptom.
- Vary seeded, body-clear approach depths to stagger wave arrivals and real
  combo starts. Respect routes, first-assault side/warnings, crowd spacing and
  wave counts. If a desired depth has no free slot, use a reachable alternative;
  do not invent an off-map/water spawn or randomly offset visual-only hits.

## Reusable authoring and verification workflow

1. State the enemy, weapon, anatomical handedness, attack phases and intended
   damage contacts in words. Identify what existing behavior must be retained.
2. Establish original geometry/proportions, fixed bones, shoulder attachment,
   grip centers, weapon face/shaft axes and world height. Review front and side
   key poses before interpolating.
3. Connect the poses without teleporting joints, independent wrist/humeral
   twists, helper-axis flips, long unintended holds or an instantaneous recovery.
   Check anticipation, reversal, top deceleration, torso lead and grip release.
4. Verify the native preview against the authoring reference, then the actual
   game at real target distances. An isolated study does not prove wall
   clearance, picking, combat timing or crowd performance.
5. Test finite transforms, actual mesh endpoints, interpolated lengths,
   grip contacts, striking-face direction, floor clearance, swept intersections
   and temporal continuity. Check save/load and snapshots during attacks and
   every retreat turn, not only at an idle endpoint.
6. Run a loaded corner case long enough to expose repeated reversals. Check
   actual staggered arrivals/first contacts and body separation. Do not count
   one normal turn into combat as oscillation, or expect a destroyed target
   to remain attackable.
7. Inspect native front/side captures and test rotation, tilt, zoom, full-part
   picking, pause, all game speeds, walking and death variants. Sample contact
   boundaries and cycle wraps. Keep personal reference footage local.
8. Run the existing focused tests and graphical population/crowd performance
   smokes with their unchanged budgets; preserve unrelated enemy behavior.
   Coordinate builds with other sessions and never rebuild beneath a user's
   running preview. Report failures/near-limit results rather than weakening
   thresholds or claiming compilation alone verified the animation.

Current regression examples:
[motion/bone/grip parity](../tests/TermCity.Tests/OrcMotionClipTests.cs),
[contact/DPS/save/snapshot behavior](../tests/TermCity.Tests/OrcCombatAnimationTests.cs),
[spacing/corner/staggered approaches](../tests/TermCity.Tests/OrcAttackSpacingTests.cs),
and [actual game rendering](../godot/OrcCombatValidation.cs).
