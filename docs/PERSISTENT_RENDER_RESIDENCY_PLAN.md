# Persistent render residency implementation plan

## Implementation status

Native terrain page batching and proactive whole-map native terrain warming
remain behind the `PersistentTerrain` / `PersistentScene` diagnostics and the
Debug-only `--persistent-residency` flag. Normal gameplay now retains unchanged
offscreen native terrain, object pages **and path pages under byte-pressure limits**.
The object-slot portion of the candidate is adopted independently; this does not
enable its whole-map native terrain warming, terrain pages, LOD or larger budgets.

Native terrain pages own installed surfaces and compact picking/cut metadata,
not all original offscreen preparation packets. New owners append surfaces
without rewriting existing geometry; edits/pressure replace affected pages.
Native building pages retain owner slots, reuse retired ranges and publish
staged changes only at completion. Camera motion defers discretionary warming.
Measured memory-fit classification selects whole-map-ready versus regional
retention without increasing the default targets.

See [implementation verification and findings](DEVELOPMENT.md#persistent-native-render-residency)
for the actual fit limits, matched results and adoption decision. This status is
not a claim that every medium map fits or that all performance budgets pass.

### Normal object residency adoption (November 9, 2026)

Normal rendering and the `Tuned` diagnostic retain published instance slots,
roof pages, original materials and picking packets across camera-only movement.
Only requested spatial pages are visible. Material pages span eight 16-cell
owners per axis (128 world cells); preparation, stamps and edit invalidation
remain owner-local at 16 cells. The first four-owner page trial duplicated more
material nodes, so eight-owner pages were measured instead. Neither layout
changes geometry, texture filtering, supersampling or MSAA.

The existing free-range allocator reuses slots on real edits/removals. Hidden
native object pages are reclaimed for independent CPU/native byte pressure,
not solely because they left the preferred region or a retained-node count was
reached. Unused native pools are disposed without uploading their cleared buffers
first. Pressure retirement also trims now-unpinned CPU replay. Targets remain
**256 MiB CPU / 128 MiB destination-native resources**; high-memory-load target
reduction and warming suspension remain intact. This is regional residency, not
an assertion that whole-map object geometry fits.

The paused fullscreen 858-resident, 48-step normal-zoom route measured:

| Measure | Fresh previous-default baseline | Resident objects |
| --- | ---: | ---: |
| Repeated object-instance upload bytes | 22,097,984 | **0** |
| Repeated roof upload bytes | Not separately sampled | **0** |
| Required-owner native republications | 184 | **0** |
| Object-pool compactions | 68 | **0** |
| Instance upload bytes across measured route | 46,211,152 | 12,043,264 |
| Readiness p90 | 661.5ms | 655.4ms |
| First-frame p90 / worst | 64.3 / 254.0ms | 60.1 / 72.3ms |
| Peak active / retained nodes | 689 / 892 | 777 / 1,024 |
| Sampled process peak | 681,918,464 bytes | 698,056,704 bytes |

These are individual runs, not statistical guarantees. Subsequent capture
verification again had zero repeated instance/roof bytes and zero required-owner
republications, but recorded 654.0ms readiness p90, 61.6ms first-frame p90 and a
180.5ms worst frame. It reached 861,388,800 bytes sampled process peak. Two earlier
root-window captures were mostly blank; the final rerun produced inspected full
UI and independent map captures. These attempts are not all-frame visual proof.

Paused-save equality, exact native buffers and terrain continuity passed.
Required terrain replacements and second-pass terrain uploads remained zero.
At this object-only stage, approximately **268MB of path uploads** remained and
path preparation/publication dominated navigation readiness. The stable-path
follow-up below addresses that separate cost.

**Validation:** build passed with zero warnings/errors; 61 focused cases passed,
with six retired cases skipped. New headless and graphical normal-object
lifecycle checks passed default/Tuned returns with zero instance and roof
uploads, stable slots, hidden-page visibility, node-only retention, local building
edits, independent CPU/native pressure and map replacement. Existing graphical
persistent lifecycle checks also passed rotation, zoom, roof picking, local
terrain/road edits and native geometry parity.

Live saved combat recorded p50/p95/p99 of **18.0/31.2/39.1ms**, worst 71.1ms and
2.314s ending backlog (2.263s drain). Zero held frames occurred among 37 eligible
motion frames. The 25ms p95 target still fails; GPU timing remained unavailable.
There is no claim of improved stationary FPS or real-time simulation.

Large seed-42 navigation/frame checks measured 340.2ms cold readiness, 23.3ms
frame p90, 3.0ms buffered pan and 169.7ms actual minimap click-to-render versus
250ms. **The overall command still failed:** it retained 513 counted render
nodes against the unchanged 512-node budget (+1), compared with 322 in the prior
nonresident run. At the final profile sample only 48 of 237 object pools and
four of seven roof pages were visible; retained resources are not all drawn.
The saved-city route also remains above its active-node and readiness limits.
The increased node count is an explicit residency tradeoff, not a budget pass.

`TERMCITY_OBJECT_UPLOAD_PROFILE` now reports active/resident object pools and
roof pages separately. `TERMCITY_REGION_SUMMARY` reports second-pass roof bytes.
Capture-enabled region diagnostics save an additional `.map.png` from the 3D
viewport alongside the root-window capture.

### Stable path-page adoption (November 9, 2026)

Normal rendering now retains dirt-path geometry by **eight-world-cell owners**,
grouped into **32-world-cell native mesh pages**. This size is independent of
terminal rendering: it keeps complete owners and their relief neighborhood
inside buffered ground preparation while limiting native node growth.
The diagnostic face-measurement path retains the complete reference pipeline.

Each owner has a content stamp from its local ribbon polygons, rendered wear
color, terrain and road dependencies, and rendering styles. Camera coordinates
and unrelated global changes do not invalidate identical content. Ordinary wear
increments with no visual change reuse the page. Prepared ribbon layouts include
an additional owner/dependency margin beyond the buffered camera window; an
initial implementation missed that margin and regenerated partial edge owners.
Ground dependencies must be available before capture, including just-offscreen
road-cut/grade geometry; unavailable dependencies are prepared asynchronously,
not silently replaced by flat terrain.

Only changed owners enter the coalesced worker. The original clipping, road
subtraction, relief, normals, texture materials and UVs are reused. Ownership is
assigned from the source-triangle center retained as each relief triangle's
first vertex: triangles are **not cut at page boundaries**. Neighboring source
faces remain in the dependency halo so shared-vertex lift limits agree with the
complete reference traversal.

Native replacements stage hidden surfaces in bounded upload slices and commit
the completed batch before disposing its old pages. Camera movement changes page
visibility, not native buffers. Live compatible wear can keep its last complete
presentation while preparing the latest; map/navigation/terrain/style/road
incompatibilities and removal reject obsolete workers. Matching content can be
certified for a newer epoch without a geometry rebuild.

Path CPU packets and native meshes, including staged replacements, are now
included in the existing **256 MiB CPU / 128 MiB native** accounting. Hidden
pages and their owner packets are reclaimed under independent CPU/native byte
pressure; required pages remain pinned and their overage is reported. Map
replacement and renderer disposal release the old owners/resources. Empty
owners do not allocate native mesh nodes. No larger budgets, LOD, reduced
render quality or GPU compute requirement were introduced.

The same paused 858-resident fullscreen 48-step normal route measured:

| Measure | Object-resident baseline | Stable paths |
| --- | ---: | ---: |
| Total path upload bytes | 268,085,696 | 22,677,200 |
| Second-pass path upload bytes | Not separately sampled | **0** |
| Second-pass instance / roof / terrain bytes | 0 / 0 / 0 | **0 / 0 / 0** |
| Readiness p90 | 655.4ms | **178.6ms** |
| Second-pass readiness p90 | 655.4ms | **54.7ms** |
| Worst readiness | 728.3ms | 323.3ms |
| First-frame p90 / worst | 60.1 / 72.3ms | 62.4 / 192.2ms |
| Peak active / retained nodes | 777 / 1,024 | 787 / 1,049 |
| Sampled process peak | 698,056,704 bytes | 640,647,168 bytes |

This bounded run reduced total path transfers about **91.5%** and readiness
p90 about **72.7%**. It is not a statistical guarantee or a first-frame latency
win: initial frame outliers remain. All 675 retained owners were built once;
the profile reported approximately 23.8MB managed path packets and 16.4MB native
path geometry. These estimates overlap process memory and must not be added as
independent physical allocations. The paused save, native object buffers and
terrain continuity remained exact, with zero required object republications or
terrain replacements.

**Validation:** build passed with zero warnings/errors; 73 focused tests passed,
with six retired cases skipped. Headless and graphical lifecycle checks passed
warm zero-generation/upload returns, complete-reference geometry, actual native
triangle/material/UV/upward-normal parity, unchanged visual wear, local visual
wear edits, independent memory pressure, map replacement, coalescing, pause/live
compatibility and stale map/navigation/terrain/style/road/removal rejection.
The graphical footpath-art suite passed five zooms/four rotations, cached/fresh
geometry, lit shoulders/shadows, actual-road clipping, doorway picking and all
220 entrance/rotation cases. Full-window, map, dirt and road-join captures were
inspected.

**Unchanged performance failures:** worst readiness remains 323.3ms versus
250ms, first-frame p90 62.4ms versus 60ms, worst first frame 192.2ms versus
100ms, and active nodes 787 versus 512. The region command still exits nonzero.
Large seed-42 performance measured 345.6ms cold, 24.8ms frame p90, 1.9ms buffered
pan and 193.2ms actual minimap latency; its total retained-node check still
fails at **513/512**. No thresholds were relaxed.

A final 45-second live saved-combat observation recorded p50/p95/p99 of
18.7/30.3/42.4ms, worst 87.6ms, 2.097s ending backlog and 2.515s drain.
Path-upload frames fell from 70 in the preceding object-resident run to 15;
global allocations measured 7.09GB. There were zero holds among only 29 eligible
motion frames, too few to establish the long-run motion-hold target. An earlier
observation still correlated a slow host event with a 54.58ms map publication;
this is not attributed to GPU work. GPU timing was unavailable. The 25ms p95
target still fails by 5.3ms, and sustained 60fps / guaranteed real-time combat
are not claimed. Idle path publication does not repeatedly resample its resource
totals; existing cache-retirement passes handle memory pressure.

`TERMCITY_PATH_PAGE_PROFILE` reports retained/built/reused owners, native/active
pages, publication counts and accounted CPU/native/upload bytes.
`TERMCITY_REGION_SUMMARY` now reports second-pass path-upload bytes directly.

## Objective

Make navigation primarily a camera/culling operation. On maps whose measured
working set fits, retain native terrain and building resources across scrolling
and minimap jumps; rebuild only genuinely changed content.

Allocated is not the same as drawn. Offscreen resources may remain resident
while only visible pages submit rendering. Do not draw the whole city every frame.
This is an architectural follow-up to the
[medium-city experiments](MEDIUM_CITY_BENCHMARK_RESULTS.md), not adoption of the
previous larger-budget, adaptive-upload or LOD experiments.

## Starting constraints

- Minimum-spec reference: the 8 GiB Apple M2 workstation.
- Start with medium 480x480 maps, beginning and genuine 858-resident saves.
  Include large random maps and TOP in regression testing.
- Current normal defaults remain 256 MiB CPU / 128 MiB destination GPU until
  measurements support a separate capacity decision.
- Preserve 16-cell preparation/invalidation chunks, road centerline geometry,
  cut/fill walls, banks, water, bridges, materials and exact terrain picking.
- Preserve native panel resolution, supersampling and 8x MSAA.
- Preserve simulation time, travel, dispatch, ownership, roofs/overhang picking,
  construction previews and the terminal UI.
- No guarantee that an entire medium map fits the existing GPU allowance.
  Previous experiments prove local retention helps, not whole-map fit.
- Implement behind one explicit diagnostic policy initially. Do not combine
  changes with overview LOD, larger halos or alternate rendering quality.

## Phase 1: measure and define residency ownership

Extend existing cache/upload diagnostics rather than adding a parallel profiler.
Measure medium base terrain, road/bank geometry, textures, object instances,
roofs/zones, picking data and required CPU backing separately.

Record cold publication cost, resident bytes, temporary replacement overlap,
pool capacity/fragmentation, driver/process memory and memory pressure.
Report requested versus actual physical display dimensions. Unified-memory
counters overlap; do not add them as independent physical allocations.

Define explicit states for prepared, native-resident, visible and replacement
pending resources. Resource identity and content stamps survive visibility
changes. Picking must use installed geometry, never a merely prepared replacement.

Use existing epoch, local stamp and lifecycle mechanisms in:

- [MapCache](../godot/PerspectiveCity.MapCache.cs)
- [GpuResidency](../godot/PerspectiveCity.GpuResidency.cs)
- [GroundPreparation](../godot/PerspectiveCity.GroundPreparation.cs)
- [ObjectChunks](../godot/PerspectiveCity.ObjectChunks.cs)

**Fit gate:** calculate whether the whole-map native terrain tier plus visible
objects, bounded upload headroom and retained CPU dependencies fits the selected
targets. Treat render targets, simulation snapshots and driver allocations as
additional process costs. Never pin a whole map based on a map-size label alone.

If it does not fit, continue with a bounded persistent regional tier. A proposed
budget increase must be measured independently on this machine and explicitly
reported; residency correctness must not rely on unlimited overage.

## Phase 2: persistent base terrain

Refactor the current prepared/native ground ownership so terrain can outlive
viewport requests without requiring every complete preparation packet to remain
pinned in CPU memory.

1. Separate immutable native terrain surfaces/textures and required picking data
   from optional preparation/replay arrays. Account for every retained reference.
2. Retain terrain independently from road geometry where exact geometry permits.
   A road edit that changes ground cutouts must replace affected terrain surfaces;
   it may still reuse unchanged terrain/flow/shore textures.
3. Build the initial visible region first. Prepare and publish remaining terrain
   progressively on bounded workers/main-thread upload slices.
4. Once the fit gate passes and warming completes, retain the map's terrain tier
   until terrain/content invalidation, map replacement or real budget pressure.
   Pure camera movement and the legacy node heuristic must not evict it.
5. If fit fails or pressure appears, use deterministic page-level LRU retirement,
   protecting visible resources and a recent useful region. Explain retirement
   reasons explicitly in telemetry.

Reuse [GroundPages](../godot/PerspectiveCity.GroundPages.cs), but do not simply
enable the old experiment. Its pages retain source packets and rebuild combined
surfaces when membership changes. The production candidate must have stable
membership after publication, accurately owned materials/textures/picking, and
accounted retiring resources.

Start with the existing 32-cell native page boundary. Test 64-cell pages only if
node/draw-call measurements require them. Keep CPU chunk boundaries unchanged.
Visibility toggles must not dirty a page. Publish a complete replacement before
retiring the old page; preserve exact road/bank materials and UVs.

**Acceptance:** after warming a map that fits, unchanged terrain navigation
causes zero generation, texture uploads, mesh uploads or page rewrites.
Local edits replace only the actual dependency neighborhood. Return visits
outside a fit tier may legitimately miss, but must report the pressure/policy
reason rather than pretending whole-map residency.

## Phase 3: stable building buffers

Keep valid native building slots attached to logical 16-cell owners rather than
recreating their allocations when those owners leave the viewport.

1. Preserve shared cube/material meshes and existing building geometry caches.
   New shared building prototypes are unnecessary for this first implementation.
2. Separate wanted/visible owners from resident owners in
   [ObjectUploads](../godot/PerspectiveCity.ObjectUploads.cs). Preserve valid
   transforms, colors, roof buffers, slots and exact picking payloads.
3. Give native material pages spatial bounds and stable membership; visibility
   controls must not make retained offscreen instances part of every draw.
   Godot MultiMesh visibility is not arbitrary per-instance visibility: verify
   page-level culling and actual GL Compatibility behavior before choosing layout.
4. Reuse free ranges on real removal or pressure eviction. Avoid append/compact/
   grow cycles and whole-pool replay on camera-only changes.
5. Batch genuine dirty ranges once per publication slice/page. Use supported
   per-instance APIs where beneficial, bulk upload where dense changes warrant it.
   Do not assume the rejected AdaptiveUploads heuristic is a measured win.
6. Keep complete footprint ownership: roofs, wings, foundations, entrances and
   yards belong to the same owner even across page/view boundaries.

CPU alternate LOD data and native detailed/overview representations remain
budgeted independently. LOD stays opt-in and is not used to conceal residency
cost. Simulated births, service updates and camera changes must not invalidate
unchanged building artwork; actual redevelopment/removal must.

**Acceptance:** navigation over unchanged resident buildings produces zero
geometry regeneration, slot relocation and native reupload. One building edit
updates its owners/dependencies without rebuilding the city. Retained data must
not produce ghost buildings or offscreen picking.

## Phase 4: camera and publication continuity

Preserve immediate camera movement. Do not reinstate the frozen old-destination
picture as normal gameplay.

- Keep installed overlapping terrain/objects visible throughout staging.
- A warm resident destination must be renderable on the next normal draw without
  waiting for CPU preparation or optional simulation artwork refreshes.
- Maintain the existing compatible footpath snapshot publication rule:
  live wear updates can coalesce behind a complete matching-context mesh;
  camera, terrain, navigation and removal invalidate incompatible work.
- Prioritize camera-visible publication over warming and discretionary compaction.
- For a cold jump, publish available terrain first and remaining objects
  progressively. Do not claim a missing region is resident or count an old frame
  as destination readiness.

If eliminating terrain holes before warming requires more than progressive
publication, evaluate a separate coarse terrain fallback only after measuring
the resident tier. It must be distinctly accounted, cover real map coordinates,
and never participate in detailed picking/placement. No proxy objects or fake
terrain hits. Full-detail completion remains a separate readiness metric.

**Acceptance:** no overlapping content disappearance or clear-to-empty flash.
Warm minimap clicks visibly move the destination on the next completed draw.
Off-map background is legitimate and must not be counted as a loading hole.
Cold startup/jumps remain explicitly identified until sufficient residency exists.

## Phase 5: lifecycle and pressure

Verify map replacement, save/load, undo/redo, zoning, redevelopment, road edits,
water/terrain changes, camera rotation/tilt, LOD transitions and disposal.

Test visible edits during preparation, rapid coalesced jumps, canceled uploads
and pressure while old/new resources overlap. Retire resources only after they
are no longer published. Map epoch changes must prevent cross-map reuse.

Keep native resources on the main thread, with bounded managed workers and
explicit errors. Pressure fallback must reclaim discretionary residency,
preserve visible correctness and log the reason. Do not hide unaccounted
references behind successful-looking byte targets.

## Phase 6: matched validation and adoption

Run separate processes from identical read-only snapshots, with baseline and
candidate using identical rendering quality and non-target cache flags:

1. Medium beginning and genuine 858-resident settlement.
2. Normal and maximum overview; four rotations, tilt and zoom transitions.
3. Local out/back twice, slow boundary drags, and 24 real minimap jumps.
4. Paused and live simulation, all three speeds, daily/weekly updates and combat.
5. Large random map and TOP for regression comparison, not SF as the sole gate.

Reuse [RecentRegionSmoke](../godot/RecentRegionSmoke.cs),
[MinimapTraversalSmoke](../godot/MinimapTraversalSmoke.cs), perspective
performance and continuity/native-buffer checks. Add per-request generation/
upload deltas and resource identity assertions. Inspect captures and consecutive
rendered frames for holes; a screenshot after readiness cannot prove continuity.
Explicitly report boundary-clipped routes.

Run three matched repetitions at the affected medium case. Retain existing
250ms destination readiness, 60ms frame p90, 100ms worst-frame and 50ms
input-handler budgets. Report active and resident node counts separately;
do not silently substitute one for an existing runner's 512-node criterion.
If a measured resident tier legitimately needs a revised node heuristic, make
that a separate documented decision, with draw-call/CPU evidence.

Require zero camera-only generation/uploads within a warmed resident tier,
at least 30% better affected readiness p90, and no consistent greater-than-5%
normal-zoom or beginning-settlement frame-p90 regression. These relative
selection gates do not waive absolute benchmark failures.

Authentic 10K/25K/50K medium-city saves remain a coverage gap. Use them when
available, but do not relabel authored population fixtures as genuine growth.

## Deliverables and stop rules

- One isolated persistent-residency policy, resource accounting and regression
  tests integrated with existing diagnostics.
- Matched measurement report with memory-fit classification, startup/warm/cold
  behavior, upload deltas, captures and passed/failed checks.
- An explicit adopt, retain opt-in or reject decision. Default changes only
  follow a measured medium-city benefit with preserved correctness.

Implement terrain residency first; measure before building the object phase.
Stop if the targeted terrain change solves the practical workload and additional
changes lack evidence. If resource fit or major regressions fail, keep the
candidate diagnostic and report the limitation rather than repeatedly increasing
limits. Near-budget findings alone do not justify endless incremental tuning.

Preserve existing user windows and unrelated changes. Do not rebuild loaded
preview binaries or launch competing graphical benchmarks. Planning this work
does not launch the game, run experiments or change defaults.
