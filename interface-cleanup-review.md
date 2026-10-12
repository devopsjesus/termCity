# Interface cleanup review

Follow-up to [the coastal verification report](./coastal-cleanup-verification-report.md), focused on the current perspective interface and the latest rendering changes.

## Fixes

- **Clean incline shoulders and bridge banks:** road shoulders now share canonical world-space vertex heights across adjacent terrain cells, eliminating the internal brown terrace wedges. Remnant cleanup is restricted to actual land polygons, and road excavation retains water underneath approaches; this closes the reproduced black strip under a generated bridge. The terrain's palette, shoreline contour and terrace layout remain unchanged.
- **Woodland variety:** four broad-leaf crown shapes and two tiered pine silhouettes now have stable coordinate-based height, width and foliage-color variations. Pines use a darker evergreen palette. Natural and planted/service trees share the batched model path and existing four-pixel foliage texture clusters; tree placement, saves and terrain are unchanged.
- **Complete generated bridges:** scenario crossings are now validated against the actual accepted road plan, including both land approaches, before water cells are committed. Rejected banks no longer leave half a bridge. Random highway routes reject water endpoints, and fallback routes omit leading/trailing open water. This prevents new malformed spans; it does not rewrite roads in existing saves.
- **Visible river motion:** sparse chunky ripple accents now travel downstream on rounded cell-to-cell tracks at 0.75 world cells per second, with eight updates per second. Equal-distance downstream routes prefer bank-parallel motion with similar bank clearance instead of the arbitrary shortest-path tie that sent accents across wide channels. The base water texture and most pixels stay still instead of moving every blue cluster. Low effects work at normal and close zoom. Decorative flow continues while paused, without advancing gameplay; effects-off and reduced motion still freeze it. The muted palette, lakes/sea, river identity and terrain geometry are unchanged.
- **Square rotating minimap:** the overview stays flat and square at every heading and tilt. Its entire contents rotate in 90-degree steps with the map. The white indicator now traces the actual camera ground footprint, changing angle/shape with the view instead of using the session's axis-aligned bounding rectangle. Clicks and drags use the inverse quarter-turn transform. Camera changes reuse the cached terrain image; the legacy cell overview remains north-up.
- **Invisible zoning:** the perspective renderer omitted vacant zones even though zoning cleared natural features. Residential, commercial and industrial plots now reuse the existing zone colors, reachability dimming and overlay tint on terrain-following surfaces. Overlays disappear when occupied or dezoned. Existing terrain geometry and textures are unchanged.
- **Sim-worn paths:** perspective paths follow actual cached traveled routes with rounded turns, not arbitrary adjacency between every worn cell. Parallel routes have no invented ladder rungs. Terrain-following ribbons now replace rectangular bars; road contacts widen smoothly and are clipped against the actual road surface, including projected diagonal-road targets. Free ends and abandoned patches are rounded. Routing, wear, travel costs and save data are unchanged.
- **House variation:** residential models use stable coordinate-based differences in wall/roof palettes, proportions, roof direction and pitch, low wings and chimneys. Original medieval artwork replaces the basic box-house appearance with broad timber corners/eaves, plaster gables, recessed window surrounds, stone sills and solid roof caps. Service, shop and workshop models retain their appearance.
- **House materials:** cached 32×32 textures now use four-pixel clusters for plaster and broad solid thatch, tile and masonry blocks. Removed narrow thatch scores, tile highlight strips and mortar seams. Structural trim stays solid-colored. House shapes and palettes are unchanged by this cleanup.
- **Roomier homes:** new full-rules residential growth uses deterministic 2x2–4x4 preferences, fitting down to 2x2 when constrained rather than generating new 2x1 houses. Single-click zoning and growth blockers use the same 2x2 minimum. Existing homes, classic growth and per-building capacity remain compatible; saves also accept four-cell-deep residential plots.
- **Hillside houses:** inset stone lower levels follow house and wing walls into the existing slope, rather than filling the reserved footprint with wood supports or a floating plot slab. Deeper exposed sides receive lower windows; shallow drops stay masonry footings. Rotation and picking follow the visible model. Terrain, simulation capacity, placement rules and costs are unchanged.
- **Checkerboard area overlays:** replaced the interim stippled shade-glyph texture with a regular small checkerboard using the existing green/blue/gold palettes. Solid 2x2-pixel blocks form four checks per world-cell side. Colored checks are 58% opaque and alternating gaps about 11%, improving visibility while preserving terrain detail. No stippling, fine outlines or squiggles; the no-stippling rule is also recorded in Copilot instructions. Terrain, reachability dimming and active-overlay colors remain unchanged. Graphical checks compare colored checks and transparent gaps against the same ground at four headings.
- **Contextual building rotation:** during building placement, `[` rotates the building 90 degrees left and `]` rotates it right, without rotating the camera. Outside placement they still rotate the map. The ghost, actual model, footprint, terrain cost, validation and picking agree; movement preserves orientation. Saves and undo retain rotation, demolition clears it, and older saves default to the original orientation.
- **Bridge-edge picking:** deck-side triangles now join the retained picking geometry. Railings and posts participate in exact transformed-box picking and resolve to a road cell on their owning bridge path, rather than adjacent water.
- **Current-interface test assumptions:** the invalid-placement color fixture now uses an obstructing zone rather than water, which roads can cross. Native rotation checks explicitly initialize the perspective view before inspecting its heading and restore the prior mode afterward.

## Verification

River-course direction refinement (October 12, 2026): reproduced five failing
cases where wide straight or bent channels sent ripple tracks across the river
because equally short outlet routes inherited arbitrary BFS parents. Downstream
choices now prefer similar bank clearance and a short bank-aware lookahead,
while still reducing outlet distance on every step. All five regressions pass,
including horizontal/vertical channels with even/odd widths, both banks and
the center, and the stretches before/after a wide bend. **97 focused tests
passed, no skips**. Solution build has zero warnings/errors; editor diagnostics
and diff whitespace checks are clean. Full graphical perspective smoke passed
on medium seed 42, including **6/6** tracked downstream ripples at normal zoom
and **3/3** at close zoom. Before/after captures were visually inspected.
Paused motion, accessibility freezes, terrain, roads, bridges, houses, overlays,
trees, paths and square-minimap checks pass. Outlet distances, water levels,
classification, saves, terrain geometry and the ripple artwork are unchanged.
The game preview includes the flow fix; no scenario benchmarks were added.

Square-minimap refinement (October 12, 2026): **41 focused tests passed,
6 existing legacy skips**. Build has zero warnings/errors; editor diagnostics
and diff whitespace checks are clean. Full graphical perspective smoke and
headless UI smoke passed on medium seed 42. The minimap fills the same fixed
square at all four camera headings; its contents rotate once per 90-degree
camera turn. The white outline's corners map back to the actual viewport
corners, and all four edges have visible white pixels in the captures.
Tilting reshapes that outline without resizing or tilting the square overview.
Rotated click/drag navigation and cached-texture reuse pass. Core regressions
cover square corner coverage, non-square map round trips, signed quarter turns,
full rotations and angle-preserving edge clipping. Captures at the first two
headings were visually inspected. The latest Debug game was launched and its
startup/process checked. Terrain, roads, river artwork and simulation are
unchanged by this refinement; no scenario benchmarks were added. The unrelated
rename-caret and separate large-map foundation fixture caveats below remain.

Historical sparse-ripple/rotating-minimap follow-up (the tilted overview is
superseded by the square-minimap refinement): **195 focused tests
passed, 22 existing legacy skips**; solution build has zero warnings/errors.
Full graphical perspective smoke passed on medium seed 42. All four minimap
headings match the actual camera axes; clicks and drags reach their world cells
without rebuilding the cached image. The captures were visually inspected.
Core regressions cover non-square map round trips, quarter turns, full rotation,
sparse ripple spacing, downstream cell joins, rounded-turn containment and
deterministic layout. Timed paused captures track **6/6** downstream ripples at
normal zoom and **3/3** at close zoom. Only **591/17,075** and **1,230/21,866** blue
samples changed between frames, respectively, keeping most water pixels still.
Paused simulation time and geometry-build counts stay fixed; effects-off and
reduced-motion freezes pass. Existing terrain, incline, bridge, shore, house,
overlay, tree, path and picking checks pass unchanged. No user game was terminated;
after the prior window closed independently, the latest Debug build was launched
and its process/startup checked. No scenario performance investigation was added.
The separate large-map house-foundation fixture caveat remains open.
The additional broad graphical UI smoke stopped at an unrelated rename-caret
pixel check (12/16 solid pixels); rename code was not changed. This does not
affect the successful focused perspective/minimap/ripple checks above.
Full headless UI smoke passed, including narrow-window layout and native
minimap navigation, without the graphical caret-pixel assertion.

Historical river-motion/bank-road follow-up (the all-cluster animation below is
superseded by the sparse-ripple refinement): river texture clusters traveled downstream
instead of merely pulsing the brightness of stationary water. The muted blue
palette, eight-update-per-second cadence, pause behavior and accessibility
switches are retained. Cached chunks share the same rendered flow-clock phase.
Road bridge classification now follows actual whole-cell water, not the old
smoothed shoreline field. A reproduced dry bank protrusion previously acquired
a raised one-sided deck despite containing no water road cells; it now remains
an ordinary road. A second regression reproduced the medium-map seed 42 stub:
dead-end curve padding reached the neighboring water cell. That padding now
stops inside its last dry cell, with four-direction coverage. The logical road
network, widths, other curves, complete crossings and saved maps are unchanged.
Both regressions failed before their fixes and pass afterward.
Graphical perspective smoke passed on medium seed 42, including real timed,
paused before/after frames at both playable zooms. Downstream texture matches
were **121/126 versus 26/126 stationary matches** at normal zoom and **59/59
versus 10/59** at close zoom, verifying actual texture travel rather than just
pixel brightness changes. The captures were visually inspected. Effects-off
and reduced-motion freezes, unchanged paused simulation time/geometry builds,
existing bridge approaches, shore color, terrain, house, overlay, tree and path
checks also passed. Final validation: **128 focused tests passed, 22 existing
legacy skips**, including medium seed 42 and large seed 1773051603 generated-road
regressions, plus a zero-warning/error build and clean editor/whitespace checks.
No scenario benchmarks were added; the separate large-map house-foundation
fixture caveat remains open. The verified build was launched after the previous
game window closed; no running user game was terminated.

Shore-color follow-up: replaced the old smoothed-density bank tint with a
binary, cardinal land/water adjacency mask matching the current block edges.
Every shore land block now shares the same earthy palette and existing broad
pixel clusters; diagonal-only contacts and off-map padding stay untinted.
Shore geometry, roads, heights, beaches, saves and gameplay are unchanged.
The new native pixel regression reproduced a green gap before the shader fix.
Final validation: **89 focused tests passed, one existing legacy skip**, a
zero-warning/error build, clean editor diagnostics and whitespace checks, and
successful full graphical perspective smoke on medium seed 42. Native coverage
checks 36 straight/stepped bank samples across four camera headings, with
captures visually inspected; existing incline, bridge, ridge, beach, picking,
house, overlay, tree, path and paused river-animation checks also passed.
No scenario benchmarks or broad-suite rerun were added. The separate large-map
house-foundation fixture caveat below remains unchanged.

Blocky-shore/incline-gap follow-up: **155 focused tests passed, one existing
legacy skip**, with a zero-warning/error build and successful full graphical
perspective smoke on medium seed 42. Reproduced the screenshot's black background
gaps using the latest autosave's large-map seed **1773051603** without modifying
the save. Six natural hill-road locations at four headings and two generated
bridge approaches at four headings passed after the fix. Standard-map native
checks also passed for level ridge tops, whole-cell bank edges, beach land seams,
road/bank closure, earth-colored incline shoulders, diagonal cuts, picking,
house rotation, checkerboard overlays, trees, paths and paused river animation.
Captures of the repaired caps and blocky shores were visually inspected; editor
diagnostics and whitespace checks are clean.

The gap fix extracts actual exposed edges of retained land polygons and cancels
shared internal edges before building cut faces. It replaces broad side-probe
assumptions that missed narrow caps, without adding overlapping internal walls.
Inclined shoulders use earth even on low approach cells. Visible shores now use
whole-cell land/water blocks and cell-centered bank tint; the smoothed field used
by road/bridge profiles and coastal-cap classification remains unchanged. Road
centerlines, curves, widths, grades, bridge spans, logical terrain and saves were
not changed. Shoreline fixtures now distinguish intentional land/water steps
from actual land-to-land cracks and verify walls against the rendered surface.

The broader large-seed smoke stopped later at the separate inset-house foundation
assertion, after its natural incline and bridge checks passed. That full large
run is not claimed passing; the house fixture remains to investigate. No house
model changes, scenario performance benchmarks or long playthroughs were added
for this request.

Ridge follow-up: removed the coastal upper-terrace taper that pulled grass-panel
edges downward into triangular points. Upper blocks now keep their own level
tops and vertical stepped faces. Smooth shoreline contours, base-bank/beach
shaping, isolated/sliver cap suppression, terrain textures, logical hill cells,
and existing road-shoulder/bridge cleanup remain intact. Removed the unused
coastal-slope cache and subdivisions; road profiles sample the same level tops.
The new coastal-road regression and native upper-ridge mesh checks both failed
before the fix, reproducing the height drop. Final validation: **154 focused
tests passed, one existing legacy skip**, with a zero-warning/error build and
successful full graphical perspective smoke on medium seed 42. Native checks
verify level upper-ridge triangle vertices and closed shoreline walls; ridge
captures at four headings and two zooms were generated and visually inspected
at all four headings. Existing incline shoulders, natural/constructed bridges,
beaches, picking, house rotation, checkerboard overlays, trees, smooth paths and
paused decorative river flow also passed. Editor diagnostics and whitespace
checks are clean. No scenario performance benchmarks or long playthroughs were
added for this narrow visual change.

Latest incline/checkerboard/paused-flow follow-up: **113 focused tests passed, no skips**, and the build has zero warnings/errors. Both native graphical reproductions failed before their respective fixes: a terrace face inside the tan incline shoulder, and a black water gap under an actual medium-map generated bridge.

Full perspective graphical smoke now passes, including three natural bridge approaches scanned at four headings, constructed bridge banks at four headings, continuous incline shoulders, checkerboard colors/transparency at four headings, and decorative flow while paused without advancing gameplay or rebuilding terrain. Effects-off/reduced-motion gates remain intact. Captures of the actual generated bridge and checkerboard overlays were visually inspected.

The broader non-playthrough suite completed with **1,145 passed and 47 existing skips** before the final checkerboard/road-mesh follow-up; the latest focused and graphical checks cover that follow-up. Counts below are historical and overlap. No running user game was terminated. After the earlier game closed, the latest rebuilt game was launched and startup reported no errors.

Tree/bridge/flow follow-up: the two new bridge reproductions failed before the generation fix and now pass. The focused bridge, map-generation, road-mesh and core flow suite passed **62 tests, no skips**. Solution build has zero warnings/errors. Full graphical perspective smoke passed, including all six tree silhouettes and captured coarse river motion with low effects at normal and close zoom (12,233 and 23,332 changed sampled water pixels respectively). The then-current pause, effects-off and reduced-motion checks also passed; pause behavior was subsequently changed as requested and verified separately above. Tree and river captures were visually inspected.

Broad-texture/roomier-home follow-up: **126 focused tests passed, no skips**. Solution build has zero warnings/errors, editor diagnostics and diff whitespace checks are clean, and full graphical perspective smoke passed. Tests cover opaque four-pixel texture clusters, solid eight-pixel roof/masonry courses without score lines, mirrored roofs, shared zone colors with transparent gaps, 2x2 single-click plots at map edges, narrow-strip growth blockers, varied 2x2–4x4 growth, 4x4 save/load, existing small-home compatibility, and progression guidance. House graphical fixtures now use 2x2 plots and a camera framing that keeps all six fronts on screen; house and zone captures were visually inspected. Terrain and service-building artwork remain unchanged. The earlier counts below are historical and overlap.

The broader non-playthrough run finished with **1,124 passed, 47 existing skips, and one failed starter-guide fixture** that still designated a one-cell-deep residential strip. That fixture now designates two rows, and the complete progression class passes in the subsequent 126-test focused run. The broad run preceded six additional texture/save tests; its totals are not a fresh all-green full-suite rerun. Long playthroughs and scenario performance benchmarks were not run for this cleanup. The updated game was launched after the earlier game closed; no unsaved running game was terminated.

Alternate-stipple/road-join follow-up: **120 focused tests passed, no skips**. The build and editor diagnostics are clean, and full graphical perspective smoke passed. Coverage includes shared legacy zone colors/tints with transparent gaps, four-heading rendered stippling, road-contact flares in all cardinal directions, diagonal-road targets, route save/load stability, real sim-generated trail pixels meeting both sides of the road contact, and unchanged road-center pixels. Overlay and road-join captures were visually inspected.

Medieval-house/hillside follow-up: **87 focused tests passed, 6 existing skips**. The solution build has zero warnings/errors, editor diagnostics are clean, and the full graphical perspective smoke passed. Texture coverage checks deterministic opaque two-pixel clusters and mirrored roof courses. Graphical coverage checks broad architectural details, four-heading lower-level picking, four building orientations, inset house/wing support alignment and unchanged terrain heights. House and hillside captures were visually inspected.

Wall-texture/transparency follow-up: **58 focused tests passed**, with a zero-warning/error build and successful graphical perspective smoke. House textures were visually inspected; zone pixels were checked against the underlying terrain at 35% opacity at four headings. The existing running game was not stopped or restarted.

Earlier artwork follow-up: **119 focused tests passed, 6 existing skips**, with a zero-warning/error build and a successful graphical perspective smoke. Coverage includes shared zone artwork/tints, parallel paths without ladder rungs, rounded turns inside traveled cells, unworn gaps, actual sim-worn routes across save/load, stable house variants and rendered house picking. Captures were visually inspected. The broader counts below belong to the preceding interface review, not a fresh broad rerun of this artwork follow-up.

- Debug solution build: zero warnings or errors.
- Non-playthrough repository suite: **1,095 passed, 47 existing skips**. This run preceded the last save-metadata tests and null-metadata guard; the focused suite below includes those final changes. Counts overlap and are not additive.
- Focused interface, rotation, affordability, service footprint, persistence, undo, terrain, road, river-flow and help regressions: **197 passed, 6 existing skips**.
- Graphical perspective smoke: passed, including actual plot-marker pixels at four camera headings, raised plots, zoning/dezoning and save persistence; building fronts and picking at four orientations; bridge geometry and railing clicks; coastal seams, hill ramps, traffic circles and river motion.
- Headless native-input smoke: passed, including real bracket key events both during and outside building placement.
- Editor diagnostics: no errors. Whitespace check: passed.

The graphical fixture places the progression-locked Fire Watch directly to check its asymmetric model. Session-level rotation tests use an unlocked Tavern, and native key-event tests use the Town Well; model verification does not imply that progression locks were bypassed in gameplay.

## Remaining limits

- The original report's older-save river inference and unusual-coastline heuristic caveats remain. Existing regression fixtures and graphical checks passed; no additional terrain tuning was warranted.
- The historical Godot/.NET finalizer abort was not reproduced in successful validation runs. Its cause remains unproven, so it is not marked fixed.
- Scenario performance was not rebenchmarked or optimized. No substantial scenario-speed regression was established, and the changes retain batched plot geometry and existing terrain meshes.
- Long playthrough tests were not completed; they are deliberately outside this interface-focused verification.
- Legacy overhead model orientation was not expanded; the current perspective interface is the target.

Save and restart any already-running game to load the rebuilt implementation. No commits were created.
