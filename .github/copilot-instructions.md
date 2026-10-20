# TermCity project guidance

## UI conventions

- All menus, submenus, dialog boxes, confirmation prompts, hover tooltips and
  other popup surfaces must use the same terminal treatment: a black background,
  two closed white border lines spaced four logical pixels apart, and the shared
  12-logical-pixel content inset. Apply this automatically to new or changed UI;
  do not wait for an explicit styling request.
- Reuse `TerminalFrame` in `godot/TerminalFrame.cs` for control-based frames and
  its tooltip style for Godot's `TooltipPanel` theme. Do not introduce default
  Godot popup styling or unrelated border implementations.
- Preserve colored text headers and accessible focus states within these frames.
- Maintain the tight terminal look across the whole interface: top-bar sections,
  sidebar, construction rail, map frame, camera controls and popups. Use compact,
  even padding, square corners and clean double-white border joins. Preserve
  existing single-line table/icon exceptions; do not add bulky gutters, rounded
  cards, ornamental medieval frames, gradients or default Godot button chrome.
- Medieval, voxel and reference-game styling cues apply to terrain and buildings,
  not the surrounding UI. New rendering modes and controls must integrate with
  the existing terminal themes and `TerminalFrame`, not replace the interface's
  black background, white borders or established typography.
- Construction icons are an explicit exception: each icon gets one white
  outline. Only the surrounding rail uses the shared double-line border flowing
  from the header. At three-way joins, keep the outer line continuous and the
  inner square corners separate; never bridge the space between branch lines.
- Verify affected popup styling and layout with the Godot integration smoke
  checks, including narrow windows.

See [the developer guide](../docs/DEVELOPMENT.md) for architecture and validation
commands.

## Map and artwork direction

- Do not use stippling, scattered dots, squiggles or fine-line textures for map
  artwork or area overlays. Vacant areas use a small, regular checkerboard of
  broad translucent color blocks, with clear zone colors and visible terrain
  between the stronger checks.
- Keep the medieval setting with an original, restrained, block-built pixel-art
  style. Classic city builders and medieval strategy games are inspiration for
  perspective, palette and atmosphere only: never import, trace or reproduce
  their sprites, textures, models, screenshots or distinctive artwork.
- The default map is genuinely tilted and rotatable, not merely top-down sprites
  drawn at a different angle. Preserve visible roofs, fronts and sides, the
  right-facing initial camera, and the optional overhead view.
- Larger plots should expand buildings, wings and courtyards horizontally, not
  stretch towers. Center windows between timber posts and floor bands; leave
  readable doorways and evenly spaced, nonoverlapping crenellations. Enlarge
  cramped placement footprints rather than squeeze details together.
- Make the entire visible building clickable, including roofs and overhangs;
  preserve frontmost picking, camera-aware placement and panning, and legacy
  saved footprints when building sizes change.
- Keep normal zoom close enough to read building details. Render the 3D scene at
  native panel resolution with edge antialiasing; do not restore half-resolution
  upscaling that causes small-detail artifacts. Map zoom and time-speed labels
  are distinct; time uses 1x/2x/3x for Slow/Medium/Fast.
- Use visible coarse, low-contrast pixel/voxel patches for grass and water, not
  smooth featureless gradients, dense bright flecks or thin squiggly wave lines.
  Tree foliage should have clustered pixel textures matching the buildings,
  not plain untextured cubes or noisy single-pixel highlights.
  Render water's edges as whole-cell blocky steps matching the hill terraces,
  with closed earthy bank faces. Keep logical water, buildability, bridge rules,
  road curves, widths and grades intact.
- Hills should rise through prominent blocky terraces, with the same grass
  surface on top as the base terrain and brown, layered cliff faces. Ground
  building plots on level foundations when they span different terrace heights.
  Keep upper terrace tops and edges level, including beside rivers; do not taper
  upper blocks down to the next terrace. Preserve the blocky shoreline style.
- Roads in perspective use dedicated centerline meshes, with shoulders,
  terrain cutouts and cut/fill walls derived from the same offset edges.
  Preserve disjoint junction surfaces, shared grades, actual-triangle picking,
  cached chunks and original pixel materials; do not restore painted terrain
  roads or grid-mask cutting heuristics. Measure actual minimap click-to-render latency on
  large random maps and populated cities, not only buffered camera pans.
- Road types meet as ordinary intersections, never automatically generated
  highway on-ramps. Poorly angled minor-road approaches may delay travel.
  Keep traffic-circle placement atomic, previewed and undoable as ordinary roads.
- Knock down thin terrace/cut-wall remnants into supported road grade; keep
  substantial blocky hilltops. Water must not inherit hill levels. Rivers need
  consistent downstream flow and gentle gradients; waterfalls are exceptional,
  not a default repeated terrain effect. Render bridges over water and short
  steep valleys with decks, parapets and supports, retaining terrain underneath.
- Road inspection and selection must match the actual mesh, with no "drawn
  nearby" status fallback. Retired painted-road, ASCII-map and glyph-animation
  tests should remain explicitly skipped; keep current geometry/gameplay tests.
- Perspective service-range guides must remain screen-space circles at every
  tilt and zoom, not projected ellipses or terminal-aspect-stretched rings.
- Allow perspective panning beyond boundaries so every map edge can reach the
  viewport center. Preserve off-map picking/placement rejection and clip minimap
  viewport outlines to the real map. Dragging must continue over off-map space.
- Slow perspective drags must move continuously in screen space, not zigzag from
  cell snapping or terrain-height changes. Use planar drag anchors and retain
  fractional camera movement; keep terrain-aware picking separate.

## Simulation decisions to preserve

- Roads are optional travel-speed boosts, not settlement/connectivity gates.
  Sims walk over land; repeated journeys gradually form persistent paths toward
  reachable roads and between nearby buildings, including roadless settlements.
  Render paths as connected turns/junctions reaching building edges, not detached
  dashes. Water crossings still need bridges.
- Services have travel delays and capacity limits, not hard radius cutoffs.
  Petitions and reports should distinguish missing dispatch, awaiting arrival,
  slow response, and near/full capacity. Keep the expanded service ranges.
- Residents must not expect or petition for locked services. Growing Hamlet at
  250 souls unlocks entry-level fire, watch/sheriff, healing and learning.
  Village at 500 unlocks grain management, Market Cross and Granary; do not
  impose grain costs, hunger or famine before that permanent milestone.
- Treat multi-cell buildings as one building for population, jobs, income,
  upkeep and service output. Single-click zoning must reserve a complete plot;
  explain cramped plots or stalled growth instead of reporting them as ready.
- See [population rules](../docs/POPULATION.md) and the developer guide for
  formulas, tier gates, persistence and rendering details.

## Verification and completion

- **Night mode**: when the user says "night mode" or asks for quiet overnight
  work, disable all spoken/audio notifications and automatic interactive game
  launches/relaunches until the user explicitly turns night mode off. Continue
  implementation, bug checks, tests and performance analytics quietly. Prefer
  headless validation; do not open graphical smoke windows overnight. Preserve
  existing user game windows. Suppress already-scheduled audio alerts as well.
  Night mode is currently **OFF** (disabled October 12, 2026). The normal
  completion/preview rules below are active.

- For renderer changes, inspect a graphical capture and test rotation, zoom,
  picking and previews, not just compilation. Preserve batching, cached terrain,
  buffered panning and separate indicator updates; selection must not rebuild
  the entire city. Check large random maps and populated scenarios using the
  existing perspective performance smoke test and its frame/interaction budgets.
- When performance is close to a budget and requested functionality and correctness
  checks pass, finish the work instead of repeatedly chasing incremental gains.
  Note the measured result, budget and overrun in the final chat summary; do not
  hide the finding or weaken the benchmark. Near-limit performance findings alone
  do not block completion. Crashes, correctness failures and substantial
  regressions still require fixes. Continue deeper optimization only when asked.
- Keep intermediate builds and tests quiet. Use the local herald voice only after
  the final game build and requested functional validation are finished.
  Do not use audio to announce test results.
  If a game window is already open, preserve it and do not open another one.
  Otherwise, launch that exact latest Debug build while validation runs; confirm
  the owned process stays alive and startup reports no errors. Do not launch
  stale binaries or rebuild files underneath an already-running preview.
- Monitor only whether the mouse has moved since launching the preview, not
  keystrokes or other user activity. If tests fail before any mouse movement,
  close only the preview process launched for that validation attempt, fix the
  failure, rebuild and relaunch quietly while rerunning relevant tests.
  Once the mouse has moved, do not automatically close the user's preview;
  report the failure and obtain permission before interrupting it. If pointer
  monitoring is unavailable, do not assume the user has not interacted.
- Report validation outcomes in the final chat summary, explicitly distinguishing
  passed checks from failed checks and near-limit performance findings.
  Test results must be silent, not spoken.
  Reserve "All is ready, sire" for fully verified completion.
- Announce completion with a brief generic medieval-soldier/herald-style
  synthesized line, not a chime, only when requested work is fully finished and
  verified, just before the completion summary. On this macOS workstation use
  `say -v Samantha -r 175 "All is ready, sire."`; vary with similar short lines such
  as "The work is done, my lord." or "All is in order, sire." Use original wording
  and a stock local synthetic voice, not copied game voice clips or impersonation
  of a specific actor. Prefer natural speech and pacing: completion announcements
  need not sound 16-bit, and should not use robotic/retro voice effects or Daniel's
  previous slow delivery. Do not claim stock local speech has neural-voice quality.
  Do not announce completion during intermediate progress,
  unfinished background tests or unresolved functional validation failures.
  Disclosed near-limit performance findings follow the completion policy above.
  If local speech is unavailable
  or fails, report that briefly rather than claim it played; do not install voice
  tooling or download audio solely for this notification.
