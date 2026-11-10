# TermCity developer guide

TermCity is a **Godot .NET** desktop game with a UI-independent C# gameplay core.
The [README](../README.md) covers installation, controls, rules and player-facing behavior.

## Build and run

### Square world terrain textures

Perspective terrain no longer uses terminal sprite dimensions or the 12x22
terminal cell rasterizer. `WorldTerrainTexture` generates 128x128 RGBA packets
directly for 16x16 logical chunks: eight square texels per cell and broad
four-texel color patches. Terrain shader categories, patch grouping and river
animation use world coordinates; shoreline/flow grids stay independent.
Terminal styling remains intentional for UI only.

Color texture storage across a 480x480 map drops from approximately 309 MiB
to **75 MiB including mipmaps** (about 76% less). Flow/shore textures, meshes,
picking and driver/process costs are additional and explicitly separate.
This change applies to normal perspective rendering as well as opt-in residency;
it does not reduce native panel resolution, MSAA, geometry or building details.
Existing serialized road-coordinate conventions and legacy/overhead compatibility
are not new world-art requirements and are not rewritten as part of this change.
See the [terrain-memory plan](TERRAIN_MEMORY_PLAN.md).

Measured on seed-42 medium full-display overview, using the same
`PersistentScene` policy and 256/128 MiB targets:

| Measure | Previous raster | Square world raster |
| --- | ---: | ---: |
| Measured terrain resource sum | 400,887,632 bytes (868/900 chunks) | 171,185,700 bytes (900/900 chunks) |
| Readiness p90 | 2,396.2ms | 1,210.4ms |
| Second-pass readiness p90 | 1,303.2ms | 736.8ms |
| Ground upload bytes | 370,338,432 | 194,825,312 |
| First-frame p90 | 64.8ms | 71.8ms |

The old resource measurement is a lower bound because not every chunk had been
sampled; it is not a driver allocation total. The square implementation samples
all chunks and is smaller even against that old lower bound. Its roughly 163 MiB
terrain total still exceeds the 128 MiB GPU target before all objects/headroom;
whole-map residency is therefore **not** claimed. The actual route clips to
360 cells on the 480-cell map.

This is one matched before/after run, not a statistical latency guarantee.
The full-overview benchmark still fails strict readiness and node budgets,
and first-frame p90 regressed by about 7ms. Worst readiness was 8,193.4ms.
Native-buffer/terrain continuity and paused-save checks passed; quality settings
were unchanged. Build and 186 focused tests passed, with six retired tests skipped.
Graphical perspective integration passed rotation, zoom, water/shore/road
geometry, picking and model previews; captured hill/river/overview artwork was
inspected. Graphical residency lifecycle also passed native square texture
dimensions, local edits, pressure, map replacement and warm identity checks.
The normal large-map performance smoke passed: cold 298.7ms, frame p90 20.1ms,
actual minimap click-to-render 148.9ms, 279 nodes and preview worst 12.2ms.

The figures above describe the square-raster step. The shared-data/indexed
follow-up below replaces that representation and reports its own results.

#### Shared terrain palette and indexed geometry

Perspective terrain now uses one shared 5x2 RGBA palette, a 16x16 RGBA
category/custom-color texture per chunk, and a 32x32 single-byte variation map.
The five original grass and water tones are generated from the same palette
functions as the square-raster reference. Tests reconstruct every reference
texel exactly from these compact packets. Category and variation textures use
nearest data sampling, not color mipmaps or terminal pixel dimensions; the
existing world-space ripple and shoreline grids remain unchanged.

The medium-map color/category/variation tier occupies **1,843,240 bytes
(about 1.76 MiB)** including the shared palette, versus 75 MiB for the previous
square textures. The roughly 8 MiB of float flow/shore textures remain separate.
Atlas ownership is per renderer, shared by all ground materials and disposed
after them. Chunk ownership and upload/accounting include the variation map;
road-only edits retain unchanged data textures.

Ground, road and bank packets use index buffers when exact attribute sharing
reduces bytes. Position, UV, color and normal are all part of vertex identity:
cliff, material and texture seams are not welded. Prepared picking/road faces
retain their original triangles. Combined pages rebase indices, and native
geometry inspection expands/traverses indices rather than treating vertex arrays
as triangle soup.

The planar 16x16-cell regression has the same 512 triangles with 289 unique
vertices: **73,728 to 20,016 bytes**, approximately 73% smaller. Actual reductions
depend on road cuts, terraces and seams; no universal mesh reduction is claimed.
Native vertex/color/UV/index buffers, packed normals, original triangle order,
seam preservation and page rebase are checked by engine diagnostics.

On the same seed-42 medium full-display overview route, the follow-up measured
roughly **51.6 MB (49.25 MiB) for all 900 chunks' native terrain resources**,
versus 171.2 MB with square textures and unindexed geometry. This includes
terrain/road/bank meshes and chunk data images, not driver allocations or objects.
A separate bounded post-route warm phase confirmed `samples=900/900`,
`resident-terrain=900`, `whole-native-ready=True` and `whole-map-fits` at the
unchanged 128 MiB native target with 32 MiB warming headroom. The shared palette
is counted once in destination-native accounting.

Three overview repeats measured readiness p90 of 128.9/130.8/124.8ms, versus
1,210.4ms in the earlier square-raster trial. Second-pass object payload was
zero; required-owner terrain replacement and object republication counts were
zero. Small 34,832-213,896-byte second-pass terrain payloads were newly warmed
chunks, not regenerated visited owners. The initial follow-up run had zero
second-pass terrain payload as well. Warm completion is reported **after**
timing, not included as a free initial warm-up.

**Remaining failures:** first-frame p90 was 87.1-92.3ms against 60ms, cold worst
readiness was 7.61-7.88 seconds, and the full-overview node check still fails
(up to 1,867 counted nodes). The route remains boundary-clipped. Memory fit and
zero native reuploads are not an all-budgets performance pass; scene/object
publication and rendering costs still require separate work.

Final tests passed 200 focused cases with six retired tests skipped. Graphical
perspective integration passed terrain/river/road/bridge geometry, four rotations,
zoom, picking, previews, stable materials and downstream ripple motion. A sparse
ripple run initially had too few visible samples; the rerun passed the original
threshold without weakening it. Headless/graphical residency diagnostics passed
native palette/data equality, indexed triangle order, hard seams, pressure,
texture-preserving road edits and map replacement. The normal large-map
performance smoke passed: cold 387.1ms, frame p90 18.1ms, minimap click-to-render
158.4ms, 279 nodes and road preview worst 22.0ms. These diagnostic-window
measurements do not substitute for full-display frame budgets.

The [persistent render residency plan](PERSISTENT_RENDER_RESIDENCY_PLAN.md)
describes the residency-first follow-up: measured terrain fit, stable native
building pages and camera/publication continuity. It is not a default-policy
change or a claim that whole-map native residency fits current budgets.

The [growing-medium-city performance plan](MEDIUM_CITY_PERFORMANCE_PLAN.md)
describes the next evidence-driven tuning work. It keeps current defaults
unchanged and gates native upload/overview LOD changes on representative
medium-city measurements.

### Persistent native render residency

Normal perspective rendering now retains unchanged offscreen native terrain until
CPU/native byte pressure requires reclamation, rather than retiring it when it
leaves recent destinations or reaches the diagnostic node-count threshold.
Offscreen terrain stays hidden; this does not draw or proactively upload the
whole map. Object retention and the opt-in persistent-scene renderer are unchanged.
The explicit `Current` cache ablation retains the previous policy; `Tuned` and
normal interactive rendering use pressure-driven terrain retention.

Once an offscreen normal native chunk is installed, optional CPU mesh/texture
replay is released while picking faces and cut metadata remain accounted for.
Nonpersistent draw-page diagnostics still pin the replay their page assembler
requires. CPU replay is trimmed before retiring native destinations. Targets
remain 256 MiB CPU and 128 MiB destination native resources, including objects;
when .NET reports high system memory load, trimming targets temporarily drop to
75% and discretionary warming stops. Visible dependencies remain pinned and
overages stay explicit. Road-only edits retain unchanged visible native textures;
terrain edits and map replacement still invalidate the appropriate resources.

`--persistent-residency-test` also exercises the normal renderer's detached
replay, warm native identity/zero generation and uploads, node-count-only
retention, forced memory reclamation, road/terrain edits and map replacement.
The existing graphical navigation/frame/node budgets are not relaxed.

Pressure-driven validation on the M2: 89 focused tests, build, and graphical
and headless normal/persistent lifecycle checks passed. The graphical perspective
suite without capture also passed, including rotation, zoom, picking, previews,
water activity and road/terrain geometry. The 858-resident fullscreen normal
48-step route preserved the paused save and exact native buffers, with continuous
terrain, zero second-pass terrain uploads and zero required terrain replacements.
Estimated retained CPU/native destination resources were approximately 82/16 MiB
at route end; sampled process peak was approximately 801 MiB. These are different
accounting domains, not interchangeable driver totals.

Performance targets remain unmet: that city's readiness p90 was 683.6ms versus
250ms, first-frame p90 64.6ms versus 60ms, and worst first frame 171.7ms versus
100ms. Active nodes peaked at 504 (512 limit); retained nodes peaked at 702.
Large-random-map frame p90 was 22.8ms, buffered pan worst 1.7ms and cold readiness
364.0ms, but minimap click-to-render was 302.6ms versus 250ms. An initial severe
accounting regression was fixed by caching immutable per-owner metadata sizes
and sampling CPU totals once per retirement pass rather than per candidate.
The capture-enabled perspective check passed geometry, rotation, picking,
road/shore/bridge and normal-zoom water checks, then failed twice at close-zoom
ripple tracking (only two qualifying samples). That separate capture check is
not represented as a pass; its thresholds were not weakened.

The [remaining-stutter investigation](60FPS_PLAN.md#remaining-stutter-investigation-november-8-2026)
separates live frame spikes, worker backlog and paused camera preparation.
Bulk native wall-top submission, unchanged-page bound/count reuse and a
shoreline-only bridge-planning prefilter reduced measured wall costs and daily
bridge planning without changing quality, combat ordering or retention.
Matched 45-second live p95 fell from 38.1ms to approximately 29-30ms. The extended
90-second observation recorded p95 28.3ms, worst 100.3ms and a 6.933s peak backlog;
smoothness and moving-presentation hold targets still fail. Daily growth,
travel repair, analysis and occasional map publication remain measured costs.
The normal fullscreen saved-city route still repeated about 23.8MB of object
uploads and 277MB of path uploads despite zero terrain replacements; readiness
p90 remained 643.6ms versus 250ms. See the investigation for complete validation
results, explicit failures and the isolated `--camera-profile-seconds` diagnostic.

The [November 9 simulation-efficiency follow-up](60FPS_PLAN.md#simulation-efficiency-follow-up-november-9-2026)
removes hot-path closure allocations, shares guard searches, uses integer
navigation-cache keys and pools only temporary travel-repair scratch. Immutable
published route arrays remain separately owned. A fixed-input original-checkpoint
replay retained byte-identical save state while reducing allocation by 49.8%
and runtime by 12.5%. Live 45-second backlog at stop fell from 7.968s to 3.208s;
worst frame fell from 104.1ms to 72.0ms. All changes are normal defaults, with
quality and deterministic ordering preserved. Strict p95 and long-run
moving-presentation hold targets remain unmet; see the report for the measured
limits and validation rather than interpreting these savings as a 60fps claim.

Normal object rendering now adopts the
[stable-slot residency portion](PERSISTENT_RENDER_RESIDENCY_PLAN.md#normal-object-residency-adoption-november-9-2026)
of the persistent candidate, independently of whole-map native terrain warming.
Unchanged instance/roof resources survive camera-only moves, requested spatial
pages control visibility, and byte pressure/local edits/map replacement govern
retirement. CPU owners remain 16 cells; native material pages span 128 world
cells. The budgets remain 256/128 MiB and high-memory-load handling is unchanged.
The fullscreen saved-city return route recorded **zero repeated instance and
roof uploads and zero required-owner republications**, versus 22.1MB repeated
instance uploads and 184 republications before. Path uploads still total about
268MB at that object-only stage; readiness p90 remained approximately 655ms
versus 250ms. Retained-node costs increase: the large-map
performance command measured 169.7ms minimap latency but failed its unchanged
node budget at **513 versus 512**. See the report for active/resident counts,
capture caveats, all validation results and remaining failures.

The [stable path-page follow-up](PERSISTENT_RENDER_RESIDENCY_PLAN.md#stable-path-page-adoption-november-9-2026)
now retains eight-world-cell path owners in 32-world-cell native pages. Complete
owner/dependency margins keep camera-only inputs stable; only changed content
is rebuilt, staged and published. Path packets/native resources are included in
the existing byte budgets and hidden pages are reclaimed under pressure.
The same fullscreen route recorded **zero second-pass path uploads**, total
path transfers of 22.7MB versus 268.1MB, readiness p90 **178.6ms** versus 655.4ms,
and second-pass p90 **54.7ms**. Geometry/reference, native buffers, local edits,
stale workers, pressure, map replacement and graphical artwork checks passed.
Worst readiness, first-frame and node limits still fail; consult the report
rather than treating a passing readiness percentile as an all-budgets pass.

The [residency implementation plan](PERSISTENT_RENDER_RESIDENCY_PLAN.md) is wired
as an isolated whole-map terrain-page/warming candidate beyond the normal
pressure-driven terrain and stable object-slot defaults:

```bash
godot --headless --audio-driver Dummy --path godot -- --persistent-residency-test
godot --audio-driver Dummy --path godot -- --persistent-residency-test
godot --maximized --borderless --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --recent-region-test --benchmark-zoom normal --cache-experiment PersistentScene
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --persistent-residency
```

`PersistentTerrain` isolates ground retention; `PersistentScene` also enables
spatial material-pool residency and culling. The interactive flag is Debug-only,
is separate from smoke/LOD options, and retains 256 MiB CPU / 128 MiB destination
GPU targets, native panel quality, supersampling and MSAA.

Installed terrain, road and bank surfaces belong to stable 64-cell native draw
pages, while preparation, dependency stamps and picking remain 16-cell based.
New owners append independent surfaces to existing meshes; pure visibility
changes do not reupload them. Changes or pressure rebuild only affected pages,
with the complete replacement mounted before old native page resources retire.
The 64-cell draw boundary addresses the node population exposed by earlier
32-cell paging/overview experiments; it does not enlarge generation chunks.

Offscreen complete CPU mesh/texture replay packets can be discarded; compact
native ownership retains the picking faces and terrain-cut metadata still
required by the game. Current-view dependencies stay available. GPU surfaces
provide replay for unchanged page members on a real membership/edit rebuild.
Terrain/flow/shore textures survive road-only edits when their terrain epoch is
unchanged. Vertex positions and UVs are checked exactly, colors retain the
existing packing tolerance, and re-encoded native normals allow 0.001 vector
error for Godot's packed-normal round trip.

Building material pools are spatially paged for all materials, with 64-instance
initial capacity rather than allocating 1,024 slots for every small textured
pool. Valid resident owner slots survive camera changes; free ranges from real
removal are coalesced/reused without relocating other owners. Reused slots are
staged in CPU backing data and published atomically with completed roof/zone
geometry, not made visible partway through upload. Pressure retires offscreen
pages, rather than compacting the whole city during navigation.

Background publication keeps 32 MiB native warming headroom and yields to
visible ground, required objects/paths and the one-second camera-motion
cooldown. CPU replay trimming precedes native retirement, so optional managed
data does not force avoidable native eviction.

`TERMCITY_PERSISTENT_RESIDENCY` reports measured terrain samples/cost, native
readiness, CPU replay/metadata, targets and active versus resident node counts.
If the measured lower bound already exceeds the target, it reports
`regional-budget`; unsampled maps are not assumed to fit. The old 480-resident
node heuristic does not retire the isolated persistent tier, but benchmark
512-node criteria are unchanged and failures remain explicit.

The 80x64 lifecycle fixture completely warms its 20 chunks at approximately
11.3 MiB native ground cost. Graphical and headless tests assert zero terrain
generation/upload/page replacement on warm navigation and rotation/zoom,
native object return identity, direct roof picking, no empty projected native
terrain samples on the next draw, exact buffers, road texture reuse, local
terrain/building edits, steady-state slot reuse, forced pressure reclamation,
map replacement and return to the normal renderer.

Medium and larger maps do **not** generally fit whole-map residency in the
current 128 MiB target. Regional working sets can eliminate second-pass uploads
without eliminating cold preparation, simulation or frame costs. Cold navigation
is still progressive; off-map background is legitimate, and no coarse-terrain
fallback or frozen old-destination picture is silently substituted.

Matched full-display normal-zoom trials at unchanged targets produced these
median results (three separate runs per policy/save):

| Save / policy | Full readiness p90 | Second-pass readiness p90 | First draw p90 | Second-pass terrain / object upload bytes |
| --- | ---: | ---: | ---: | ---: |
| Beginning / Tuned | 89.1ms | 80.0ms | 54.2ms | 68,408,672 / 16,426,128 |
| Beginning / PersistentScene | 150.2ms | 59.0ms | 62.3ms | 0 / 0 |
| 858 residents / Tuned | 703.7ms | 680.5ms | 61.7ms | 74,638,976 / 22,753,760 |
| 858 residents / PersistentScene | 721.9ms | 721.9ms | 79.2ms | 0 / 0 |

The native-reuse requirement passes in these regional return routes. Beginning
warm-return readiness improves about 26%, but first-pass/full-route readiness
and frame latency regress; the populated save still spends substantial time
outside native upload. **The candidate is not promoted to normal defaults.**
It does not meet the plan's 30% affected-readiness and no-regression gates, nor
all existing absolute budgets. Zero uploads must not be represented as zero
camera/simulation/footpath work.

Initial large-random and TOP normal routes also eliminated second-pass terrain
and object upload bytes, with first-pass readiness still over target. The
full-overview medium visible set alone exceeds the 128 MiB target, so whole-map
retention cannot be promised. Such cases retain explicit pinned-visible overage
and performance/node failures instead of discarding visible geometry or lowering
quality. Their detailed measurements are diagnostic limits, not acceptance
passes.

Final regression checks with incremental surface append, compact initial pools
and camera cooldown completed without native-geometry or paused-save failures:

| Diagnostic | Readiness p90 / worst | First draw or frame p90 | Repeated terrain / object bytes |
| --- | ---: | ---: | ---: |
| Large random, normal local route | 106.7 / 222.9ms | first draw 66.8ms | 0 / 0 |
| TOP, normal local route | 428.7 / 630.3ms | first draw 129.3ms | 0 / 0 |
| Medium beginning, full overview local route | 2,395.5 / 8,091.6ms | first draw 69.0ms | 164,305,984 / 9,710,592 |
| 858 residents, live normal minimap traversal | 3,881.0 / 7,996.6ms | frame 133.8ms | not a repeated local route |

The overview is a pressure case, not a fit case: active/resident scene nodes
reached 1,850 and all strict readiness/frame/node failures remain reported.
The live minimap run completed 24 jumps but remains substantially over budget.
These broader-case findings are additional reasons to keep the architecture
opt-in rather than claiming a general scrolling-speed improvement.

Final validation: build succeeded without warnings/errors; 174 focused tests
passed and six retired tests remained skipped. Both independent terrain and
combined-scene lifecycle diagnostics passed, including actual rendered
next-frame continuity and steady-state edit range reuse. The normal large-map
performance smoke also passed: 279 nodes, cold 335.0ms, selection/picking 0.1ms,
buffered pan 1.5ms, frame p90 22.6ms, actual minimap click-to-render 178.1ms and
road preview worst 14.6ms. These default diagnostic-window results do not turn
the opt-in full-display failures into passes.

### Medium growth checkpoint tools

Generate ordinary-rule medium settlement progression headlessly:

```bash
godot --headless --audio-driver Dummy --path godot -- --size medium --seed 42 --generate-medium-checkpoints /absolute/new/checkpoint-directory
```

The generator uses normal founding, construction, gates, connected expansion,
tax controls, service provision, combat, recruitment and daily simulation.
It does not inject households, gold, happiness or service capacity. Generation
is bounded at 7,280 additional days or ten minutes; missing population targets
are recorded explicitly in `manifest.json` and return exit code 2. Existing
output directories with contents are rejected. Intermediate milestones are
saved as well as the requested 10K/25K/50K targets when reached.

Generation can resume from an explicit owned checkpoint using `--load <file>`.
It creates a new output directory and preserves the source snapshot.

Region and minimap diagnostics now accept explicit read-only saved checkpoints;
implicit quick-save loading remains rejected. Isolate zooms in separate
processes to begin every comparison from the same saved simulation state:

```bash
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --recent-region-test --benchmark-zoom overview --cache-experiment Tuned
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --minimap-traversal-test --benchmark-zoom normal --benchmark-speed Medium --cache-experiment Tuned
```

`--benchmark-speed` accepts Slow/Medium/Fast and sets the starting live-half
speed; ordinary wave-driven speed changes are retained and reported.
Diagnostics report p95, actual/clipped local travel, required-owner resource
replacements, whole-buffer/per-instance writes, pool growth/compaction,
capacity/live/unused slots, and separate roof/path upload byte estimates.
Legacy instance-upload byte counters retain their original meaning.

See the [medium-city execution report](MEDIUM_CITY_BENCHMARK_RESULTS.md) for
actual population coverage, baseline measurements, paired upload/LOD trials,
garrison search findings and explicit failures. Native macOS fullscreen
transitions could roll back the requested size, so these diagnostics now use
a full-display borderless maximized window and validate its physical dimensions
throughout sampling. Native panel sizing, supersampling and MSAA are unchanged;
normal gameplay window behavior is untouched.

### TOP minimum-spec baseline and recent-region experiments

Use `--size TOP --seed 42` for the current top-end **player-scale** baseline.
SF remains a legacy stress fixture, not the supported top-end settlement target.
The minimum-spec reference workstation is an Apple M2 with **8 GiB unified memory**.
TOP is a persistent named 640x640 scenario with exactly 100,000 residents,
2,084 tenement households, ordinary services producing happiness 69.1 (Content),
160 intact reinforced wall components, four gates, mounted defenses and 64
garrison defenders. Its mature state is authored, not a simulated growth
playthrough. Terrain outside its cleared urban/defense collar remains seeded.
Population, contentment, enclosure, model scale, wall health and garrisons are
tested through ordinary save/load; F5 saves an interactive instance normally.

```bash
dotnet build TermCity.slnx
godot --audio-driver Dummy --path godot -- --size TOP --seed 42 --recent-region-test --cache-experiment Tuned
godot --audio-driver Dummy --path godot -- --size TOP --seed 42 --recent-region-test --cache-experiment LargerBudgets
godot --audio-driver Dummy --path godot -- --size TOP --seed 42 --recent-region-test --cache-experiment RecentRegionLod
godot --audio-driver Dummy --path godot -- --size TOP --seed 42 --recent-region-test --cache-experiment RecentRegionPagedLodLarge
godot --audio-driver Dummy --path godot -- --size TOP --seed 42 --minimap-traversal-test --cache-experiment RecentRegionPagedLodLarge
```

The recent-region diagnostic pauses simulation and performs 48 camera steps at
each of normal and maximum overview zoom: two screens out and back, twice.
It measures first completed draw, full readiness, second-pass uploads/builds,
active versus retained nodes, accounted cache bytes and sampled process memory.
Native buffers, overlapping terrain publication and paused saves are checked.
The viewport is genuinely fullscreen: 2940x1846 window, 2530x1748 native map
panel, 5060x3496 render target and 8x MSAA. At overview, the requested 772-cell
travel exceeds the 640-cell map and **clips at the boundary**; this is not a
successful unrestricted two-screen overview traversal.

Initial matched TOP sweeps, before the final dry collar and page corrections:

| Policy | CPU/GPU MiB | Normal readiness p90 | Overview readiness p90 | Second-pass terrain uploads, normal/overview | Second-pass object builds, overview |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tuned | 256/128 | 378ms | 12,188ms | 210 / 896 | 750 |
| LargerBudgets, unchanged policy | 512/384 | 286ms | 11,182ms | 210 / 896 | 427 |
| Protected recent region | 512/384 | 210ms | 11,562ms | 58 / 744 | 533 |
| Protected region + overview LOD | 512/384 | 264ms | 3,605ms | 58 / 668 | 0 |

**Larger allowances alone did not eliminate reloads.** Policy retirement and
overview geometry/terrain working-set size matter independently of byte caps.
Terrain upload counts include buffered/halo chunks; they are not counts of
unique visible cells. LOD removes fine house/workplace/tree details only at
maximum overview; services, foundations, picking and construction previews
remain detailed.

All new policies remain **diagnostic-only**. Normal gameplay retains 256 MiB CPU,
128 MiB destination GPU, the 1/2-chunk halo, immediate camera presentation and
the existing renderer. `RecentRegion` and `RecentRegionLod` use 512/384 MiB.
`RecentRegionPagedLod` additionally uses stable 32-cell terrain draw pages;
`RecentRegionPagedLodLarge` uses 64-cell pages and 768/512 MiB. Both preserve
16-cell preparation, invalidation and picking. Page membership survives pure
visibility changes. Stable-page eviction groups offscreen owners and replaces
the affected page before testing pressure again, avoiding over-eviction caused
by counting retired owners' still-published page buffers. Validated pages are
still disposed normally; validation never changes their resource lifecycle.

Recent-region bounds include the terrain exit halo and use **unclipped projected
viewport dimensions** for the three-screen allowance. Reaching a map edge must
not shrink the allowance and reset the return route. Byte pressure can still
evict a route, and farther navigation/zoom changes reset its protection.
The diagnostic disables the 480-resident-node heuristic, not the unchanged
512-active-node acceptance criterion.

Cache targets exclude render targets, driver allocations, simulation snapshots,
temporary preparation and GC headroom. On this unified-memory machine, do not
add managed heap, driver-video and working-set counters as though they were
independent physical allocations. Sampled working set alone also understates
memory pressure when macOS compresses or pages memory. Larger targets are not
a total-process memory guarantee or evidence that an 8 GiB device can afford
unlimited residency.

After stable-page eviction and revisit-bound corrections, the final
768/512 MiB, 64-cell-page TOP route measured:

| Zoom | Readiness p90 / worst | First draw p90 / worst | Repeat terrain uploads | Repeat object builds | Maximum active / resident nodes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Normal | 194.5 / 553.0ms | 81.8 / 172.3ms | 0 | 12 | 111 / 148 |
| Maximum overview | 191.3 / 5,072.4ms | 118.4 / 205.5ms | 0 | 11 | 461 / 603 |

This eliminates second-pass terrain uploads in this route, not all object
regeneration or cold loading. Accounted native/CPU byte diagnostics distinguish
the protected route from other warmed data. At the route's final return,
protected normal-region packets accounted for **136 MiB CPU / 97 MiB native**;
the boundary-clipped overview region accounted for **480 MiB CPU / 487 MiB
native**. Total accounted CPU caches were 196/552 MiB respectively, and
destination-native allocations were 97/487 MiB. These are endpoint costs, not
allocation peaks or an extrapolation from SF. Sampled process peaks were
900 MiB normal and 593 MiB overview, but are **not** total allocation peaks.
The overview endpoint's managed heap was approximately 2.8 GiB and the driver
reported approximately 1.2 GiB video allocations, demonstrating why nominal
cache targets and a compressed working-set sample are insufficient for
minimum-spec capacity planning.
Exact native terrain/road/bank buffers, continuous overlapping terrain, paused
save preservation, directly picked overview roofs/overhangs, detailed previews
and detailed/overview/detailed cache returns passed. The transition fixture uses
four building orientations and validates the selected page configuration.

**Failed performance checks:** worst readiness must remain below 250ms, first
draw p90 below 60ms and worst below 100ms. These runs fail all three thresholds;
the 512-active-node threshold passes despite 603 retained overview nodes.
The large cold-overview tail is substantial, not a near-limit finding. Neither
the threshold nor supersampling/MSAA has been relaxed. Earlier corrected-page
live minimap traversal also stalled at overview jump 19 with pending footpaths,
after normal 24-jump readiness p90 of 2,546ms and worst frame of 3,021ms.
The final unclipped-route configuration completed all 24 normal minimap jumps
with response p90 92.1ms, readiness p90 2,413.8ms, worst readiness 3,862.1ms
and worst frame 3,003.4ms. Live simulation advanced 0.98 days. Its overview
run stalled at jump 15, again with pending footpaths and no pending ground or
object work; no overview 24-jump aggregate is claimed.
Therefore this configuration is not yet a certified minimum-spec gameplay
default. Further work should address live footpath publication/simulation
pressure and large destination object-buffer uploads rather than merely
increasing the retention ceiling again.

Pre-publication-fix verification: build succeeded with no warnings/errors; 168 focused tests
passed and six retired glyph/painted-road tests remained explicitly skipped.
Graphical perspective integration passed camera rotation/zoom/picking,
construction previews, original terrain/road geometry and submenu models.
The existing large random-map performance smoke passed: 279 nodes, cold
318.2ms, selection 0.1ms, buffered pan 1.3ms, picking 0.1ms, frame p90 18.0ms,
actual minimap click-to-render 178.7ms and road preview worst 12.6ms.
These small diagnostic-window results do not certify fullscreen TOP.
The separate large-map headless integration smoke failed its Oil cauldron
menu assertion: an expected Effect row was absent. That menu behavior is
outside the TOP/cache changes and is not represented as passing.

#### Live footpath publication follow-up

The pending-footpath stall was reproduced deterministically: queue a newer
wear update before a completed worker publishes, and continue doing so while
its buffers upload. Revision-only cancellation discarded every superseded
worker/upload, so a live city could never publish a complete destination.

Footpath work now carries an immutable publication context: map identity,
navigation and terrain versions, viewport bounds, terrain/depth treatment and
road-rendering mode. A completed snapshot may publish while newer **compatible
wear updates** are coalesced behind it. Map/camera, terrain, navigation, style,
road-mode changes and removal of all paths still cancel obsolete work.
There remains only one active worker/upload and one latest queued request.
Paused inspection awaits the latest revision and still compares exact geometry;
live destination readiness awaits a complete matching-context publication, not
quiescence of optional wear refreshes. No simulation ticks are paused or skipped
to attain readiness. Diagnostics distinguish required work from background
refresh and count completed footpath publications.

```bash
godot --headless --audio-driver Dummy --path godot -- --size small --seed 42 --footpath-art-smoke-test
godot --headless --audio-driver Dummy --path godot -- --size TOP --seed 42 --footpath-art-smoke-test
```

The first command verifies exact dirt vertices, normals, UVs, road joins,
doorway picking and cache returns, plus coalesced publication and invalidation
regressions. The same new publication regression **failed with the old
revision-only rule and passed with the context rule**. TOP mode exercises
24 destination changes at each zoom, actual journey-generated dirt, daily
simulation updates and exact native terrain/object/page buffers at a
2530x1748 logical map panel. It is a functional check, **not** a rendered-frame
or latency benchmark; its headless results must not replace the fullscreen
minimap thresholds above.

The isolated TOP functional run completed all 48 changes: normal zoom published
6,588 dirt triangles with 26 completed meshes and simulation day 2.96; overview
published 476,115 triangles with 52 completed meshes and simulation day 11.00.
Native buffer and publication/invalidation checks passed at both zooms.

The publication fix was first built and tested in an isolated session workspace,
leaving the existing TOP preview and its loaded binaries unchanged. The final
workspace Debug build and 169 focused tests pass (six retired tests skipped).
Graphical footpath integration also passes the coalescing/invalidation regression,
exact cached/fresh dirt geometry, rotation/zoom, doorway picking, road joins,
material/shadow checks and model previews. Final TOP captures at both zooms were
inspected.

After restarting the owned preview, the **post-fix fullscreen minimap run completed
all 24 jumps at both zooms**, including live simulation. Unlike the earlier
stalled runs, it produced complete aggregates:

| Zoom | Response p90 | Readiness p90 / worst | Frame p90 / worst | Live-half simulation days |
| --- | ---: | ---: | ---: | ---: |
| Normal | 92.5ms | 2,536.5 / 3,934.3ms | 59.5 / 3,033.3ms | 0.923 |
| Maximum overview | 350.7ms | 12,537.8 / 15,350.2ms | 75.8 / 2,383.8ms | 20.173 |

The stall is fixed; the **performance budget is not met**. Distant minimap jumps
are not interchangeable with the zero-repeat-terrain local revisit route.
Normal frame p90 passes 60ms, but readiness and worst frames fail; overview also
fails frame p90. The original traversal's 512-total-scene-node check remains
unchanged and also fails on retained overview destinations (up to 625 nodes).
Do not substitute the local route's separate active-node criterion to turn this
into a pass. The overview sampled process peak was approximately 2.36 GiB.
No resolution, MSAA, latency threshold or simulation rate was reduced.

Detailed/overview/detailed cache returns, four building orientations, roof
picking, full-detail previews and paused-save preservation passed after the
completed traversal. New cache/LOD/page policies remain diagnostic-only, with
normal 256/128 MiB defaults unchanged. Further performance work should focus on
destination native object-buffer upload volume and cold full-overview preparation;
larger byte limits alone are not the recommendation.

### Camera responsiveness diagnostics

Use the live camera benchmark to distinguish camera input cost from simulation,
HUD, terrain, building and footpath work:

```bash
dotnet build TermCity.slnx
godot --path godot -- --size medium --seed 42 --debug-siege --camera-performance-test --capture /tmp/camera-siege.png
godot --path godot -- --size large --seed 42 --camera-performance-test
godot --path godot -- --size SF --seed 42 --camera-performance-test
```

The same command supports small/medium/large random maps, SF/LA/SD/CHI/STL,
and custom dimensions. It drives actual input and checks left/middle dragging,
keyboard/edge scrolling, trackpad pan/pinch, wheel/key zoom, rotation/tilt,
minimap click/drag, placement preservation, off-map rejection, narrow terminal
controls and overhead navigation. Live runs include all three time speeds,
daily/weekly updates and combat in siege fixtures. A separate wide drag crosses
the buffered scene boundary.

`TERMCITY_CAMERA_SAMPLE` reports p50/p90/p95/p99, worst whole-frame and
input-to-render time, input-handler cost, simulation/HUD costs, frames above
50/100ms, static rebuilds and path-only rebuilds. Whole-frame measurements include
the interval between completed draws; input-to-render starts at the input event.
The benchmark uses normal rendered frames, not forced extra draws during sampling.
Its watchdog fails explicitly if drawing stops. This diagnostic mode keeps its
scripted clock/input active when its window loses focus; normal gameplay retains
the existing focus suspension behavior.

Budgets remain 60ms frame p90, 100ms worst frame/input-to-render, and 50ms input
handling. Existing perspective, minimap and road-preview budgets are unchanged.
Native panel sizing, 2x supersampling and 8x MSAA are asserted, not reduced to
improve benchmark results.

Camera drags update the planar camera immediately but defer repeated scene
reconciliation to drawing. Perspective mode prepares viewport dimensions without
sampling unused terminal glyph cells. Camera changes refresh zoom/minimap controls
without rebuilding the full sidebar; simulation notifications coalesce into one
HUD refresh per frame. Service coverage is calculated and memoized on demand from
immutable dispatch data, and pollution patches are reused without changing their
original clamping/order semantics.

Navigation, path artwork and analysis have separate invalidation signals. Daily
service-age/clock changes do not invalidate unchanged map artwork; active analysis
overlays still refresh. Persistent journeys, road targets and directed terrain/road
footsteps are cached with their relevant invalidation conditions. Compacted-path
cost updates first try sparse exact relaxation, then fall back to exact shortest-
path-tree repair for a larger influence region. These optimizations do not add a
travel radius cutoff, skip simulation steps or pause during camera interaction.

Path layouts retain exact visible polygons with a two-cell neighborhood.
House-yard decisions still run in global owner order, but use lazily built local
path layouts. Static building/terrain buffers remain retained when only paths
change. Footpath relief, normal/UV generation and indexed vertex buffers run on a
bounded worker using captured managed geometry; Godot mesh/resource creation and
swapping remain on the main thread. Only one job runs per view and newer requests
coalesce. The previous complete path mesh remains visible until replacement.
Geometry inspection/captures await pending work. Roof and dirt uploads use bulk
arrays while preserving authored triangles, normals, UVs and picking.

Map objects use retained 16-cell ownership chunks. A building's anchor owns its
complete footprint, roof, overhangs, entrance and yard; viewport requests also
include owners whose geometry crosses into view. Local dependency stamps include
terrain, roads, features, zones, footprints, rotations and yards. Global version
changes trigger validation, not unconditional regeneration of unchanged chunks.
CPU retention is byte-budgeted, not limited to 256 chunks. See
[whole-map CPU retention](#whole-map-cpu-retention) for accounting, warming,
eviction and the current measurements. Buffered neighbors are CPU-prefetched
after visible requests, without delaying visible-scene readiness or uploading
offscreen objects; the direction of travel biases neighboring requests.

Artwork emission is cooperative on the main thread; at most two immutable
payloads concurrently prepare picking groups, encoded instances and roof/dirt vertex arrays on
workers. Scene/resource operations remain on the main thread. Upload/preparation
uses a frame-aware 4-20ms host budget targeting a 50ms frame, rather than a single
whole-window object rebuild. Complete chunks publish their draw and picking data
together; an old complete chunk remains active until its replacement is ready.
Unpresented occupied plots reject inspection/placement with a loading message,
while perspective dragging remains available.

Colored instances use bounded 64-cell material pages; textured groups remain
shared, and roofs, entrance dirt and zones use 32-cell multi-surface pages.
Large writes upload an affected material page, not just its newly reserved slots.
`ObjectUploadBytes` includes those full-page instance transfers, capacity changes,
compaction and retirement; it excludes roof/zone meshes and textures. Logical
instance counts alone are not GPU bandwidth.
Whole roof pages remain pinned together to keep draw/picking publication atomic.
Obsolete native pools, canceled zone resources and offscreen supporting terrain
chunks are retired with their caches. Existing rendering/performance budgets are
unchanged.

Cold camera terrain/road chunks prepare managed vertex packets against an
isolated snapshot. Visible chunks are prioritized; buffered neighbors publish
under a 4ms mounting budget instead of holding viewport readiness behind the
entire lookahead window. GPU meshes and terrain textures stay on the main
thread. Ground epochs track terrain, road geometry and active overlays, not
unrelated building/service updates. A pending zoom remains dirty until its
replacement window is actually installed.

Path workers emit bounded indexed pages (16,384 vertices / 49,152 indices).
Uploads stage invisibly under a 4ms budget, retaining the old complete dirt mesh
until all replacement pages are ready. Stale pages are discarded. Path captures
use the visible viewport's padded ground data, including prepared terrain
packets, rather than waiting for offscreen lookahead. A camera moving beyond
the retained path capture refreshes paths without rebuilding the city.

### Whole-map CPU retention

The renderer progressively prepares the whole map **when affordable**, not the
whole GPU scene. Object ownership progresses through preparing, CPU-ready,
GPU-staging and published states. Retirement returns a published object to
CPU-ready; eviction removes its retained packet. CPU-ready objects contain
managed instances, roofs, zone vertices, picking groups, highlights and entrances,
not `SurfaceTool`, mesh or node resources. Foundations, yards and door approaches
sample retained ground triangles without uploading offscreen terrain. Complete
draw and picking data still publish atomically; queued objects remain unpickable.

The default retained-CPU target is **256 MiB**, with **32 MiB of preparation
headroom** before discretionary warming stops. Accounting includes object and
terrain/road packet arrays, list capacity, picking and zone geometry, building
replay data, estimated shared terrain/road/ownership dependencies, and CPU instance
upload backing arrays. Managed terrain/flow/shore texture packets are included
in this budget too. Shared ground picking arrays are counted once. Complete
streamed objects already own their encoded geometry, so streaming does not also
retain duplicate per-building replay copies. Allocation headers, dictionaries
and derived geometry are estimates, not an allocator-level measurement.

This target was selected on an 8 GiB Apple Silicon workstation using collected
managed-heap and process measurements as well as geometry accounting, **not**
encoded-instance bytes alone. A fully warm 480x480 random map accounted for
approximately 147 MiB before texture-packet caching and had a 338 MiB collected managed heap; dense scenarios
reached approximately 409-496 MiB collected heaps while retaining useful subsets.
The remaining heap includes simulation state, managed Godot wrappers and shared
runtime data. The original 192 MiB accounting target left substantial system headroom
rather than assuming every dense map should fit. It is not a process-memory cap.
CPU and GPU allocations compete for unified memory on Apple Silicon.

The CPU target was temporarily raised to 256 MiB for a retention benchmark, leaving
the 96 MiB destination-GPU target, node ceiling and 32 MiB preparation reserve
unchanged. That experiment stopped discretionary warming around 224 MiB of accounted
CPU data, or earlier under system memory pressure. Measurements below retain
their original budgets; they are not retrospective 256 MiB results.
The seed-42 graphical Large cache rerun passed with a reported budget of
268,435,456 bytes: 343/1,600 ownership chunks retained, approximately 224 MiB
accounted CPU data, 365 MiB collected managed heap and 498 MiB sampled peak
process working set. Warm minimap jumps measured 38-54ms. The artificial
one-byte pressure check preserved pinned data and passed without a background
preparation/eviction loop. This is a historical experiment, not a controlled
192-versus-256 MiB speed comparison.

The current CPU/destination-GPU targets are 256/128 MiB. The CPU target was
raised again after the independent 192/128 MiB native-retention experiment.
The 32 MiB preparation reserve and 480-node soft retention ceiling are unchanged.
CPU warming stops around 224 MiB of accounted data, or earlier under memory pressure. Historical results
below retain their original budgets; raising the GPU target does not change them.
The preceding seed-42 graphical Large minimap rerun passed with CPU/GPU targets of
201,326,592/134,217,728 bytes. Warm home/away returns measured 18/26ms with no
visible-destination regeneration or reupload. Frozen-frame, keyboard/picking,
latest-only publication, running-simulation and artificial one-byte GPU-pressure
checks passed. The two destinations occupied approximately 48 MiB of native
cache, below both the old and new GPU targets; this run verifies correctness,
not a demonstrated speedup from the larger allowance.

The subsequent 256/128 MiB graphical Large minimap rerun passed with the expected
268,435,456/134,217,728-byte targets. Warm home/away returns measured 23/21ms,
with no visible-destination regeneration or reupload; frozen presentation,
running-simulation, keyboard/picking and GPU-pressure checks passed. This confirms
the combined targets work, not that they improve latency for destinations that
already fit within smaller budgets.

Visible objects, whole published roof pages, current ground and active preparation
dependencies are pinned. Optional building/ground-builder replay caches are
released first under pressure; unpinned far data is evicted by LRU before nearby
data. If pins exceed the target, they remain correct, warming stops, and
`pinned-overage` reports the excess. Background scheduling also stops at
85% of the runtime's high-memory-load threshold. A visited set prevents repeatedly
preparing and evicting background regions within one artwork version.

The deduplicated object queue prioritizes visible work, directional buffered
neighbors and retained camera destinations before a progressive ownership-grid
sweep. Camera changes abandon unfinished distant artwork between cooperative
steps. Building/yard ownership is indexed once per relevant version, rather than
scanning every building for every background chunk. The existing two-encoding-job
limit and frame-aware 4-20ms host budget remain. Background object preparation
needs no simulation snapshot. Cold camera ground uses consistent snapshot batches
of at most 32 packets; cancellation is checked between packets so a distant batch
can yield to a new visible request. Native operations remain on the main thread.

Ground packets survive camera-window changes. Terrain/road/style/overlay epochs
validate their local dependency hashes; unchanged packets survive unrelated
changes. Object hashes retain local building, rotation, yard, terrain, road and
path dependencies. Simulation analysis clocks do not invalidate static objects
when no analysis overlay is active. New-map work is isolated; map swaps reject
old work, mode changes cancel ground preparation, and shutdown drains workers and
releases retained ownership. Pending object encoding is revalidated before
publication after a mode change.

GPU residency remains separate: only required object chunks upload, with existing
bounded material pages. The **128 MiB destination-GPU target** covers retained
instance capacities, roof/zone vertices, ground meshes and native terrain/flow/
shore textures (including estimated terrain mipmaps). Unpinned destinations retire
by LRU, with priority for the last populated view; that priority is not a permanent
pin. Whole 64-cell instance pages retire together, with one full-page rewrite
per affected pool. A 480-node soft retention ceiling leaves headroom under the
existing 512-node benchmark; required data remains correct if pins exceed limits.
CPU packets remain reusable after native retirement. The current/staging view,
whole required material pages and ground dependencies stay pinned; overage is
explicit. `driver-video` reports Godot's available renderer telemetry,
which also includes render targets and other resources; it is zero under the
headless dummy renderer. `upload-backing`, estimated GPU bytes, in-flight snapshot
allocations/reserves, collected heap and process working set are separate values;
do not sum overlapping metrics into a supposed exact total.

Reproduce the cache diagnostics with a Debug build:

```bash
dotnet build TermCity.slnx
godot --path godot -- --size 80x48 --seed 42 --whole-map-cache-test --capture /tmp/cache.png
godot --path godot -- --size small --seed 42 --whole-map-cache-test
godot --path godot -- --size medium --seed 42 --whole-map-cache-test
godot --path godot -- --size large --seed 42 --whole-map-cache-test
godot --path godot -- --size SF --seed 42 --whole-map-cache-test
```

Use the same command with LA/SD/CHI/STL. `--headless` is useful for managed
geometry/lifecycle checks, but explicitly skips native buffer readback and cannot
validate GPU latency, frame budgets or captures. The cache smoke standardizes its
window to 960x800, exercises actual minimap input and awaits visible uploads,
picking and a rendered frame. It checks memory-pressure suspension/no churn,
exact retained/fresh ground packets, every-region reuse on the 80x48 fixture,
local invalidation, overlays, unchanged serialized state, load/map replacement,
mode transitions and disposal. Existing performance smoke thresholds are unchanged.

Initial November 2026 seed-42 **headless CPU diagnostic** measurements (MiB rounded,
before terrain texture packets and recent-destination GPU retention):

| Fixture | Retained ownership chunks | Accounted CPU | Collected managed heap | Cold jump worst | Warm jump worst |
|---|---:|---:|---:|---:|---:|
| 80x48 | 15/15 | 8 | 27 | 45ms | 11ms |
| Small 320x320 | 400/400 | 88 | 189 | 334ms | 126ms |
| Medium 480x480 | 900/900 | 147 | 338 | 385ms | 106ms |
| Large 640x640 | 944/1600 | 160 | 379 | 255ms | 88ms |
| SF 640x384 | 130/960 | 160 | 409 | 530ms | 149ms |
| LA 640x384 | 129/960 | 161 | 409 | 891ms | 205ms |
| SD 640x384 | 198/960 | 161 | 437 | 1,127ms | 238ms |
| CHI 640x384 | 78/960 | 185 | 492 | 1,146ms | 439ms |
| STL 640x384 | 48/960 | 184 | 496 | 1,646ms | 546ms |

These are not graphical budget passes or peak-memory claims. They demonstrate
full retention on affordable maps and pressure-limited subsets on dense maps.
Preparation after the cold jumps took approximately 11.3s for Small (283 new
chunks), 28.1s for Medium (774), and 28.8s for Large (840 before suspension).
Process working-set samples at warm readiness ranged from approximately 239 MiB
to 648 MiB and are affected by macOS memory compression. Transient/driver peaks
can be higher. A deliberately one-byte target preserves pinned data, explicitly
reports overage, and performs no repeated background builds.

Graphical checks validated exact native instance transforms/colors and cached
picking on the 80x48 fixture, with 15/15 regions reused without regeneration.
Cold/warm minimap readiness was 71/32ms; every-region revisits were below 44ms.
Godot reported approximately 91 MiB total video memory, including render targets.
The graphical capture retained native-resolution supersampling/antialiasing and
the terminal UI; no whole-map native allocation was introduced.

The final graphical memory runs retained 949/1600 Large chunks and 131/960 SF
chunks, both at approximately 160 MiB accounted CPU. Frame-sampled process peaks
were approximately 333 MiB (80x48), 608 MiB (Large) and 811 MiB (SF); sampled total
driver-video peaks were approximately 91/145/158 MiB respectively. macOS returned
zero for `Process.PeakWorkingSet64`, so that OS peak metric is unavailable; the
explicit `sampled-process-peak` and `sampled-driver-video-peak` values are
frame-sampled maxima after cache tracking starts, not guaranteed instantaneous
process-wide peaks. Sampling runs only in cache diagnostics, not normal gameplay
or the live camera benchmark.

Graphical SF performance smoke measured cold readiness at 1,479ms against
1,500ms (near limit), 373 nodes against 512, frame p90 54.7ms against 60ms, and
selection/pan/picking below 5ms. It **still failed** minimap readiness at 585ms
against 250ms and road preview at 198ms against 100ms. A same-workstation original
binary baseline recorded 1,780ms cold, 701ms minimap and 181ms preview. Large random
passed cold readiness at 461ms, frame p90 at 19.8ms and preview at 16ms, but failed
minimap readiness at 294ms against 250ms. Historical SF 1,446/491/120ms results
below remain historical measurements, not substituted current results.

The graphical live-camera matrix passed all strict budgets on Medium, Large and
the debug-siege fixture. Small/custom recorded isolated 172/152ms frames against
100ms; SF/LA/SD also recorded strict worst-frame failures. The first CHI/STL runs
recorded Fast p90 of 69.4/84.4ms against 60ms. A matched STL baseline/rerun measured
65.1ms with the original binary versus 57.7ms with the cache implementation;
the rerun still failed on one 116.1ms Medium-speed frame against 100ms. These
failures are not hidden by the passing camera mechanics, reduced rendering
quality, changed fixtures or relaxed thresholds.

Targeted core/presentation, generation, snapshot, road-layout, authored-scenario
and persistence runs passed 270 tests, with six intentionally retired rendering
tests skipped. Graphical footpath/220-entrance catalogue, narrow interface and
garrison regressions passed. A broader suite attempt exposed unrelated existing
compact-artwork, gate, water-generation and path-contact failures; it is not
reported as an all-passing full suite. The compact-model smoke searches the same
nearest eligible fixture in distance order before testing expensive whole-plot
eligibility, avoiding an exhaustive rectangular scan without changing the chosen
fixture or its assertions. The full graphical perspective geometry/interaction
smoke passed on rerun, including rotations, zoom, picking, foundations, zones,
bridges, paths, gardens and compact models. Its first graphical attempt failed
the existing ripple sample minimum at close zoom (two moving tracks versus three
required); the rerun passed with the original assertions unchanged.

### Sustained minimap traversal tuning

**Normal gameplay now presents the requested camera immediately.** The full-map
frozen framebuffer introduced below is no longer the default: sustained
navigation showed it hid visible feedback until all object uploads finished.
The optional frozen mode remains available for regression/ablation checks, but
does not copy a framebuffer every frame in normal play. This deliberately
restores progressive presentation: cold destinations can briefly show incomplete
terrain/objects. Picking still uses published geometry, and occupied plots whose
objects are not presented retain the existing loading rejection. This is a
latency/UX trade-off, not a claim that every transition remains visually atomic.

The default GPU policy retains the current region, the previous minimap
destination and the last populated region preferentially, rather than keeping
every traversed native region until the byte/node limits force eviction. Those
regions are still evictable when budgets require it. Broad residency caused native
data to pin substantial CPU payloads and repeated dense-region regeneration.
Offscreen native resources outside these useful regions retire together with
their existing material pages. Resource reconciliation only rescans when requests/
uploads change or a budget is exceeded. Discretionary instance-pool compaction is
deferred while navigation is cooling down; it no longer runs once per retired
page on the destination's critical path.

A **one-second minimap navigation cooldown** defers directional/offscreen artwork
and whole-map warming between repeated clicks. Required destination work still
runs immediately, and the existing two-worker and host budgets remain. The
configured CPU/GPU targets remain **256/128 MiB**: lowering them to 192/96 MiB
did not improve sustained SF traversal. Background warming remains enabled after
navigation settles; it is not a settlement/growth/simulation pause.

The new graphical `--minimap-traversal-test` uses the same 24 targets for each
experiment: 12 paused jumps, 12 running jumps, a return to the populated center
every fourth jump, 100ms between destinations, and one second of initial settling.
It measures first changed map-picture samples separately from complete visible
readiness, input handling, object regeneration, actual instance upload bytes,
retirement host work and process memory. Native buffers are validated after each
destination. The running phase explicitly asserts simulation progress and keeps
its diagnostic clock active on focus loss. A rendered-frame watchdog fails
explicitly instead of leaving a stalled comparison running.

```bash
dotnet build TermCity.slnx
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment Current
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment NoWarming
```

`Current` reproduces the pre-tuning all-on cache/frozen configuration; `Tuned`
matches normal gameplay's immediate presentation, useful-region retention and
navigation cooldown. Other explicit diagnostic ablations are `NoResidency`,
`NoFrameCopy`, `NoTexturePackets`, `SmallBudgets`, `SmallPages`, `TerrainFirst`,
`UsefulResidency` and `LazyTextures`. They are accepted only with the traversal
test, not as silently ignored live-game settings. `--minimap-jump-test` explicitly
selects the optional all-on frozen mode for its byte-identical-frame checks.

The saved November 5 pre-minimap-change source was built in an isolated temporary
copy with the identical traversal driver and diagnostic focus policy. Its renderer
was not altered. **That snapshot already contained whole-map CPU preparation**:
it is a pre-minimap/GPU-retention baseline, not an exact reconstruction of the
renderer before every cache change. The unrelated dirty branch was preserved;
no speculative whole-worktree rollback was used.

Matched seed-42 graphical traversal results (milliseconds):

| Fixture / configuration | First-response p90 | Complete-ready p90 | Ready worst | Frame p90 | Frame worst |
|---|---:|---:|---:|---:|---:|
| Large saved pre-minimap | 11.6 | 131.0 | 171.5 | 86.9 | 125.7 |
| Large all-on pre-tuning | 132.8 | 134.5 | 187.8 | 22.0 | 79.7 |
| Large tuned default | 13.9 | 131.1 | 184.0 | 37.3 | 74.1 |
| SF saved pre-minimap | 30.4 | 380.8 | 964.2 | 65.4 | 129.9 |
| SF all-on pre-tuning | 753.2 | 755.3 | 1,066.1 | 42.1 | 146.1 |
| SF tuned default | 33.6 | 377.3 | 1,019.9 | 51.5 | 156.1 |

The tuned SF running-half average was 163ms versus 427ms all-on; emitted instance
transfers were approximately 1.29 GB versus 1.82 GB (and 1.92 GB in the saved
baseline). Large tuned traversal rebuilt 105 ownership chunks versus 206 all-on
and 276 saved. Its running-half average was 45ms versus 70ms saved, though the
broader all-on cache still gave a faster 30ms warm half. This is not a universal
win for every warm-return pattern.

Initial isolated SF ablations showed:

| Experiment | Ready p90 | Finding |
|---|---:|---|
| All-on | 793ms | Sustained regression reproduced |
| No whole-map warming | 714ms | Disabling warming alone was insufficient |
| No broad GPU residency | 378ms | Strong evidence against retaining all visited native regions |
| No frozen framebuffer | 854ms | First response improved to 62ms, but readiness still regressed |
| No texture-packet caching | 419ms | Helped retention, but worst frames reached 174ms |
| Smaller 192/96 MiB budgets | 729ms | Not adopted |
| Smaller instance pages | 818ms | Not adopted; full-page traffic did not improve |
| Terrain-first frozen publication | 749ms | Response improved, but less than immediate camera presentation |
| Useful-region residency only | 386ms | Adopted with navigation cooldown and immediate presentation |

These measurements include graphical readback of 25 fixed pixel probes until
first response; that diagnostic overhead is shared by comparisons but is not
normal gameplay. The first response is the first sampled changed picture, not
proof that all geometry is present. Running simulations advance for wall-clock
duration, so faster traversal naturally advances fewer days; generation and
simulation rules are not skipped or modified.

**Passed:** Large sustained traversal passed all existing thresholds; the final
focused core run passed 164 tests with six retired checks skipped. Full graphical
perspective, exact ground/pixels and map/load/disposal checks, optional frozen-mode
checks, the 220-entrance/path catalogue and narrow-interface regressions passed.
The tuned Large standard performance smoke passed at 314ms cold readiness,
174ms minimap readiness, 16ms road preview, 21.6ms frame p90 and 271 nodes.

**Remaining failures:** SF sustained readiness still exceeds 250ms on cold
destinations, including an approximately 1-second worst jump. Repeated tuned runs
recorded readiness p90 of 366-398ms and first-response p90 of 32-36ms; some runs
had isolated 116-156ms frames against 100ms. The standard SF smoke recorded
401.5ms minimap readiness and 140.6ms preview against 250/100ms, while cold
readiness (583ms), p90 (32.1ms) and nodes (373) passed. Its first live-camera attempt
hit the rendered-frame watchdog; the rerun passed, with Fast p90 28.8ms.
Large live camera budgets passed. Thresholds, native resolution, supersampling,
antialiasing and fixtures were not weakened.

The changes remove the measured sustained *regression* and restore prompt feedback;
they do not solve every pre-existing cold dense-city latency or memory cost.

### Diagnostic road-off traversal

`RoadsOff` is an isolated traversal experiment, **not a normal gameplay setting**.
It uses the same tuned camera, warming, native-retention policy and 256/128 MiB
CPU/GPU targets as `Tuned`, but constructs an empty rendering-only road layout.
Ground therefore has no road corridors, road cut/fill geometry, road shoulders
or road surfaces, and the perspective road-bridge renderer has no segments.
Intact terrain/water replaces those surfaces rather than displaying empty cuts.
Buildings, trees, terrain textures, paths and pedestrian footbridges still render.
The minimap and simulation continue to contain the actual roads.

This measures more than hiding road meshes: road layout tessellation and its
ground-surface work are omitted. It does **not** disable simulation road access,
journey planning, traffic or shared logical road extraction. Building foundations,
door approaches and paths sample the diagnostic terrain/empty rendering layout,
so it is a visual diagnostic rather than a playable alternative representation.
No terrain cells, roads, road types, generation rules or saved data are changed.

```bash
dotnet build TermCity.slnx
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment RoadsOff
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment RoadsOff --capture /tmp/roads-off.png
godot --path godot -- --size medium --cache-experiment RoadsOff --minimap-traversal-test --debug-siege
```

Each experiment runs the same 24 jumps, including the running-simulation half.
Debug builds also accept `--debug-siege` with traversal experiments, using the
requested random-map size and existing debug-settlement generation. Other starter
modes and populated scenarios cannot be combined with debug siege. The traversal
window exits when the diagnostic completes; strict budget failures still produce
a nonzero exit rather than a launch/configuration error.
The medium debug-siege road-off command passed its 24-jump graphical run:
ready p90 111ms, worst readiness 138ms and frame p90 40ms, with logical roads
preserved and normal road rendering restored afterward.
`RoadsOff` asserts that prepared/uploaded road surfaces, road-border triangles
and road bridge segments are absent, verifies unchanged serialized state during
paused traversal, and checks logical road counts after simulation advances.
It then restores `Tuned`, invalidates diagnostic work, rebuilds normal road
geometry and verifies restoration leaves serialized state unchanged. Rendering
epochs include the road diagnostic flag so terrain/native caches cannot leak
between modes. Options are rejected outside `--minimap-traversal-test`.
`ground-worker-total` reports accumulated completed camera-ground worker time;
it is not the complete cost of all simulation or background work.

Matched seed-42 graphical results, two sequential runs per configuration:

| Fixture | Road mode | First-response p90 | Complete-ready p90 | Ready worst | Ground-worker total |
|---|---|---:|---:|---:|---:|
| Large | Tuned / on | 11-14ms | 123-129ms | 193-196ms | 540-595ms |
| Large | RoadsOff | 16ms | 98-99ms | 119-124ms | 398-406ms |
| SF | Tuned / on | 32-40ms | 362-377ms | 1,002-1,036ms | 1,743-1,858ms |
| SF | RoadsOff | 31-34ms | 262-270ms | 529-551ms | 411-413ms |

Road-off substantially reduces dense-city ground preparation, but first visual
response is similar and SF complete readiness **still exceeds 250ms** on four
cold destinations. Native instance transfer totals remain approximately
1.39-1.41 GB in road-off SF traversal, so object upload work remains substantial.
One SF road-off run recorded a 123ms frame against 100ms; the other stayed below
79ms. Both Large road-off runs passed all traversal budgets. A final road-off/
restore rerun passed correctness on both maps, with Large readiness p90 108ms and
SF 264ms. The captured SF scene visibly contains intact grass where roads normally
render; terrain/building quality and the terminal UI are unchanged.

The first trial ran while an existing user preview remained open and recorded an
isolated 14.3-second frame; it is not included in the matched sequential ranges
above and is not hidden as a passing result. A later capture attempt hit the
existing rendered-frame watchdog; its unchanged retry passed road-off and
restoration checks. These interruptions do not establish a road-renderer defect,
and no performance thresholds were relaxed.

Validation: isolated Debug build passed without warnings, and 150 focused
experiment/persistence/snapshot/road-layout tests passed with six retired tests
skipped. Existing user preview binaries were not rebuilt underneath the running
preview. **Normal gameplay still renders roads.** This experiment supports
investigating a terrain-first pass, not permanently removing roads or claiming
they explain all minimap latency.

### Preparation profiling and full-zoom-out traversal

The traversal diagnostic now runs **24 jumps at normal zoom and another 24 at
`GameSession.MinZoom` (-2, the full overview)**. Each zoom gets its own metrics,
12 paused jumps and 12 running jumps; the normal-zoom performance exception is
collected so it does not prevent the full-overview pass from running. The same
250ms readiness, 60ms frame p90, 100ms worst-frame, 50ms input and 512-node
thresholds apply at both zooms. Native resolution, 2x supersampling and 8x MSAA
are asserted at each zoom. The requested zoom is asserted after every jump.
Road-off experiments reapply the diagnostic before the overview pass after the
normal pass has verified restoration, so both zooms measure the selected mode.

`TERMCITY_PREPARATION_PROFILE` separates ground geometry, its road-border subset,
terrain pixels, cooperative object emission, its building subset, worker encoding,
native object uploads, native ground bytes and snapshot-host work. Subset timings
overlap their parent categories; do not sum them as disjoint costs. Worker timings
can overlap main-thread timings. `terrain-ready-p90` records the first completed
draw with every required ground chunk mounted, independently of complete
object/path readiness. Captures add `.overview.png` for full zoom-out.

Implemented preparation changes:

- Exact per-cell road-border bands, segment border bands and road-hit queries
  are cached within the road-layout lifetime. Completed current-epoch workers
  return immutable managed dependencies to the main-thread builder, so later
  bounded workers can reuse them. Cache sizes are bounded and their retained
  arrays/dictionaries are included in CPU estimates; changed terrain/roads
  replace the owning builders.
- A consistent ground snapshot is reused across sequential batches for the
  **same pending viewport and epoch**, rather than taking another complete
  simulation snapshot for each 32-packet batch. It is released after readiness,
  navigation replacement, invalidation or shutdown. Snapshot allocations remain
  separately reported as in-flight work, not hidden as free cache memory.
  An initial longer-lived snapshot-retention attempt caused extra CPU eviction
  and was rejected; this bounded viewport lifetime avoids that problem.
- Prepared terrain/road-ground packets can mount progressively under the 4ms
  host budget while path preparation is still pending. Requested viewport bounds
  remain stable during staging unless the actual camera/map changes. This lets
  correct terrain appear without waiting for buildings or path meshes. It is
  **not** an approximate terrain-only replacement: ground and road cuts still
  come from their common exact mesh preparation.
- Object preparation skips already captured/pinned ground dependencies instead
  of repeatedly checking and yielding through the same neighboring chunks for
  every tree, foundation and entrance.
- Worker instance encoding writes color groups directly into the final packed
  buffer, eliminating per-color temporary float arrays and the subsequent copy.
  The shared writer validates destination capacity and retains original transform,
  color and cached-span ordering.

Fresh/cached validation now creates a genuinely fresh road-surface builder rather
than allowing the comparison to reuse the same newly cached border dependencies.
Exact terrain/road triangles, picking, pixels and native buffers remain required.

Commands are unchanged; a single traversal run now includes both zooms:

```bash
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment Tuned --capture /tmp/preparation.png
```

Measured seed-42 graphical results, with all 24 jumps completed at each zoom:

| Fixture / zoom | Baseline readiness p90 | Updated readiness p90 | Updated terrain-ready p90 | Updated worst readiness |
|---|---:|---:|---:|---:|
| Large normal | 120ms | 115ms | 79ms | 200ms |
| Large full overview (-2) | 548ms | 543ms | 428ms | 575ms |
| SF normal | 349ms | 373ms | 186ms | 1,043ms |
| SF full overview (-2) | 8,688ms | 7,945ms | 1,742ms | 84,604ms |

These are modest preparation optimizations, **not an all-passing latency result**.
SF normal geometry/road-border/pixel preparation totals fell from approximately
1,715/418/257ms to 1,593/386/233ms across the jumps, and instance encoding from
72ms to 60ms. Complete normal readiness did not improve in that paired run.
Large's latest profile captured 7 ground snapshots at normal zoom and 21 at
overview; batches still contain at most 32 packets each.

Full zoom-out exposed previously unmeasured severe failures. Large overview
still exceeds 250ms readiness and recorded a 175ms worst frame against 100ms.
SF overview takes seconds for many destinations, with an approximately **85-second
running-jump outlier in both the baseline and updated renderer**, and a roughly
1.15-second worst frame in the updated run. Both SF overview frame p90 values
were about 78ms against 60ms. CPU regeneration, approximately 28-30 GB of cumulative
instance transfers and concurrent simulation updates dominate that workload;
the renderer is not being declared responsive at full overview.

The original 20-second diagnostic watchdog stopped before all overview jumps
could be measured. Its stall timeout is now 120 seconds so these pathological
latencies are reported rather than omitted. **Only the watchdog duration changed:
none of the performance budgets were relaxed.** The scripted running half
continues simulation throughout slow jumps, so it advances many more days than
normal zoom and is explicitly reported with `running-days`. The paired SF
overview runs advanced approximately 111-119 days; their mutable cities are
therefore not identical final-state snapshots.

The profile establishes that additional full-overview work must focus on
publication bandwidth, retained object reuse and simulation/invalidation churn,
not merely terrain pixel generation. No rendered quality, road gameplay,
building footprints, entrance details or picking fidelity was reduced.

Final functional validation passed the zero-warning Debug build, 164 focused
cache/road/entrance/persistence tests (six retired tests skipped), exact fresh
ground/pixel and disposal checks, the 220-entrance artwork catalogue, narrow
interface checks and full graphical perspective smoke on rerun. The first
perspective capture failed the existing close-zoom ripple minimum (two moving
tracks versus three required); the unchanged retry passed. Standard Large
performance smoke passed at 321ms cold readiness, 176ms minimap readiness,
21.5ms frame p90 and 271 nodes.

Separate small/medium road-off dual-zoom attempts hit the existing graphical
rendered-frame watchdog before completing; they are not reported as passes.
The completed Large/SF normal-road runs above each measured all 24 jumps at both
zooms. The watchdog interruptions and full-overview performance failures remain
explicit rather than being hidden by a successful build or normal-zoom run.

### Terrain-priority retention and fullscreen benchmarks

Useful terrain now has retention reservations **within**, not in addition to,
the existing 256 MiB CPU and 128 MiB destination-GPU budgets. The default shares
are 128 MiB CPU and 64 MiB GPU, capped at half of each configured total so
deliberately small-budget tests remain meaningful. Current terrain is pinned as
before; terrain from the last three distinct minimap views receives retention
priority. Unused reservation space is not preallocated and can be used by other
objects. Discretionary building/object data is evicted ahead of reserved useful
terrain; if pinned scene data leaves insufficient room, reserved offscreen terrain
is still evictable. Total budget and render-node limits are never overridden by
these reservations.

CPU terrain/flow/shore pixel packets are now also retained independently of
road-shaped ground geometry. A road edit can regenerate its actual cuts, grades
and mesh without discarding unchanged base pixels. Pixel ownership is invalidated
on map replacement or terrain-version changes, including downstream/coastal changes.
Packets already referenced by prepared ground are not counted twice; independent
packets are included in the same CPU budget and evicted under pressure. Disabling
perspective and renderer disposal release this independent ownership.

The terrain mesh itself remains the common exact terrain/road-cut surface rather
than an overlapping immutable flat mesh underneath roads. This intentionally
preserves foundation heights, bridges, cuts and actual-triangle picking. Road edits
still invalidate affected mesh dependencies, but not unchanged terrain pixels.
No whole-world GPU allocation or permanent offscreen terrain pin was introduced.

**Traversal now runs in actual fullscreen mode**, not a fixed 960x800 window.
`TERMCITY_TRAVERSAL_DISPLAY` reports the fullscreen window, physical screen, map
panel, native render dimensions, supersampled dimensions and 8x MSAA. Both the
normal and maximum zoom-out passes use that real viewport and each execute 24
jumps. Historical small-window numbers above remain historical, not fullscreen
claims. OS-reserved/notch areas may make the fullscreen window smaller than the
reported screen; the renderer uses the actual map panel, not a fabricated size.

```bash
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment Tuned
godot --path godot -- --size large --seed 42 --minimap-traversal-test --cache-experiment NoTerrainPriority
```

`NoTerrainPriority` isolates these reservations and independent base-pixel
retention while keeping tuned navigation, road rendering, quality and the same
total budgets. A functional cache smoke verifies unchanged pixel identity through
a road edit, replacement of the road geometry, pressure eviction under an
artificial one-byte budget and complete release on disposal.

The matched Large fullscreen run on this workstation reported a 2940x1846
window, 2530x1748 native map panel and **5060x3496** 3D render target, with 8x MSAA:

| Zoom / metric | Terrain priority off | Terrain priority on |
|---|---:|---:|
| Normal terrain-ready p90 | 287ms | 293ms |
| Normal complete-ready p90 | 440ms | 470ms |
| Normal worst readiness | 663ms | 534ms |
| Full zoom-out terrain-ready p90 | 1,841ms | 2,010ms |
| Full zoom-out complete-ready p90 | 7,909ms | 9,336ms |
| Full zoom-out worst readiness | 10,316ms | 12,424ms |

**This comparison does not establish a speedup.** Reservations improve retention
policy and pixel reuse across edits, but are too small to keep this fullscreen
overview's entire working set hot. Both runs failed the original 250ms readiness
budget; normal frame p90 was approximately 87-91ms against 60ms, and full overview
exceeded the 512-node budget with roughly 729-1,174 nodes. The running halves
advanced different amounts of simulation time (about 51 versus 69 days at overview),
so later mutable scenes are not identical. No claims of performance improvement
are inferred from a successful build or from the earlier small-window checks.

Functional validation passed the isolated zero-warning build, 164 focused tests
with six retired checks skipped, exact ground/pixel/lifecycle checks including
road-edit base-pixel reuse, the 220-entrance/path catalogue and narrow-interface
checks. The fullscreen capture was inspected. The existing user's running preview
was preserved, and its binaries were not rebuilt underneath it.

### Optional atomic minimap presentation and retained destinations

#### Diagnostic maximum-zoom overview LOD

`OverviewLod` is an opt-in renderer experiment, not a change to the gameplay default.
It uses simplified building/tree geometry **only at `GameSession.MinZoom` (-2)**.
All other discrete zoom levels, construction previews and non-zoned service/
landmark buildings remain detailed. The existing discrete zoom steps provide
stable transition thresholds; no fractional zoom jitter selects the LOD.

```bash
godot --path godot -- --size SF --seed 42 --halo-scroll-test --cache-experiment OverviewLod
godot --path godot -- --size SF --seed 42 --minimap-traversal-test --cache-experiment OverviewLod
godot --path godot -- --size SF --seed 42 --overview-lod
```

`--overview-lod` enables the experimental representation in a normal interactive
Debug game, including loaded footprints. Zoom fully out to the 0.25x view to
activate it; normal/intermediate views and construction ghosts stay detailed.
It is rejected in Release builds and when combined with auto-exiting diagnostics,
which select the experiment explicitly instead.

Houses directly emit their existing horizontal block/wing/courtyard layout,
body and solid stepped roof courses instead of generating facade trim and
individual roof tiles. Footprints, centers, model scale, quarter-turn orientation,
roof height, roof-course overhangs, chimneys and foundation support are preserved.
Commercial/industrial buildings directly emit compact body/gable forms with
recognizable stall canopies, forge chimneys and mill wheels. Small counter
contents, facade trim and other subpixel details are omitted. Entrance approaches
and steps retain the original geometry so paths still meet the actual thresholds.

Trees directly return their existing coarse canopy tiers and trunk, bypassing
fine cluster subdivision, small roots and branches. Shape variation, authored
height, palette and existing clustered foliage material remain; the renderer does
not replace trees with an untextured billboard or reduce terrain quality. Terrain,
shorelines, road cuts, road surfaces, bridges, paths and the terminal UI are unchanged.

The representation flag is part of object dependency stamps and validation.
Detailed and overview CPU packets are retained separately when affordable, within
the same existing budget; inactive representations are evicted under pressure.
Only the current representation publishes native drawing/picking data, and stale
queued representations are rejected. All visible coarse bodies/roof courses
participate in picking; gable triangles keep the existing exact roof-ray tests.
The building's actual frontmost drawn representation, not an unrelated proxy,
is used for selection and highlights.

`TERMCITY_OVERVIEW_LOD_TRANSITIONS_OK` uses an isolated four-rotation house fixture
to verify fewer generated parts, detailed intermediate zoom, exact detailed-cache
reuse on return, and unchanged serialized state. `TERMCITY_OVERVIEW_PREVIEW_OK`
captures a full-detail construction preview while overview is active. Native
buffers, complete visible box/roof-overhang picking and directly visible roof
hits are validated. Core tests cover all tree shapes and unchanged detailed
outputs. Halo diagnostics capture their save baseline after ordinary renderer/
yard initialization; measured scrolling and LOD transitions cannot alter it.

Fullscreen SF local-scrolling runs completed 48 moves at normal zoom and 48 at
full overview. Full-detail overview readiness p90 was approximately **4.24s**;
overview LOD runs measured approximately **1.44-1.82s**, with worst readiness
about **4.4-4.7s** versus about **10.2s** full-detail. First-draw p90 fell from
roughly 223ms to 102-107ms. This is a material improvement for dense object
preparation/upload work, but it is **not a passing overview result**: readiness,
frame and node budgets remain exceeded. Counted render nodes remain about 1,440,
because this LOD reduces instance geometry, not the terrain's node count.

The Large random-map 24-jump LOD run completed all destinations at both zooms.
Normal readiness p90 was 431ms and full overview 4.08s on the actual fullscreen
panel; terrain-ready p90 at overview was 1.66s. Those budgets still failed.
The SF 24-jump run completed normal zoom but hit its explicit 120-second stall
timeout at overview jump 13 (`paths`/`objects` pending). Its earlier attempt hit
the rendered-frame watchdog. These attempts are not claimed as completed or
passing SF traversal benchmarks, nor compared to paused scrolling as equivalent
workloads. The running diagnostics advance simulation throughout slow jumps,
so later cities differ from the paused fixture.

The expanded LOD checks also exposed missing visible ground after a completed
camera batch: mounting could wait indefinitely when required packets were absent
and no worker remained. Buffered mounting now requests missing visible ground
again when snapshotting is safe, instead of waiting for another camera change.
This preserves current geometry and foreground readiness rather than returning
a success-shaped fallback.

The zero-warning build and 157 focused tree/option/map/road/entrance/persistence
tests passed (six retired checks skipped), as did four-rotation cache/preview/
save checks and the unchanged 220-entrance/path catalogue. Graphical captures
were inspected. The gameplay default keeps LOD disabled pending more complete fullscreen
live-simulation validation; the explicit Debug flag is available for manual trials.

The final profile run emitted 44,606 overview instances and approximately
386 MB of cumulative instance transfers on the paused SF scroll route.
Building emission measured 140ms, encoding 22ms and native object-upload host
work 246ms; these are separate workload categories, not a guaranteed per-frame
speedup. All original ground and path geometry still contributes to loading.
Normal views in the same run reported `overview-lod=False`.
The final build passed with zero warnings and 158 focused tests (six retired
skips), including interactive-flag isolation. A separate small-map diagnostic
passed both zooms and the four-rotation/detailed-preview/cache/save checks.
The full-detail profile retry hit the graphical watchdog before finishing
overview; it is not claimed as an additional matched timing result.

#### Experimental 32-cell terrain GPU draw pages

`TerrainDrawPages` keeps the existing **16x16 preparation, invalidation and
picking chunks**, but groups up to four neighboring chunks into a **32x32 native
draw page**. It is diagnostic-only; normal gameplay still uses the existing
chunk renderer because measurements did not demonstrate a latency win.

```bash
godot --path godot -- --size large --seed 42 --halo-scroll-test --cache-experiment Tuned
godot --path godot -- --size large --seed 42 --halo-scroll-test --cache-experiment TerrainDrawPages
godot --path godot -- --size SF --seed 42 --halo-scroll-test --cache-experiment TerrainDrawPages
```

Pages contain one multi-surface terrain mesh, retaining each chunk's exact
texture coordinates and material, plus combined road and bank meshes where
present. Terrain still needs a material surface per chunk; this reduces native
render-node count but does **not** turn four independently textured surfaces
into a single terrain draw call. Roads/banks with shared materials are concatenated
without changing their triangles, normals, UVs, colors or per-chunk picking data.
This prototype uses no atlas, lower-resolution textures, simplified geometry
or changed simulation/generation chunks.

Changed page memberships stage and publish together under a 4ms host budget.
Old complete pages remain installed until replacement. Chunk readiness includes
page publication, not merely prepared metadata. Offscreen visibility changes
rebuild only their affected page; terrain continuity samples only truly published
chunks. Retained page sources remain owned/accounted during replacement so CPU
eviction cannot invalidate an installed native page. Native ground metadata does
not also upload duplicate per-chunk meshes in paged mode. Actual full-page mesh
rewrites are included in ground-upload counters, including replacement writes.

Native validation checks exact vertex/UV ordering and colors with the existing
GPU quantization tolerance. Normals are compared against the standard native
upload of the same prepared mesh, including Godot's packing of zero normals.
Ground, road and bank surfaces are checked; simulation save contents are asserted
unchanged, and each scrolling run restores the default renderer afterward.

Fullscreen results on the same seed-42 route, normal/full zoom-out:

| Large metric | Existing chunks | 32-cell draw pages |
|---|---:|---:|
| Normal maximum render nodes | 199 | 86 |
| Normal readiness p90 | 35ms | 35ms |
| Normal repeat ground uploads | 39 | 40 |
| Normal transferred ground bytes | 25 MB | 39 MB |
| Overview maximum render nodes | 1,174 | 385 |
| Overview readiness p90 | 117ms | 124ms |
| Overview worst readiness | 2.09s | 2.51s |
| Overview repeat ground uploads | 112 | 112 |
| Overview transferred ground bytes | 79 MB | 111 MB |

On SF, the earlier matched unpaged halo route recorded 298/1,441 normal/overview
render nodes and approximately 55ms/4.2s readiness p90. Paged SF recorded
138/547 nodes and approximately 93ms/4.9s readiness p90; repeat ground uploads
remained 40/48. Its overview still exceeded the 512-node budget, and first-draw
p90 was approximately 253ms against 60ms. These results are not a claim that
reducing nodes solved the reload problem.

The page renderer materially reduces counted render nodes, but CPU/GPU memory
pressure still causes the same repeated chunk registrations and reloads.
Visibility changes now also rewrite mixed pages, increasing bandwidth. Large
overview passed the node count but still failed readiness/frame budgets; SF failed
both. **Do not adopt this experiment as the default on the strength of node counts
alone.** Avoiding visibility-driven page rewrites and/or texture-atlas batching
would require another measured implementation, not simply larger preparation
chunks.

The zero-warning build and 143 focused option/map/road/persistence tests passed
(six retired checks skipped). Fullscreen continuity, native ground/road/bank
buffer checks and capture inspection passed. The earlier paged prototype exposed
publication/source-lifetime issues that were fixed before these measurements;
the performance budgets were not relaxed.

#### Terrain halo and boundary hysteresis

The halo size can be compared without changing normal gameplay:

```bash
godot --path godot -- --size large --seed 42 --halo-scroll-test --cache-experiment Tuned
godot --path godot -- --size large --seed 42 --halo-scroll-test --cache-experiment LargerHalo
```

`Tuned` uses the default one-chunk entry / two-chunk exit boundaries;
`LargerHalo` changes only those boundaries to two/three chunks. Both use the
same CPU/GPU budgets, terrain reservations, cooldown, native pages and node limits.
The graphical test runs fullscreen at normal and maximum zoom-out. At each zoom
it moves the camera four cells per step, travels 48 cells out and back, then
repeats the same route (48 moves total). Simulation is paused to isolate scrolling
and prevent edits from confounding retirement counts. It reports ready/frame
latency, new ground uploads, second-pass uploads, upload bytes, memory/node/policy/
invalidation retirements, maximum nodes/halo membership and sampled process memory.
Every step validates unchanged overlapping terrain and native object buffers.
It is a local-scrolling experiment, not the separate 24-destination minimap test.
Its frame samples are click/scroll-to-first-draw intervals, not exhaustive
telemetry for every frame drawn while awaiting complete geometry. Complete
readiness includes the wait for uploads and paths; the 250ms limit is checked
against the worst step, not only the p90.

The seed-42 experiment used the actual fullscreen 2530x1748 map panel and
5060x3496 supersampled target, with 8x MSAA. Matched Large and SF runs, repeated
in reverse configuration order, showed:

| Map / zoom | Default 1/2 halo repeat uploads | Wider 2/3 halo repeat uploads | Finding |
|---|---:|---:|---|
| Large normal | 40 | 33-35 | Modest reduction in policy-driven reloads |
| SF normal | 40 | 32 | Some nearby reloads avoided |
| Large full overview | 112 | 112 | No reduction; memory pressure dominates |
| SF full overview | 48 | 48 | No reduction; memory pressure dominates |

The wider halo did not eliminate edge reloads. Large normal readiness p90 was
33-46ms with the default and 32-34ms with the wider halo; the reverse-order run
showed nearly equal latency, so a general speedup is not established. Normal
native node counts increased from 199 to 286 on Large and 298 to 365 on SF.
Large ground upload-byte totals did not decrease despite fewer repeat chunk
uploads, because the wider halo also mounts additional offscreen terrain.
SF normal readiness p90 was approximately 55-56ms with the default and 55-59ms
with the larger halo, providing no consistent latency win despite the repeat
upload reduction. The tests are paused, so policy/memory eviction—not simulation
invalidation—explains the counted churn. Process-memory samples vary across runs
and are not proof that one halo always uses less total application memory.

At full overview, both halo sizes recorded identical retirement counts and
ground upload bytes: 252 memory retirements / about 79 MB on Large, and 120
memory retirements / about 57 MB on SF. Neither recorded policy retirements
or invalidations in this paused overview route. Increasing the halo does not
make the visible working set fit the existing memory limits. Maximum counted
nodes were 1,174 on Large and 1,441 on SF, exceeding the 512-node benchmark.
Large overview readiness had approximately 1.7-1.9s worst cases; SF reached
roughly 10s. Those performance failures remain explicit, not treated as successful
benchmarks because the geometry/continuity checks passed.

The normal 1/2 halo remains the gameplay default. `LargerHalo` is diagnostic-only
and the experiment leaves CPU/GPU targets, node limits and visual quality unchanged.
For this workload, the wider halo is a modest normal-zoom reload reduction at
the cost of additional residency, not a solution to full-overview churn.
The experiment build passed without warnings and 105 focused option/map/road
tests passed, with six retired checks skipped. Both maps completed both zooms
in both configuration orders. Normal-zoom budget checks passed; full-overview
checks failed the unchanged readiness/frame/node limits. Continuity/native
buffer checks and graphical capture inspection passed. A separate capture run
was not included in the paired ranges because capture overhead and cache
settling changed its normal-zoom totals.

Terrain retention now includes a one-chunk (16-cell) halo around the visible
camera chunk window. Chunks enter this useful set within one chunk of the view;
previous members stay useful until they are more than two chunks away. This
different enter/exit boundary prevents policy-driven retire/reload churn during
small back-and-forth scrolls. Halo bounds are clipped to the map, reset on
map/mode/disposal changes, and refreshed for actual camera chunk-window changes.

The halo is a retention preference, **not a new permanent pin**. The existing
CPU/GPU budgets, 480-node soft ceiling and 512-node benchmark remain unchanged.
Memory or node pressure can still retire offscreen halo data. Hidden halo nodes
are excluded from mounted-visible readiness and do not expose offscreen picking.
Already prepared neighboring packets can acquire native residency after visible
readiness and the navigation cooldown, under a 2ms mounting budget and only when
there is memory/node headroom. This does not trigger whole-world preparation or
block the foreground camera waiting for the halo.

`TERMCITY_TERRAIN_HALO_OK` exercises eight actual camera moves across a 16-cell
boundary and back, comparing retained native node identities. The existing
terrain continuity checks still assert that unchanged overlapping visible chunks
remain visible. Cache diagnostics report `terrain-halo-chunks` and native terrain
retirement counts for memory, nodes, policy and invalidation; this distinguishes
cache retirement/reupload from ordinary per-frame drawing.
Validation passed the zero-warning build, 164 focused tests (six retired skips),
graphical/headless exact ground/pixel/road-edit/disposal checks, and eight real
out-and-back crossings with native identities retained. The fullscreen camera
attempt passed the halo identity check for 100 resident chunks but stopped making
rendered-frame progress in the following continuity phase; it was stopped and
is not claimed as a completed fullscreen performance pass. The separate Large
live-camera rerun passed all controls/budgets, with 36 retained halo chunks and
768 shared-chunk continuity observations. Its capture was inspected. Existing
fullscreen performance limitations remain unchanged.

#### Ground visibility continuity during navigation

Pending terrain mounting no longer hides every retained ground node and clears
the entire mounted set when the camera's requested bounds change. Visibility is
reconciled against the new chunk window: unchanged overlapping nodes remain
visible and mounted, already-resident destination nodes are revealed immediately,
and only out-of-window nodes are hidden. New chunks continue to mount under the
existing budget. Rebuilding the installed scene uses the same reconciliation,
preserving progressive presentation rather than reintroducing the frozen map.

`TERMCITY_TERRAIN_CONTINUITY_OK` validates subpixel scrolling and buffered-window
crossings over 48 rendered frames, asserting identity and visibility of every
unchanged overlapping chunk. The check must inspect at least one shared chunk;
it cannot pass vacuously. Fullscreen traversal also checks retained terrain after
each drawn frame during minimap clicks, at both normal and full zoom-out. Real
terrain/road/overlay changes remain allowed to replace stale geometry.

Fullscreen normal-zoom traversal completed 24 clicks without withdrawing shared
terrain, and the 48-frame continuity check passed at both zooms. The broader
full-overview traversal hit the existing rendered-frame watchdog on two attempts
after those continuity checks; it is not recorded as a full-overview performance
pass. Normal fullscreen readiness/frame budgets remain over limit (approximately
495ms readiness p90 and 86ms frame p90 in the rerun). The separate Large live
camera smoke passed, including 768 shared-chunk continuity observations,
dragging, rotation, zoom, picking and placement preservation. The flashing fix
does not claim to solve those existing fullscreen loading/driver limitations.
The final zero-warning build, 164 focused tests (six retired skips), exact
ground/pixel/road-edit/disposal checks and complete headless perspective geometry
smoke passed. The full graphical perspective smoke failed the existing close-zoom
ripple sample minimum on both attempts (two moving tracks versus three required);
it is not claimed passing. Terrain continuity and the live graphical camera
checks passed independently of that water-animation sampler.

The following records the earlier all-on default and its short-return tests.
After sustained traversal tuning above, frozen presentation is diagnostic-only.

In the optional frozen mode, minimap input records a requested camera destination while presentation keeps
the last complete picture. A dedicated 2D `SubViewport` copies the completed 3D
render on the GPU at the same supersampled resolution; it freezes during loading.
There is no image readback in the input handler and no low-resolution fallback.
After terrain, objects, paths and picking are ready, the destination renders and
publishes at `FramePostDraw`. The extra retained framebuffer is accounted
separately from destination residency (approximately 6 MiB at the smoke panel
size), not hidden in the 128 MiB destination budget.

Map pointer inspection, troop picking, placement and cursor/confirmation action
keys are blocked while that old picture is displayed. Edge scrolling is suspended.
Minimap navigation, camera rotation/zoom, overlays, pause and menus remain
available. The minimap retains the presented white viewport outline and shows
the requested destination in yellow; its projection uses the presented heading
until publication. A normal terminal status message explains loading, without a
new popup. Rapid clicks replace the pending destination; only the latest completed
camera state publishes. Mode changes, map replacement and disposal cancel the
retained presentation.

Native ownership chunks remain resident across camera-window changes until
budget/node pressure requires retirement. Statistics/picking enumerate the
requested ownership region, not every offscreen retained object. Ground has a
separate retained node tree: camera reconciliation hides/reuses unchanged mesh
nodes instead of deleting and rebuilding every ground node. Local terrain/road/
overlay hashes retire only stale native ground. CPU-ready destinations upload
only missing native resources; recent resident destinations reuse instance slots,
roof pages, ground meshes and textures.

Ground workers also capture managed terrain RGBA, river-flow float and shore float
pixel packets with their geometry. Pixel generation occurs against the same
bounded snapshot as the ground meshes. Native image/texture creation and mesh
uploads remain on the main thread. Dependency hashing includes downstream flow,
ripple-track inputs and coastal distances, not just nearby terrain IDs.
Exact fresh/cached pixel tests cover all three buffers.

Visible/staging ground packets stay pinned even when outside the previously
installed camera window. This prevents overview-to-close navigation under CPU
pressure from repeatedly preparing and evicting the destination before it can
publish. Diagnostic camera waits yield frames and fail explicitly after 20 seconds
rather than spinning indefinitely. While a minimap transition is pending,
foreground object requests precede buffered neighbors and discretionary warming.
Canceled consistent CPU work may still be retained, but cannot publish offscreen
draw/picking data for an obsolete destination.

Reproduce the graphical transition checks:

```bash
dotnet build TermCity.slnx
godot --path godot -- --size 80x48 --seed 42 --minimap-jump-test --capture /tmp/jump.png
godot --path godot -- --size large --seed 42 --minimap-jump-test --capture /tmp/jump-large.png
godot --path godot -- --size SF --seed 42 --minimap-jump-test
```

`--minimap-jump-test` rejects the headless dummy renderer. It checks that retained
frames are nonblank and byte-identical during loading, blocks picking against
the pending camera, measures handlers separately from screenshot readback,
revisits both destinations repeatedly, checks latest-only publication through
rotation/zoom and tests one-byte GPU pressure. Warm reuse checks compare the actual
visible destination's ownership/native-upload identities and ground/material
identities. Global background build counters are diagnostic totals, not proof
that a visible destination regenerated. The existing local-edit smoke likewise
counts replacements of previously valid cached chunks, not unrelated first-time
background warming; its nine-chunk invalidation threshold is unchanged.
It also verifies cursor/confirmation action keys cannot act on the pending
picture and exercises jumps while simulation is running, before draining pending
simulation work for the memory-pressure checks.

Seed-42 graphical results on this workstation:

| Check | Result | Budget/status |
|---|---:|---|
| Large random warm home/away | 26/16ms | <250ms; no visible resource regeneration/reupload |
| SF warm home/away | 108/81ms | <250ms; visible native identities retained |
| Minimap handlers | approximately 0.3-3ms | <50ms; no framebuffer readback |
| Large full perspective smoke cold readiness | 324ms | <1,500ms |
| Large full perspective smoke minimap readiness | 178ms | <250ms |
| Large road preview | 14ms | <100ms |
| Large frame p90 / render nodes | 22.7ms / 329 | <60ms / <512 |
| SF full perspective smoke cold readiness | 1,347ms | <1,500ms |
| SF full perspective smoke minimap readiness | 488ms | **Fails 250ms cold-jump budget** |
| SF road preview | 115ms | **Fails 100ms budget by approximately 15ms** |
| SF frame p90 / render nodes | 26.9ms / 373 | <60ms / <512 |

SF's warm-home diagnostic recorded six additional buffered ground uploads, but
none replaced/reuploaded the visible destination's retained resources. Repeated
home/away returns continued to preserve visible identities. Large's retained
destinations used approximately 48 MiB native cache; SF used approximately
56 MiB. Artificial one-byte GPU pressure retired unpinned destinations and
explicitly preserved/reported current-view overage rather than degrading quality.
Full overview scenes can also exceed the CPU target while pinned; a measured SF
overview reported approximately 25 MiB CPU overage during reconciliation.
Final repeated runs recorded Large warm returns at 17-20ms and SF at 70-111ms;
the displayed table is a representative measured run, not a guaranteed latency.
Running-simulation jump checks recorded 11/22ms on Large and 29/58ms on SF with
unchanged visible native resources and nonblank frozen transitions.

The updated full graphical perspective smoke passed rotation, zoom, terrain,
bridges, zones, picking, previews, gardens and water/path artwork. Placement
navigation checks now await actual minimap publication before asserting resumed
edge scrolling, instead of assuming a fixed number of frames is sufficient.
Graphical narrow-interface and garrison checks passed; the focused core/cache/
persistence/road/entrance/presentation run passed 163 tests with six retired tests
explicitly skipped. The first global-counter local-edit assertion and placement
edge-scroll assertion exposed mismatches with background warming and the new
intentional loading gate; those tests now verify the actual contracts without
relaxing geometry or performance thresholds.

Live graphical camera checks on Medium, Large, SF and the debug-siege fixture
passed the unchanged frame/input budgets with the new presentation path. Their
Fast-mode p90 values were approximately 19.9/19.5/26.9/20.7ms respectively. These
are current reruns, not a replacement for the historical failing fixtures above.

These results distinguish removing blank frames from reducing latency: cold
dense destinations can still take hundreds of milliseconds, but the previous
complete map stays visible throughout. CPU/GPU cache targets are not total
process or total-driver memory limits. Native panel resolution, 2x supersampling
and 8x MSAA remain unchanged.

Current Debug validation separates smooth camera motion from cold readiness.
Camera controls passed on small/medium/large random maps, SF/LA/SD/CHI/STL and
80x48, including rotations, tilt, zoom, placement navigation, off-map handling,
fractional dragging, narrow windows and overhead mode. Medium/large/SF/LA/SD
passed all live camera frame budgets. Typical random-map frame p90 is about
20ms; dense SF's worst phase p90 was 36.6ms. CHI/STL Fast-mode p90 was
68.9/72.7ms against 60ms, with maximum frames of 103.8/112.3ms against 100ms.
Small/custom maps recorded isolated 127-159ms frames; the siege rerun recorded
120/139ms outliers despite a combat p90 of 34.6ms. The instrumented renderer
host work was below 1ms in those isolated rerun outliers.

The static SF smoke passes cold readiness at approximately 1,446ms versus
1,500ms, but still fails minimap readiness at 491ms versus 250ms and road
preview at 120ms versus 100ms. Cached selection/panning/picking, exact
cached/fresh geometry and native buffers, local invalidation, out-and-back
reuse and the 373-node scene passed. These failed budgets remain explicit;
they are not an all-passing performance claim or a reason to lower quality.

Simulation runs through one long-lived isolated worker with an ordered input
channel. Initial capture, edits and world replacement create isolated checkpoints;
ordinary combat does not recapture the whole map. Mutable layers and planning
caches remain worker-owned; immutable definitions and completed geometry can be shared.
The UI reads only its last committed game. Publication stays on the UI thread and
preserves game, map, wall, living actor, projectile and unchanged road-network identities, so it does not
reset effects, picking or road/terrain buffers.

Responsive updates retain elapsed slices with their original speed and batch
delivery without merging individual `Update` boundaries. A worker batch publishes
at a combat revision or wave transition. Pausing stops new slices, not a step
that already started. User actions remain immediate: a model-version mismatch
rejects an obsolete result and retries its retained time against the edited city.
Wave announcements are forwarded once on publication and retain automatic Slow
speed for pending slices. Serial/snapshot parity tests include both gate
orientations, RNG, patrol orders, transient motion/deaths, pause/speed changes and
new-game replacement.

The worker grants a completed read lease until the host publishes its result;
the host never modifies the worker's world. Combat-only publication copies
scalars and combat state without copying terrain, roads, zoning or household
arrays. Daily/map-changing publication still copies the required mutable map
state. Immediate player edits reject obsolete results and enqueue a new
checkpoint before replaying retained inputs. Empty paused warm-up jobs publish
only completed analysis caches, never actor state. Explicit quick-saves synchronize
queued inputs before writing, so save-and-quit includes pending simulation time.

Siege scheduling uses the same speed-scaled 0.1-second accumulator and update
cap as serial combat, including the epsilon at a tick boundary. Sub-tick inputs
remain cheap to enqueue; first-wave creation and combat-only ticks no longer run
synchronously just because a day boundary is not due. No siege ticks or elapsed
slices are discarded, and edits during combat reject and retry obsolete worker
results just like day updates.

Defender navigation caches directed ground/walkway/ladder edges and interior
eligibility, not combat decisions or body occupancy. Terrain/navigation,
enclosure topology, established land, wall material and walkway support changes
invalidate these caches. Body blocking is rebuilt for each search with spatial
buckets and the original exact distance/elevation tests. Occupied goals remain
endpoints, never intermediate route cells. A resumable shared breadth-first
search retains the original neighbor order and explores only as far as requested
goals; unreachable posts are filtered before congestion scoring. Wall-post range
checks are shared across archers for each enemy position, while target weighting,
post occupancy, existing-path reuse and missile visibility remain live.

The November 8 combat-only reproduction loads the genuine 858-resident checkpoint
read-only (73 attackers, 24 defenders, 968 wall components), sets Medium speed and
runs 90 updates of 1/60 second without rendering. The handoff baseline took 12.8
seconds for 1.5 seconds of simulation, with 98% in siege processing. The optimized
Debug run took 2.57 seconds (2.29 seconds in siege, 0.27 seconds in daily work).
The equivalent responsive run took 2.37 seconds including worker waits; its worst
host update/publication call was 11.5ms and its final worker slice was 90.7ms.
Saved serial/responsive state matched exactly, with zero retries in that run.
This is roughly a fivefold throughput improvement and removes the synchronous
combat freeze, **not** a claim of real-time Medium-speed throughput: total CPU
time still exceeds the 1.5-second simulated interval, and the cold serial update
peaked at 669.5ms. Rendering/scrolling changes and benchmark budgets are unchanged.
Validation passed 184 focused navigation, assignment, combat, snapshot and camera
simulation tests, plus the headless Godot garrison smoke (patrols, ladder travel,
commands, target distribution, physical arrow impacts and cached city geometry).
The complete Debug solution built with zero warnings/errors, and that exact build
was launched with the original checkpoint without startup errors.

Path layouts, immutable terrain-height samples and global yard decisions can be prepared with the isolated day.
Camera-window changes also prepare their layouts asynchronously while retaining
the previous complete scene. Capture, cold-ready and minimap-ready checks wait for
this preparation and the final dirt mesh, rather than timing only a queued draw.

#### Buffered combat presentation and smoothness follow-up

The subsequent [60fps implementation pass](60FPS_PLAN.md) adds matched CPU/GPU
telemetry, owner-local wall/top dependencies, spatial native siege pages,
retained actor rigs, generation-stamped path workspaces and separately versioned
presentation preparation. It records before/after results and diagnostic-only
native-AA comparisons without changing normal rendering defaults.

`SiegePresentation` owns render-only actor copies and a per-frame clock. New
commits interpolate from the last displayed position/elevation/walk distance to
the committed endpoint, with a bounded arrival-interval estimate. It never
extrapolates actors through walls or advances damage, cooldowns, RNG or saved
simulation time. Pause freezes the presentation clock; world replacement resets it.
Committed edits are ingested before picking/rendering even before the next
process callback. Facing transitions use time-based quaternion interpolation,
and aimed weapons interpolate from a fixed shot-start angle rather than
repeatedly feeding their previous rendered angle back into interpolation.

Derived statistics/services/finance are warmed on checkpoints or changed
analysis/day state. Garrison navigation preparation requires navigation changes;
footpath/yard preparation requires changed map/path/terrain dependencies.
Captured road-layout inputs are immutable worker requests, and prepared path
packets transfer once rather than reappearing on later combat publications.

Animated MultiMesh groups retain capacity and backing transform buffers, encode
changed transforms and bulk-upload changed buffers. Static troll model parts
reuse original authored data. Cheap wall state comparisons retain unchanged
geometry snapshots and recapture affected wall/tower ladder dependencies.
Individual aimed/mounted-defense updates keep the bulk-buffer mirror current.
Headless Godot does not provide graphical native-buffer readback: those runs
explicitly check encoded transforms and report native checks skipped; graphical
runs verify the actual retained buffers against model/picking transforms.

Water shader and river-activity phases now advance every rendered frame without
regenerating terrain or activity-site catalogues. Ambient effect presentation no
longer has a separate 15Hz redraw cap; expensive spawning remains governed by the
existing effect director. Native resolution, 2x supersampling, 8x MSAA, terrain
residency and the scrolling improvements remain unchanged.

Automatic saves capture a consistent committed checkpoint, then serialize and
rotate backups on a background task. Requests coalesce while a save is pending;
completion marks only the captured revision/time, errors are reported on the
host, and unique temporary files are cleaned up. Atomic newest-backup replacement
preserves the prior newest save on rotation failure. Explicit quick-save remains
synchronous and first drains ordered simulation inputs. Shutdown drains retained
updates and pending autosave before stopping the worker. HUD refresh is dirty-
driven, including layout/zoom changes, while messages and focused inspectors
remain current. Narrow debug controls wrap and retain the shared terminal frame.

The original read-only 858-resident checkpoint (73 attackers, 24 defenders, 968
wall components) was measured graphically at normal zoom, focused on an attacker.
During 15 seconds of Medium-speed operation:

| Measure | Result | Budget/meaning |
| --- | ---: | --- |
| Rendered frames | 558 | 557 frames changed actor poses |
| Frame p50 / p90 | 21.2 / 53.3ms | p90 passes existing 60ms budget |
| Frame p95 / p99 | 55.7 / 70.6ms | **fails added 25 / 50ms smoothness targets** |
| Worst frame | 86.9ms | passes existing 100ms budget |
| Worst host simulation/publication | 8.8ms | not a hard 4ms deadline |
| Worst actor update | 11.0ms | below existing 12ms actor budget |
| Peak / stopping backlog | 4.486 / 4.387 seconds | elapsed time retained, not discarded |
| Backlog drain | 7,678.2ms | zero retries; zero repeated world captures after warm-up |

This demonstrates continuous pose updates and bounded host work, **not** sustained
60fps or real-time Medium-speed simulation throughput. The save still needs more
simulation/GPU work for those goals. A full graphical settlement run also retained
an active-swarm p90 failure around 74-80ms against 60ms; it was not converted into
a pass. The large seed-42 graphical performance smoke passed: cold 357.8ms, frame
p90 17.9ms, actual minimap click-to-render 157.1ms, 279 nodes and road preview
worst 14.2ms. Garrison graphical integration passed rotation, zoom, ladder/body
picking, bow poses, target distribution, physical impacts and cached city geometry.
The deterministic headless full settlement run passed geometry, controlled
presentation, repair, upgrade, selection, construction, growth and narrow-window
checks.
Final focused validation passed 334 tests with six retired glyph-animation checks
explicitly skipped. The graphical wall-network run passed exact retained native
transforms, wall/body picking, four rotations, three zooms, previews, selection,
closure/gates, undo and save/load. It retained separate populated wall-purchase
latency failures: 154.0ms incremental and 204.0ms full-control at 1,000 residents,
against the unchanged 100ms click-to-render budget. Its empty-city incremental
case passed at 87.2ms. These are not all-budgets passing results.

Reproduce the saved-combat diagnostic without replacing the checkpoint:

```bash
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --camera-performance-test --capture /tmp/combat-cadence.png
godot --audio-driver Dummy --path godot -- --seed 42 --garrison-smoke-test --capture /tmp/garrison-cadence.png
godot --audio-driver Dummy --path godot -- --seed 42 --size large --perspective-performance-test
```

Saved camera diagnostics retain the actual attackers instead of replacing them
with the smaller new-city debug wave. Existing budgets remain enforced; added
pose/displacement and p95/p99 metrics are reported separately.
Native instance/roof batches are retained per existing material group and compared
against complete authored buffers before reuse; this does not add draw groups.
Cached buildings also retain their encoded instance buffers and color-grouped roof
vertices, avoiding repeated per-instance encoding during a full buffer rebuild.
Footpath road clipping, triangulation, captured-ground sampling and relief/indexed
buffer generation share one bounded worker; its Geometry2D calls use thread-local
math arrays, never scene-tree nodes or rendering resources.

Local building/yard changes repair the affected nearest-road distance subtrees.
A distance-guided FIFO pass preserves the exact original source/next-step tie
order, so cached and fresh routing produce identical daily path wear. Road and
terrain changes still invalidate the whole field. Fortification traversal uses
directed cell-edge masks invalidated with wall topology; path compaction leaves
unreachable cached travel fields untouched. On maps without fortifications, large
independent field repairs use at most three participating threads after lazy
step-cost caches have been filled on the calling thread. Workers own their route
arrays; results are published only after joining, and serial/parallel results are
compared exactly in tests. Fortified graphs retain serial repair to avoid
front-loading a complete wall-link cache during a live frame.

The minimap is keyed by physical navigation/terrain changes rather than path wear
or the simulation clock. RGB pixels are uploaded in one buffer, and same-size
updates retain the existing texture instead of issuing a native call per pixel
and replacing the texture every day. Petitions also retain their computed text
until the analysis state or weekly complaint index changes.

Performance is **not guaranteed smooth on every populated scenario**. Early
Debug-siege runs reproduced 600-950ms periodic freezes despite paused drags staying
below 25ms. Isolated updates remove that main-thread daily-update dependency;
background work and its retained time are logged separately. Remaining native
scene reconstruction, cold viewport loading and occasional allocation spikes are
still measured against the original budgets, not hidden behind a good p90.

On the Apple M2 validation workstation, the final normal Debug siege run measured
19-21ms frame p90 during noncombat drags, with worst frames of 60ms at Slow,
115ms at Medium and 43ms at Fast. Combat measured 40.7ms p90 and 72ms worst.
The single Medium overrun exceeded the unchanged 100ms worst-frame budget by
15.2ms; a prior complete Debug run passed every phase. Retained simulation backlog
drained to zero at every phase boundary. Build and 304 coupled core checks passed;
graphical interface, garrison and exact footpath artwork checks passed.

The final supported-map matrix passed camera/control correctness everywhere.
Random medium/large camera runs also passed all performance budgets. Small and
80x48 fixtures retained isolated 131/122ms overruns. Populated LA/SD/CHI/STL
retained worst running frames of 208/183/250/385ms, respectively; cold wide-buffer
crossings reached 249/364/600/413ms. These render findings remain failures against
100ms, not universal smoothness claims. SF running Medium/Fast measured worst
frames of 41/44ms; Slow had one 180ms outlier, while buffer crossing measured 95ms.

The original perspective check retained minimap overruns (large 258ms, SF 508ms
against 250ms) and an SF road-preview overrun (118ms against 100ms). SF batching
remained at 354 render nodes, below the unchanged 512-node limit; large-map
batching measured 320 nodes. Cold scenes passed at SF 1304ms and large 515ms
against 1500ms. Do not classify the larger latency overruns as near-limit successes
or remove them from the benchmark.

Requirements: **.NET 10 SDK** and **Godot 4.7.2 .NET**. The standard Godot edition cannot run C# scripts.
The root [TermCity.slnx](../TermCity.slnx) includes core, Godot and unit-test projects.
The Godot-local solution also remains available for editor integration.

```bash
dotnet build TermCity.slnx
godot --path godot -- --seed 42 --size medium
godot --path godot -- --seed 42 --size small --debug-starter
godot --headless --path godot -- --size SF --seed 42 --dump-map
godot --path godot -- --size small --seed 42 --building-smoke-test --capture /tmp/termcity-buildings.png
godot --path godot -- --size small --seed 42 --milestone-smoke-test --capture /tmp/termcity-milestone.png
godot --path godot -- --size small --seed 42 --woodlot-smoke-test --capture /tmp/termcity-woodlot.png
godot --path godot -- --size small --seed 42 --placement-smoke-test --capture /tmp/termcity-placement.png
godot --path godot -- --size small --seed 42 --perspective-smoke-test --capture /tmp/termcity-perspective.png
godot --path godot -- --size large --seed 42 --perspective-performance-test
dotnet build TermCity.slnx -c Release
dotnet test TermCity.slnx -c Release --no-build
```

### Building depth rendering and comparison

The combined depth treatment is the default in gameplay, placement previews and
rendered model thumbnails: directional shadows on lit terrain, recessed house
openings, deeper stepped eaves, log-built timber defenses and projecting stone-wall details. Logical building
footprints, wall openings, gameplay and saved layouts are unchanged. The shared
default is `DepthStudyArtwork.DefaultTreatment`; original rendering is retained
only as an explicit comparison/benchmark control.

The Debug-only comparison is a private, paused staging scene, not a loaded city.
It does not save or change footprints:

```bash
dotnet build TermCity.slnx
godot --path godot -- --depth-comparison
godot --path godot -- --depth-comparison --capture /tmp/depth-study.png
godot --path godot -- --depth-comparison-smoke-test --capture /tmp/depth-study.png
```

Compare a timber home, stone home, stone wall/tower/gate assembly, wooden
wall/tower/gate assembly and the canonical Motte and Bailey palisade artwork.
Keys **1–4** switch between original rendering,
lighting only, architectural relief only and their combination (the game default).
The scene starts with the combined treatment. Only one live
3D viewport exists. Switching treatments retains the camera, zoom and deterministic
model variation; changing subjects chooses an appropriate starting zoom.

Use **Q/E** or right-drag to rotate, **T** to cycle tilt, **-/+** or the wheel to
zoom, **R** to reset the camera and **[ / ]** to change subjects. Left-click tests
model picking without editing gameplay. **C** writes four matched, labeled PNGs
for the current subject, named `<prefix>-subject-<1–5>-<treatment>.png`.
The default prefix is `user://depth-comparison/study.png`; `--capture` overrides it.
**Esc** exits the study. Capture controls are temporarily disabled so input cannot
change the camera between images. Capture errors are reported in the footer and
Godot log.
The graphical smoke also writes `-close-subject-4-<treatment>.png` comparisons
of real wooden settlement defenses at normal zoom.

Lighting variants reduce ambient fill, change the sun angle and enable directional
shadows, including a lit version of the existing terrain shader so the ground can
receive shadows. Relief variants use real window/door recesses, deeper stepped
eaves, projecting wall-walk caps and selective stepped stone supports. The palisade
keeps its already-dimensional post geometry as a lighting control. No SSAO,
normal maps or renderer migration is included. Wooden settlement walls and towers
use the same bark tones, coarse grain, shaped log shoulders and rough-cut crowns
as the reference palisade, rather than retaining timber-colored slabs.
Pixel palettes, filtering, antialiasing and the terminal interface are retained.

The comparison smoke checks all subjects/treatments, rotation and zoom picking,
cached camera movement, narrow-window controls, and graphical captures. Run it
graphically to validate images; headless execution cannot validate shadows.
For original-versus-default city-scale measurements, use the existing performance
smoke, adding the explicit Debug baseline flag for the original treatment.
The older `--depth-performance-comparison` flag remains accepted as an explicit
request for the combined default:

```bash
godot --path godot -- --size large --seed 42 --perspective-performance-test --depth-performance-baseline
godot --path godot -- --size large --seed 42 --perspective-performance-test
godot --path godot -- --size SF --seed 42 --perspective-performance-test --depth-performance-baseline
godot --path godot -- --size SF --seed 42 --perspective-performance-test
```

Keep window dimensions and display scaling identical between runs. This measures
the candidate with the same frame, selection, buffered-pan, road-preview and actual
minimap click-to-render budgets as the baseline, rather than benchmarking four
simultaneous comparison viewports.

Pre-promotion prototype measurements on Apple M2 / GL Compatibility, with the performance
harness at 960x800 and canvas scale 1:

| Scene / treatment | Frame p90 (60ms budget) | Minimap click-to-render (250ms budget) | Road preview (100ms budget) |
| --- | ---: | ---: | ---: |
| Large random / current | 15.9ms | 250.3ms | 18.1ms |
| Large random / combined | 16.1ms | 269.5ms | 17.2ms |
| Populated SF / current | 6.0ms | 481.6ms | 163.4ms |
| Populated SF / combined, repeated run | 40.6ms | 412.4ms | 138.6ms |

These are comparisons, not passing performance claims: minimap latency exceeded
budget in all four runs, and both populated-city road-preview runs exceeded
budget. The initial populated combined run reached 843.5ms minimap latency before
the repeat above; retain that first-run finding rather than treating the repeat
as a guarantee. Populated baseline cold rebuild was 2044.2ms against 1500ms;
the repeated combined run was 1237.8ms. No budgets were relaxed.
The isolated graphical study passed all 16 subject/treatment combinations,
rotation/tilt/zoom picking, placement ghosts, visible ground-shadow pixel
comparisons and 640x780 layout. Broader baseline smoke runs separately reported
waterfront-starter placement and Ballista release-timing failures; these are
outside the opt-in art study and are not represented as passing checks.

After promotion, the normal (no comparison flag) performance runs measured:

| Default scene | Frame p90 (60ms budget) | Minimap click-to-render (250ms budget) | Road preview (100ms budget) |
| --- | ---: | ---: | ---: |
| Large random | 16.3ms | 258.9ms | 16.1ms |
| Populated SF | 36.0ms | 428.5ms | 131.9ms |

Frame, selection, picking, buffered-pan and cold-rebuild budgets passed. Minimap
latency remained over budget, as did populated road previews; these findings are
retained rather than weakening the benchmark. Default building rendering, actual
cached submenu thumbnails, graphical depth comparisons and freeform wall-network
checks passed. Promoting relief also exposed reversed clipped wall-cap triangles;
the renderer now normalizes their winding before emission. Original timber walls
resolve their effective material before choosing stone-only supports.
The supplemental woodlot-art smoke still reports a part-count mismatch: its
fixture compares world-sized plots to authored-size artwork. It is not counted as
a passing check.

### Terrain relief, artwork and comparison

The selected production treatment uses balanced materials/terrain-edge relief
everywhere and **3x hills in newly generated random maps** (approximately eighteen
feet maximum). `CityGame.New` assigns the new random-map profile; populated
scenarios retain the original scale. Saves contain an optional `ElevationProfile`;
missing profiles load as `Current`, preserving existing save heights. New 3x
maps retain their profile across save/load and undo. Invalid profiles are rejected
explicitly. This is additive version-two save data, not a rewrite of old maps.

The shared artwork pass also supplies clustered, stepped foliage and trunk
forks/roots, textured rock facets, coarse dressed-timber and masonry materials,
stronger bridge rails/caps/piers/underbeams, recessed workplace/civic facades,
hollow wells and aqueduct troughs, and real forge openings. Roof overhangs remain
pickable. Attached tower projections and disjoint masonry runs avoid coincident
wall/tower faces; canonical model parts are shared by gameplay, ghosts and thumbnails.
Already-dimensional statues, weapon/actor models and completed palisades retain
their established forms. Tree positions/spacing, road widths/centerlines/grades,
logical building footprints, simulation output and terminal UI are unchanged.

Structure picking uses a cached per-owner broad phase followed by the same exact
box/roof tests. Trees and rocks use their actual model boxes and frontmost
occlusion. Feature inspection is separate from terrain-based placement and planar
panning, so leafy crowns do not redirect road/building previews to a trunk cell.
Complete footbridge picking uses actual plank, rail and support triangles.

The Debug-only comparison controls do not change another game or its saved data.
Current building, palisade and natural-feature artwork is present in every candidate:

1. Original terrain and original hill heights.
2. Lighting consistency for roads, bridge decks, cliffs and banks.
3. Balanced broad grass grouping, turf caps and recessed earth strata at current heights.
4. Balanced treatment with 2x hills (approximately twelve-foot maximum hills).
5. Selected balanced treatment with 3x hills (approximately eighteen-foot maximum hills).

```bash
godot --path godot -- --terrain-comparison
godot --path godot -- --terrain-comparison-smoke-test --capture /tmp/terrain-study.png
godot --path godot -- --size large --seed 42 --perspective-performance-test --terrain-performance-treatment DoubleHills
godot --path godot -- --size SF --seed 42 --perspective-performance-test --terrain-performance-treatment DoubleHills
```

The performance flag accepts `Current`, `Lighting`, `Balanced`, `DoubleHills` and
`TripleHills`. It requires an isolated Debug performance smoke and rejects loaded
cities and other comparison modes.

Use **1–5** to select treatments, subject buttons or **[ / ]** for the three
fixtures, **Q/E** or right-drag to rotate, **T** for tilt, **O** for overhead,
**-/+** or the wheel to zoom, middle-drag for planar fractional panning, **R** to
reset and **C** for five matched captures.
The default capture prefix is `user://terrain-comparison/study.png`, separate from
the building comparison. The graphical smoke writes normal and `-close` captures
for terrain/river, village/infrastructure, woodland/outcrop and a nine-model
structures/props gallery, plus a 640x780 narrow-window capture.

Height profiles are per-map `TerrainElevationProfile` values. Debug candidates
can override them with `GameMap.SetPreviewElevationProfile`; the method invalidates
terrain/travel caches and never mutates global constants. Terrain elevation,
maximum picking heights, hill thresholds, bank margins, road grading, foundations
and bridges consume that profile. Fixtures are deterministic private maps.
River/sea levels and logical cell layouts are unchanged. Production profiles
are persisted; private comparison controls do not save or alter normal games.

Lit roads and banks have explicit clockwise-compatible normals. Balanced strata
are inset into existing terrace/shore faces, with turf caps below the unchanged
top planes. Neighboring recessed bands continue across cell seams without internal
fin walls. End-cap continuity uses the same strict inset eligibility as the adjacent
band and its actual land/graded shoulder heights; a cut or a band ending at a
terrace top must close rather than expose the background. Road cut geometry, centerline offsets, widths and grade rules are not
replaced by texture tricks. Actual bank faces participate in candidate terrain
picking. All geometry remains chunked and batched.

The graphical study checks all twenty subject/treatment combinations, visible
terrain picking over rotations/tilts/zooms, overhead, both bridge types, reference
building/feature picking, placement ghosts, cache reuse, profile/save isolation and real
road shadow reception using shadowed/unshadowed pixel comparisons.

The initial fifteen-case prototype measured 16.7-21.2ms
frame p90 against 60ms, at most 0.5ms terrain picking against 50ms, and at most
54 render nodes against 512. The road-shadow pixel comparison found nineteen
darkened road samples outside the reference models' silhouettes.
With the `DoubleHills` candidate, the large random-map performance smoke passed
(237.9ms minimap click-to-render against 250ms; 16.1ms frame p90 against 60ms).
Populated SF remained over budget for minimap clicks (443.0ms against 250ms)
and road previews (151.0ms against 100ms), while its 37.8ms frame p90 passed.
These are disclosed comparison results, not passing claims for the populated
interaction budgets; no thresholds were relaxed.

After the full artwork rollout, the default large-map smoke passed at 16.3ms frame
p90 (60ms budget), 0.9ms picking (50ms), 319 nodes (512) and 229.7ms minimap
click-to-render (250ms). Populated SF retained its original heights and measured
40.7ms frame p90, 4.2ms picking and 353 nodes. Its minimap clicks (430.7ms/250ms)
and road previews (126.7ms/100ms) still exceeded budget. The owner-level picking
index avoids testing every small facade/foliage box for every interaction; no
budgets were weakened.

The full map-art pass passed 147 focused artwork/terrain/road/shoreline tests and
the twenty-case graphical comparison, including the structures gallery, natural
feature picking and smooth panning. A broader persistence/placement/world-scale
selection passed 217 tests with one legacy skip, but retained two world-scale
failures: the old gate-deck assertion requires a centered deck instead of its
actual inward walkway offset, and the large-settlement troll-introduction timing
assertion fails. These checks are disclosed, not counted as passing, and their
unrelated simulation/fortification behavior was not changed by the artwork pass.

### Physical world scale

`WorldScale` fixes one foot at **0.225 scene units**. The unchanged tier-one goblin's
1.0125-unit anatomical crown represents **4.5 feet**; Orc anatomy is 6 feet.
Combat tiers retain existing goblin/Orc health, damage, speed and one-second attack
formulas but no longer enlarge their bodies. `SiegeEnemy` remains neutral attacker
state; `SiegeEnemyDefinition`/`SiegeEnemies` centralize species rules. Wave orchestration
stays in the siege subsystem; there is no generic movable hierarchy.

Building models use their original authored coordinate system at
`WorldScale.ArchitectureScale` (five world units per authored unit). The registry exposes
actual occupied `Width`/`Height` and separate authored `ModelWidth`/`ModelDepth`.
The renderer applies the same conversion to complete models, roofs, openings, props
and picking transforms; foundations use the converted footprint. Floors are authored
independently of population level so stacked homes retain human-usable clear openings.
Do not apply the conversion twice to natural trees embedded in building models.

Material-aware wall barriers are **20/26/30 feet**, with **30/36/40-foot towers**.
`WallDamageLayout` supplies physical widths/depths, damage fragments, wall-walk height
and horizontal bounds to drawing, collision and garrison movement. Main gates use
three shared-health opening cells, a 13.3-foot clear passage and overlapping structural
posts; the passage accommodates grid-aligned human lanes without shrinking bodies.
Gate leaves have raised crossbars and metal fasteners on both faces. They share
damage-aware fragments with rendering and picking and remain below the access deck.
Two-cell corridors reserve future wall thickness, with extra inner access around
towers. Nearby towers shift clear of the gateway rather than leaving a gap in
automatic arrow coverage. Founding validates clear exterior corridors, allowing
grated wall crossings over water while keeping starter sites dry and gate-reachable.
Periodic towers are omitted when their full meshes would overlap a fixed corner
tower; construction reservations use the same placement rule. Pre-fix saves whose
tower flags encode overlapping layouts are rejected explicitly, not silently moved
or rewritten. New or restarted settlements use the corrected spacing.
Archers stand on actual deck fragments, including neighboring tower platforms, not
parapet tops. Guards wait inside gates and corners, leaving ladder approaches clear.
Projectile origins, mounted weapons and placement ghosts share the supported deck
height; actor interpolation includes the previous and current terrain levels.
Deck caps replace the upper body layer instead of sharing its exposed top face.
House plinths and doorsteps use disjoint stone pieces, and hillside supports stop
at the plinth underside. Keep these surfaces nonoverlapping: coincident textured
faces cause striped depth artifacts when the camera scrolls. Settlement smoke checks
foundation overlap, tower-deck picking and cached geometry during fractional pans.
Ground-level timber beams are omitted where the stone plinth already occupies the
same top plane. Checks include differently colored faces, not just stone-on-stone
overlaps, and exercise all four corner towers over 48 fractional pan/rotation cases.
Opaque 3D textures use linear mipmapped anisotropic filtering, retaining their
original palettes and broad patterns without nearest-sampled pixel steps. Closed cuboids cull back faces;
open roof/path meshes remain double-sided. Tree dimensions vary at fine deterministic
precision within the same bounds, rather than repeating five canopy heights that
produce coplanar foliage where neighboring crowns overlap.
Motte and Bailey palisades use individually shaded dark timber posts and interior
braces instead of flat wall slabs. `PalisadeArtwork` varies log girth, depth, inset,
height and five rough-cut crown profiles using a stable building-position seed.
Chamfered shoulders, occasional recessed chinks and notched/broken crowns are real
disjoint volumes, not decals or repeating stacked caps. Thin exposed end grain replaces
the top bark layer; no tip is pasted over a coincident body face. Five bark tones,
coarse elongated scars and broad knots break up repeated wood patterns.
Texture color is applied once, retaining mipmapped filtering and cached material batches.
Models, picking and construction thumbnails share these parts; overhead sprites use
the same darker palette. Wooden settlement walls, towers and gate posts share this log generator. Their
visual parts are clipped to canonical damage fragments; tower skins follow the
outer perimeter instead of creating solid or nested interior slabs. Supporting
log tops remain level below the existing decks, while exposed crenellation crowns
are rough-cut. Decks, gate fittings and water grates retain their physical
positions; inner braces and end grain use the darker shared timber palette.
Drawing, exact picking, selection, wall/founding ghosts and construction thumbnails
consume the same visual parts. Fire surfaces are selected from exposed log tops,
and burned timber retains coarse charred grain. Stone and reinforced-stone walls
keep their masonry treatment. Logical wall footprints, walkway support, heights,
health, damage bands and upgrade rules are unchanged.
The wall-network smoke verifies this path on actual settlement walls, freeform
joins, water crossings, gates, mounted weapons and canonical construction
thumbnails. Its graphical palisade fixture measured 13,987 timber parts in 114
cached render nodes, 0.1ms camera sync, 7.1ms picking and 16.8ms frame p90
(budgets: 512 nodes, 50ms sync/picking, 60ms frames). The current large-map
performance smoke passed, including 205.0ms minimap click-to-render against
250ms. Populated SF still exceeded the existing minimap and road-preview
budgets: 437.1ms against 250ms and 129.3ms against 100ms, respectively; its
35.0ms frame p90 passed the 60ms budget.
New random maps space natural trunks at least 18 feet apart using seeded,
non-row-ordered placement priorities. Woodland clusters remain, but full-sized
crowns no longer grow from adjacent 4.4-foot cells. These are actual feature
placements, not hidden render-only trees; clearing and inspection match what is
drawn. Existing saves and hand-authored scenario woodland are not rewritten.

New default random maps reserve 12.5% of dry land for seeded forest regions and
12.5% for hill terrain. Several separated centers produce coherent, irregular
patches rather than map-wide speckles. Forest coverage describes the regions,
not trunk-cell or literal canopy coverage: candidate density is 55% inside and
0.1% outside, followed by the unchanged trunk-spacing and shoreline rules.
Default water generation precedes regional hills; optional river canyons consume
the same hill budget. Forest/hill masks are generation-only data; no new zones,
region labels or save layers are added. Custom hill/scatter generators retain
their original noise behavior unless `GeographicRegions` is explicitly enabled.

Tree, stone and mud trolls append species IDs. Deterministic `AppearanceSeed` controls
stable 8-11-foot individual stature; `TrollArtwork` emits distinct original silhouettes.
Species profiles vary durability, damage, movement and attack cadence without adding
special powers. Body-aware exterior movement checks terrain, buildings, wall bounds
and swept mixed-species collision; route floods account for each large species'
actual radius. Spawn positions also avoid ground defenders. Only front-rank attackers
can reach a wall. Blocked first approaches and forced goblin-only waves defer pending
monster introductions without producing invalid saves. Troll arm strikes pivot at the
shoulder and match their physical contact distance.
The permanent 1,000-person introduction uses a saved one-time warning/forecast.
Neutral save members retain JSON names **`Goblins`** and **`NextGoblinId`**.
New-layout round trips and undo are supported; pre-scale random-world layout migration
is out of scope. Prepopulated scenarios retain their original building/road definitions
through `GameContent.ForScenario`, without moving or enlarging their placed objects.
Cross-scenario loads copy the definitions rather than mutating the live registry.

Dirt/cobbled/King's Road surfaces are **1/2/4 cells** wide. Canonical offset edges still
drive reservations, cut/fill walls, shoulders, bridges and picking. Mature trees are
roughly 20-34 feet tall, and bridge rails are human-height. The sidebar retains a
visible minimap and scrolls overflowing reports instead of collapsing navigation
to zero height in populated settlements.

Historical examples, not a universal medieval standard: original eastern sections
at [Conwy](https://en.wikipedia.org/wiki/Conwy_town_walls#Architecture) were about
**12 feet**, [York](https://en.wikipedia.org/wiki/York_city_walls) generally has
**13-foot masonry on earth ramparts**, and
[Avila](https://en.wikipedia.org/wiki/Walls_of_%C3%81vila) averages **39-foot walls**
with principal gate towers about **66 feet** high. That example range supports a
20-foot fantasy barrier as plausible scale; it does not prove an 11-foot creature
cannot climb. The explicit game rule requires trolls to breach intact walls.

```bash
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'FullyQualifiedName~WorldScaleTests|FullyQualifiedName~SiegeEnemyDefinitionTests'
godot --headless --path godot -- --seed 42 --size medium --world-scale-smoke-test
godot --path godot -- --seed 42 --size medium --world-scale-smoke-test --capture /tmp/termcity-world-scale.png
godot --path godot -- --seed 42 --size medium --settlement-smoke-test --capture /tmp/termcity-settlement.png
```

The world-scale smoke authors a new 1,000-person populated fixture on a large map,
measures the existing perspective/minimap budgets and a 120-attacker mixed swarm,
then verifies five-species picking over four rotations and three zooms. The ordinary
large random-map performance smoke remains a separate check. Smoke audio is silent.
Paid 10,000-population playthroughs use regional layouts of 480x288, 560x336 and
640x384 cells: small pre-scale maps do not contain enough physical land for that goal.
They retain the original population target, paid purchases, positive-treasury and
no-loan requirements. The guide searches beyond crowded local neighborhoods for
replacement civic sites and restores full service funding when a wealthy mature city
stalls on happiness. It tries smaller service buildings when larger upgrades do not
fit, and retains existing providers instead of removing capacity before distant
replacements have arrived. No population, treasury or unlock values are injected.
Use Release outputs for these long simulation runs. Set `TERMCITY_PLAYTEST_OUTPUT`
to retain progress/complaint logs and final worlds, including failed attempts for diagnosis.

### Settlement and siege validation

The new settlement route is **G > Motte and Bailey enclosure**. Its first click anchors
one corner; pointer movement or cursor keys resize the opposite corner, and a second
click/Enter purchases the complete enclosure atomically. Garrison uses the shared
construction catalog, canonical model thumbnails, statistics and Add/Cancel actions,
with the same terminal framing, tooltips and narrow-window scrolling. Manual zoning
and the existing city scenarios retain their legacy behavior.

The siege debug preset preplaces one Guard Post and one Archer Barracks, using
normal paid placement after establishing its 100 residents and before filling
workplaces. Both have valid interior sites clear of gate approaches and recruit
their ordinary four-person rosters. Their footprints are reserved before spreading
homes; an undersized enclosure fails explicitly rather than omitting a garrison
or reducing the 100-resident population. Plain settlement debugging remains unchanged;
same-seed siege restarts recreate both garrisons.

Desktop `--debug-settlement` and `--debug-siege` launches request exactly 64x64
walls through `SettlementDebugSetup.DefaultWallSize`. `SettlementDebugSetup.Create`
discards unsuitable maps and regenerates with consecutive seeds until the complete
preset fits, rather than reporting a terrain placement error or shrinking the walls.
Starter-site and housing-capacity checks use the same gate facing. Only placement
failures trigger retries; invalid dimensions, insufficient starting funds and presets
that cannot fit even on clear land still report configuration errors. The clear-land
feasibility check uses a disposable map, never the played terrain.
Desktop generation runs off the UI thread and can be cancelled by closing the window.
The accepted seed is stored in the game and saves; regenerated starts log
`TERMCITY_DEBUG_SETTLEMENT_REGENERATED` and show the seed and retry count in the UI.
Explicit siege dimensions and restarts retain their requested/current dimensions.
Restarts use the same regeneration factory and only replace the existing game after
the entire preset succeeds. Normal map creation, loaded saves and manual founding
do not regenerate.
Internal integration fixtures may still use the capacity-checked size search.

```bash
godot --path godot -- --seed 42 --size small --debug-settlement
godot --path godot -- --seed 42 --size small --debug-siege
godot --headless --path godot -- --seed 42 --settlement-smoke-test
godot --path godot -- --seed 42 --settlement-smoke-test --capture /tmp/termcity-settlement.png
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'FullyQualifiedName~Settlement'
```

Settlement state and siege rules live in the UI-independent core. Save version 2 has
an optional settlement section; pre-scale footprints are not migrated. Combat advances on the
simulation clock and respects pause, focus suspension and game speed. Drawing and
picking never advance combat RNG. Wall/goblin meshes have their own batched scene
roots; combat and health updates must not invalidate terrain or rebuild the city.
`PerspectiveCity.Siege.cs` renders palisades, stonework, gate openings, towers, mounted
weapons, goblins and projectiles; `TerminalMap.Siege.cs` handles health bars, weapon-range
circles and the overhead representation. Minimap combat markers do not rebuild its
terrain texture.

`TowerDefenseArtwork.cs` owns shared oil/stone station geometry, front-hinged
cauldron poses and gravity-driven drops. Towers keep two stations per primary
exterior face and append one station in the forward outer gap of each available
adjacent face. Already occupied faces and the ladder-access face are excluded.
Straight towers have four stations; original corner towers have five. Existing
station indices remain stable, and the saved-attack validator accepts the expanded
bounded index range, including orphaned releases. Each release still uses one
station and the existing damage/reload, not simultaneous extra attacks.
Stations sit between the merlons, with a four-step access flight and connected raised
platforms; ordinary walls retain a single position. Previews, picking and selection
bounds use the same parts as the city renderer.
Generated corner/junction towers use their full tower platform for weapon placement;
their short logical anchor spans are not subject to ordinary wall-span length checks.
Health, unlock, gold and existing-defense checks still apply.
Cauldrons tip over the outside edge and pour a connected sheet that reaches a
ground-level station footprint. `DropAttackLayout` defines oil's 1.5 x 1.5-cell
strip and rocks' 1 x 1-cell area. Stone racks release staggered tumbling stones;
Drop acquisition measures the unchanged reach from the actual station, not the
tower center: full-scale attackers stop at body-aware contact beyond a center-based
2.5-cell radius, which otherwise prevents oil from defending its own tower base.
Footprint membership and obstruction still constrain every candidate and victim.
each attack applies damage once at the first ground contact, not once per stone.
Oil uses a single connected folded-sheet mesh with shared cross-sections, animated
width/edge undulation and a thinning stream. A dark amber, partially translucent
non-emissive material replaces the gold cuboid chain that read as a laser beam.
Three coarse teardrop splashes lift from ground contact while oil is arriving;
they are bounded visual droplets, not projectile hits or extra damage ticks.
The mesh updates with siege actors, not static city/fortification geometry.
Its ground artwork
spreads over 0.18 siege seconds through five connected, disjoint broad bands,
clipping the corners inside the fixed damage footprint instead of popping into
a square decal. Both map views share this pool layout; reduced motion uses its
full static pose. This cosmetic growth does not add damage ticks or change reloads.
`CombatProfiles` and `WallDefenses` centralize fighter/mounted damage and intervals.
`SettlementAttacks` handles fixed drop snapshots, direct swept first-hit projectiles,
arcing trebuchet impacts with 360-to-90 radial falloff and guard contact revalidation.
`SettlementAttackSave` preserves stable attack IDs, world release/landing geometry,
age and applied flags. Source removal retains released attacks; debug reset clears
them. Current network saves without the new list load it empty; old cosmetic traces
are not reconstructed as damage. Save cooldown validation accommodates the new
10/14-second reloads, and fighter windups share the persisted attack lifecycle.
Moving pot parts update cached MultiMesh slots and their pick volumes; attacks
must not rebuild the city or static fortifications. Pause freezes the motion;
reduced motion uses static ground impact guides. `TowerDefenseSmoke` exercises
real attacks, access geometry, connected oil, delayed contact, stone drops and exact footprint sizes.

`CityGame.Development.cs` replaces bailey zoning with demand-driven site scoring.
All trades compete for shared interior land; only constructed footprints receive
internal R/C/I classifications for the existing simulation indexes. Site selection
preserves worn paths, an inner repair alley, gate clearance and flood-reachable access
to every interior building. Land value and crowding choose broad courtyard homes,
compact houses, stacked shanties or inner-wall housing. Shanties are new infill only:
crowding must not automatically replace existing homes with shanty towers. Wealthy
weekly redevelopment preserves households instead of manufacturing population. New building registry entries are
appended, preserving existing save IDs; load migration clears only vacant bailey zones.
`SettlementDevelopmentTests` covers sites, trade mix, wealth, crowding, wall preservation,
artwork and deterministic persistence.

`CityGame.Settlement` derives the map's footpath bounds on founding and load. Bailey
journeys connect building perimeter/yard cells, ignore road destinations, and use a
shortest-edge spanning forest within the reachable interior. Footpaths still wear
gradually and persist; loading older baileys trims outward trails at the wall.
Legacy city and bridge-path behavior remains unchanged when no settlement is present.
Fuel pollution uses the building-aware `CityServices.PollutionRadius` helper in both
simulation and range guides; fuel sources are capped at six cells without changing
smoke intensity, industrial emission ranges or commons cleanup.

`Main.DebugSiege.cs` owns the Debug Siege map-edge toolbar. It is gated by the explicit
session mode, uses `TerminalFrame` and the shared terminal button theme, and wraps at
narrow map widths. Its text input consumes gameplay shortcuts while focused.
Labels, inputs and buttons default to 20 logical pixels. Right-sidebar body and camera
text uses 22 pixels, with 24-pixel headings/zoom controls; its minimum width is 320.
The demand section is removed, but simulation forecasts and growth reports are unchanged.
The minimap yields vertical space before readable city text; unusually short windows scroll.
`--interface-smoke-test --capture /tmp/termcity-interface.png` isolates 960/640 layouts
at 16/20/28 font scaling, enlarged debug controls, duck/rail ordering, camera controls,
friendly picking and rendered sub-tick cadence from the broader settlement art suite.
Core debug wave/clear/repair actions remain Debug-build-gated, use ordinary session
snapshots for paused undo, and only invalidate siege rendering. Forced waves replace
the current swarm atomically, honor the 120-attacker save limit and use the normal
dry-land routing and milestone scaling. Restart uses the existing progress guard and
restores the same-seed preset, including its initial camera focus and population.
The settlement integration smoke covers button actions, keyboard isolation and
960/640-pixel layouts; `DebugSiegeControlsTests` covers gating, validation, persistence
and restart semantics.

Debug wall width/height inputs are applied through the guarded same-seed restart.
Explicit dimensions use exact rectangular placement, not the default 64/56/48/40/36
fallback search. Validation happens before replacement, so rejected dimensions or a
failed dry-land/cost search preserve the live city. Ordinary restarts retain the
current debug enclosure dimensions.

`CityGame.SiegeWaveStarted` switches the simulation to Slow (1x) for every new natural
or forced wave. Remaining fixed-step time is rescaled when that speed changes.
Friendly defenders and death animations request continuous map redraws even when no
attackers, projectiles or repair crews are active. Garrison smoke checks actual rendered
soldier positions between fixed ticks at every speed, frozen pause and city-cache reuse.
Wall purchases invalidate travel/services but preserve road and city-artwork caches when
no natural features are cleared. Feature clearing still invalidates the map artwork.
Stationary wall-pointer updates reuse their preview until money, map or siege state
changes. Collinear enclosure corners are simplified without dropping source wall IDs;
travel relaxation skips blocked-edge work when a candidate cannot improve the route.
Ordinary wall model parts are cached, and coplanar top clipping uses spatial broad-phase
candidates while preserving exact polygon subtraction and original cutter order.

Wall purchases batch topology invalidation across all their span/tower additions and
gate replacements. Defender paths are reset once per purchase; session game-change
notifications wait until the wall action finishes so no intermediate territory state
is exposed. Validation, treasury changes, travel/service refresh, territory, and undo
remain synchronous and atomic.

The renderer keeps stable-ID geometry snapshots, including exterior orientation and
tower ladder dependencies. A net-new addition reuses unchanged wall buffers, picking
transforms and highlights, appending only purchased components. Instance buffers retain
bounded spare capacity (at most twice the used count); animated weapons update both
the rendered buffers and retained transforms. Cap clipping retains ordered spatial
cutters and uploads only changed color surfaces. Changes to existing geometry,
closures that redirect catwalks, gate removal/replacement, damage, terrain, material
upgrades, undo and load take the complete-rebuild path. Tower joins whose ladder
geometry changes also use that safe fallback; these are not claimed as incremental
speedups.

`--wall-network-smoke-test` compares incremental picking transforms, cap triangles,
UVs and actual MultiMesh buffers against fresh full rebuilds, including a first
extension, closure, gates, damage/fire, repair, terrain foundations, gradual/completed
stone, tree clearing, undo and load. Short-click timing measures the first rendered
frame containing visible purchased walls, rather than a preview or buffered pan.
Per-purchase diagnostics separate snapshot, validation/mutation, notification/service
refresh, territory, chained preview/UI, and wall synchronization costs. Service time
is nested inside notification time and must not be added to it again.

### Short-wall optimization measurements

Apple M2 / GL Compatibility, native 1440x900 window, three chained three-cell purchases:

| Fixture | Comparison | Worst click-to-first-render | Wall synchronization |
| --- | --- | ---: | ---: |
| Complex 128x128 wall network | Fresh pre-optimization baseline | 136.1ms | 39.8-41.0ms |
| Same network | Optimized, final repeat | 109.9ms | approximately 5-7ms |
| 256x256 map / 1,000 real residents | Forced full-render control | 310.6ms | approximately 51ms |
| Matched populated fixture | Incremental rendering | 246.8ms | approximately 1ms |

This is approximately 19% less end-to-end latency in the complex fixture and 21%
in the populated comparison. The populated control uses the same new synchronous
simulation code but deliberately rebuilds all wall graphics; it is not an old-build
baseline. Its cloned initial city and purchased wall endpoints match the incremental
fixture. Rendering regenerates three new walls rather than 262-268 existing walls
in the complex scene, or 511-517 in the populated scene.

The first fully checked optimized run measured 107.3ms in the complex scene and
327.5ms versus 263.5ms in the populated control/comparison; retain those observations
alongside the repeat above. The existing **100ms** benchmark remains unchanged.
The final complex result exceeds it by 9.9ms; populated placement still exceeds it
by 146.8ms. Populated service routing (roughly 135-155ms) and territory refresh
(roughly 42-45ms) dominate the remaining cost. Incremental routing and asynchronous
placement were intentionally not included in this optimization.

Graphical geometry equivalence, tower pivots at four rotations/three zooms, timber
thumbnails and narrow-interface/troop-input checks passed. The related unit run
passed 226 checks and skipped seven retired checks; the separate seed-1 starter
battle-duration test still left an attacker alive after its 60-second limit,
including when repeated independently. That test exercises a new siege preset,
not wall purchases; no battle-balance change is part of this work.
The separate large random-map regression measured 17.0ms frame p90 and 251.5ms
minimap latency against 60ms/250ms budgets. Populated SF measured 38.8ms frame p90,
419.9ms minimap latency against 250ms and 123.6ms road preview against 100ms.
Those performance runs remain over budget where stated; no thresholds were weakened.

Before this optimization, the focused three-cell wall regression on Apple M2 / GL Compatibility measured **66.4ms
click handling and 128.5ms click-to-first-render**, against a **100ms** render budget,
in the 128x128 complex-wall fixture at 1440x900. The render check remains over budget
by 28.5ms; it is not a passing performance claim. Geometry, rotated/zoomed picking,
water crossings, atomic undo and actual cached timber thumbnails passed. The separate
960x800 large random-map benchmark passed (16.2ms frame p90, 239.9ms minimap click,
13.0ms road preview). Populated SF retained its existing overruns: 425.9ms minimap
click against 250ms and 111.1ms road preview against 100ms; frame p90 was 39.4ms
against 60ms. No benchmark budgets were relaxed.
`GameSession` forwards/rebinds the cue across game replacement without replaying it
on load or undo; startup and debug restart announce their newly created first wave.
`BattleHorn.cs` synthesizes the original mono horn PCM. Playback shares SOUND mute,
volume and focus behavior with placement sounds, and smoke-test audio remains silent.

Siege simulation remains fixed-step (0.1 siege seconds); at Slow/1x that is five
combat updates per real second. `SiegeEnemy.cs` defines the mutable attacker instance
for both goblins and orcs; `SiegeEnemyDefinition.cs` owns their explicit health,
damage, attack interval, movement speed, body scale and collision-radius rules.
`SiegeEnemies.Get(kind)` provides shared immutable definitions. `SettlementState.Enemies`
owns the live instances; settlement siege code still schedules waves and resolves
combat, while crowd and garrison code retain their distinct movement rules.
Save JSON deliberately retains `Goblins` and `NextGoblinId` through explicit property
names. Founded settlements now require the explicit wall-network schema described
below; the old rectangular settlement representation is not migrated.
There is no general movable base class.

### Editable wall networks and arrival eligibility

Right-clicking an actual enemy model commands the selected living defenders, or every
living defender when the selection is empty. Ground right-clicks still require a
selection for movement; otherwise they retain the ordinary context menu.
Shift-click adds troops and Shift-drag draws an additive, screen-space freehand
selection polygon. It does not pan or edge-scroll the camera; global mouse motion
and release finish the gesture across map boundaries. Esc/focus loss cancels it.
Ctrl/Command-drag remains available for ordinary map areas.
`--interface-smoke-test` checks the drawn circle, additive mixed-unit selection,
gesture cancellation, real enemy picking and selected-versus-all attack orders.

Wall-run clicks on visible towers use the picked tower ID and its exact center,
not the ground cell behind the roof. The originating tower is excluded from
endpoint tower snapping, so the preview can pivot within its footprint. Other
towers still snap endpoints exactly, and ordinary angle constraints are unchanged.
While the pointer is over the originating tower model, its roof-height plane drives
the heading instead of terrain behind the tower; ordinary terrain picking resumes
once the pointer leaves the tower.
The wall-network smoke checks roof clicks and all four pivot directions without
rebuilding city geometry.

Garrison walkways reserve a 1.3-unit inward catwalk with offset actor
positions behind the parapet. Only intact towers have access ladders; gates and
gate wings never do. Ground-to-wall routes use tower ladders, while gate passages
remain walkable at ground level and their decks remain connected wall walkways.
Idle archers face the exterior side of their actual wall or away from the tower's
inward access ladder, not a fixed world heading. Walking, climbing and aiming at
live enemies retain priority. Ground-level idle archers look away from the bailey
center; guards retain their previous idle pose. This is visual-only and adds no
saved facing state or simulation changes.
The interface smoke checks actual visible archer transforms on walls/towers at
four camera headings and both close zooms, live/dead target handling, walking
priority and the existing 12ms actor-update budget. Facing unit tests also cover
all three wall materials and reversed concave extension runs.
Garrison travel uses 3.5 units/sec
and ladder ascent 2.4 units/sec, with nearest-enemy facing and distinct patrol posts.
Shared ladder/rung and climber offsets keep bodies on the exposed side, facing
the rungs. Ground-to-wall and wall-to-ground routes use the same ladder face.
Descent first moves to the ladder at deck height, then lowers vertically before
walking away; ascent uses the same exposed anchor. World-height preservation and
foundation-relative ladder tops handle terrace differences. Climbing limbs reach
toward the rungs, and archers must finish stepping onto supported deck space before
firing. Existing saved paths need no new traversal fields.
Automatic archers distinguish threat acquisition from immediate shot availability:
failure to find a clear in-range shot must not discard a live assigned threat.
Changing threats interrupts stale automatic routes, while explicit player orders
remain authoritative. A screened in-range shot falls through to movement rather
than freezing the archer at its current post. Transient route-target tracking is
recomputed on load without changing save fields. Ladder height steps reserve body
clearance, and already-overlapping saved allies can separate at normal movement
speed rather than mutually blocking forever. Saved archer path validation accepts
fortification-supported walkway cells over water but still rejects open-water
swimming and ground soldiers without an actual bridge.
Automatic archers and guards share weighted threat coverage. Candidate cost is
`(other assigned troops + 1) / ThreatWeight`, with weights 4 for every troll
variant, 2 for Orcs and 1 for Goblins. Equal costs retain an existing assignment,
then prefer distance and stable IDs. This is a preference, not absolute priority:
reachable lower-ranked threats still receive coverage. Explicit attack orders
are never reassigned but count toward coverage; move-ordered units do not.
New archer assignments require an in-range shot or a reachable supported bow post;
guards require reachable interior ground or visible melee contact. Unreachable
high threats do not prevent engaging weaker enemies. Assignments rebalance after
kills and converge when fewer enemies remain.

Peaceful archer patrols reserve reachable supported spots across the connected
wall network: one per corner, then one per remaining tower, then balanced extras
with body-clear deck positions. Connected extension corners qualify; disconnected
fortifications do not. `SettlementPatrol.cs` reuses movement/support geometry and
checks structural routes without treating a passing ally as a permanent obstacle.
Roster, orders, staging, map and topology changes refresh allocation; stalled
routes can retry at bounded intervals. Stable allocations are retained, including
after recruitment finishes; old clustered saves are redistributed on their next
simulation update. Guards keep ground patrols. A breach must not delete living
rosters or invalidate their saves merely because established land is no longer
enclosed.

The first Orc warning stages both rosters along the announced wall, including
both adjacent corners. Guards use interior ground without blocking gate access;
archers use elevated supported posts. Optional `FirstAssaultSide` and
`FirstAssaultLastOrcId` save fields retain that staging through the first assault
and end it when its living Orcs are gone. Missing fields remain valid for old
saves. Later live Orc routes can guide idle staging without filtering out other
combat candidates: weighted targeting and explicit orders remain authoritative.
When combat ends, ordinary corner-first archer patrols and guard ground patrols
resume. The existing one-time warning highlight is unchanged.

The independent Debug garrison smoke avoids running unrelated enclosure
construction checks before exercising troop behavior:

```bash
godot --path godot -- --size medium --seed 42 --garrison-smoke-test --capture /tmp/termcity-garrison.png
```
Ground ladder faces and landings are chosen from the structural enclosed interior,
with lateral offsets at corners and no exterior-ground fallback. Short tower-side
ladders start at connected walkway height and finish at the tower deck; matching
parapet openings, route anchors and climb poses share their geometry. Tower support
takes priority over overlapping wall cells when routing height changes.
Walkway/parapet sides for added walls follow closed structural faces, independently
of drawing direction and mounted-weapon facing. Open runs and internal partitions
prefer the founding town's side; collinear ties use a stable world-axis direction.
Artwork topology and side lookups are cached until walls are added, removed or
structurally changed, so breaches do not flip surviving catwalks. Placement previews
include proposed enclosures. Collision fragments are invalidated with structural
changes, and actor routes use the same inward offset as the rendered decks.
Gate routing distinguishes the ground passage from its upper walkway; only tower
transitions can change between ground and wall travel.
Arrow projectiles include crossed fletching. Transient `SiegeDeath` records capture
arrow, blade, crushing, oil and fall deaths immediately when damage resolves; their
visual playback does not delay combat or become a selectable living actor.
Patrol post IDs survive saves; blocked routes replan without losing body separation.
Corner/junction towers are generated in the atomic run preview and validated before
purchase, with center snapping inside the tower's visible footprint.
The interactive wall tool snaps endpoints to the nearest 0/30/45/60/90-degree
direction in each quadrant, projecting the pointer onto that direction. Nearby
tower centers take precedence for exact connections. Preview and chained purchases
use the same snapped endpoint; low-level construction and saved spans retain
arbitrary-angle support, and gate rotation remains unchanged.
Ladders are part of cached fortification batches rather than per-frame actor work.
Tower parapets/merlons leave a 0.9-unit clear ladder opening. Actor attack poses reuse
`SiegeMotion.AttackSwing` for guards. `ArcherArtwork` instead drives an articulated
bow pose from the unchanged 0.3-second windup and 1.5-second interval: raise/draw,
cheek anchor, release, follow-through, lower and nock. Two-link arm solves preserve
upper/forearm lengths, bow tips keep a constant-length two-segment string taut,
and the held fletched arrow disappears at committed release. Archers use
a side-on shooting stance; ready/reload hand paths stay outside the coat.
Regression checks test the actual oriented arm/hand box volumes against the
rotated torso and head through 1,501 poses, permitting only the shoulder socket
attachment. Limb thickness, hand and torso sizes are shared with the renderer.
The bow is stowed
while climbing; reduced motion uses static ready/aim poses. Actor batches and
correctly oriented pick boxes update without rebuilding fortifications or changing
damage, target assignment or projectile snapshots. Native close-ups cover all
phases at two camera angles and zooms. Death rendering reuses enemy
species artwork and preserves troll appearance seeds; slain actors are removed from
picking immediately while the bounded transient death list animates. Both map views
draw these effects. Clipped extension top faces use Godot's front-facing winding,
covered by native checks and inspected deck captures.

`World/WallNetwork.cs` derives a canonical planar graph from actual straight spans:
T/X intersections split connectivity, collinear overlap is deduplicated and bounded
faces determine enclosure. The road tool's anchor/preview/confirm/re-anchor workflow
is reused, but roads' logical staircases, smoothing, partial placement, bridges and
terrain grading are not reused as wall simulation.

`SettlementNetwork.cs` owns stable wall IDs, spatial and traversal caches, founding
wall affiliation, established territory and gate-accessible arrival eligibility.
The original `Bounds` records founding provenance; `WallBounds` is only a spatial
envelope. Growth does not use either rectangle as membership. Whole plots must fit
eligible land and actual clearance. Separate retained territory keeps existing
households and paths alive after a breach. Both new development and vacancy refilling
are gated; births and ordinary non-settlement scenarios are unchanged.
`SettlementState.ReachableLand` supplies the shared physical flood for both
territory discovery and construction access: traversable gates and nearby dry
land are included, but only connected closed interior cells become arrival plots.
`SettlementAccessError` keeps proposed construction from stranding existing buildings
and identifies the blocked building/coordinates. Its baseline graph, per-plot
checks and inspection results are cached against city and wall-topology revisions.
The production candidate scoring is unchanged. Diagnostics reuse its type, footprint,
land-value and clearance rules without sorting every world candidate on each hover.
`GrowthDiagnostics.ForSettlementCell` describes empty unzoned land, and zone/report
vacancies are nonoverlapping, access-checked sites rather than merely geometric fits.
Eligible-site text explicitly leaves final selection to normal demand/site value.
The settlement native smoke checks real extension households and the existing status
tooltip and Growth report at 960- and 640-pixel window widths.

`CityGame.WallConstruction.cs` validates whole purchases before editing anything,
quotes net new Euclidean length and builds bounded-length health sections.
New runs cost 20/80/200 gold per unit at wood/stone/reinforced tiers, matching the
base cost plus existing material upgrades. New sections during a purchased upgrade
use its target tier and do not reorder the stable upgrade queue. Gate cutting and
removal are temporarily rejected during that upgrade. Explicit gates fit actual road
widths/angles; road gates remain explicit and water-wall crossings use shallow
arched bases with submerged iron grates rather than road bridges.
Corner and non-collinear junction towers are automatic, previewed and checked for
clearance before placement. The one-unit rendered span cadence matches established walls.
Removal and insertion preserve surviving damage percentages rather than healing
neighbors. Undo and new-format saves preserve IDs, geometry, material, health groups,
repair latches and established land. Unsupported old rectangular settlement saves
fail explicitly and are left unchanged.

`WallDamageLayout` supplies oriented collision, fragments and terrace foundations.
`WorldScale.HasDryTowerFootprint` checks the complete tower base, not only its
anchor cell. Terrain-aware founding and junction previews/purchases omit wet tower
sites while retaining ordinary walls and their closed network spans. New saves
preserve these terrain-dependent founding flags; existing saved water towers remain
loadable and are not silently removed or refunded.
`WallWaterLayout` trims only wet body columns to a shared shallow watercourse arch
(0.3-unit clearance plus up to 0.55-unit rise), preserving dry artwork. Iron bars
extend 0.65 units beneath logical water level and use the same rendered/pickable
fragments as collision. Dry-bank foundations must not fill these openings.
Founding previews, purchases and debug searches use the selected gate side and
dry physical flood when choosing starter footprints; no starter may seal the
others' sole route. Water-wall saves retain logical terrain, without new save fields.
These crossings do not add traversable water, erase terrain, change flow, add roads
or bypass bridges; gate placement remains land-only. Territory and construction
reachability seed their physical flood from actual usable gates, not an arbitrary
central island that can become separated by a river.
`SettlementFootbridges.Advance` runs through `TravelRoutes.WearPaths`, with a
resident requirement and one construction project at a time. It searches from
gate-reachable dry land to another bank in the same connected closed region,
limits cardinal crossings to eight water cells and checks every segment against
actual fortifications. The four-increment project preserves water and requires no
gold or RNG. Completed spans integrate with pedestrian/service routes, garrison
ground travel, path wear/links and arrival reachability without becoming roads.
Optional `Footbridges` save records validate alignment, length, terrain and progress;
older saves load with none. Perspective and overhead render planked decks, railings
and supports, with a workman at the unfinished edge. Actual elevated deck triangles
provide perspective ground picking. Inspection reports progress or blocked work.
Rendering, full-surface ray picking, selection, previews, overhead view and minimap
use that geometry. New coplanar top faces use disjoint convex subtraction through
the existing zone/footpath polygon helper; they do not overlap at T/X joins.
Selection uses the picked stable section ID, including intersecting spans sharing
a map cell. Mounted oil/stone stations follow the actual span tangent; R flips a
new span's defended face. Route/spatial caches invalidate on structural passability
changes, not camera movement, selection or cosmetic repair animation.

```bash
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'FullyQualifiedName~WallNetworkTests|FullyQualifiedName~WallConstructionTests'
godot --headless --path godot -- --seed 42 --size medium --wall-network-smoke-test
godot --path godot -- --seed 42 --size medium --wall-network-smoke-test --capture /tmp/termcity-walls.png
```

The native check exercises on-screen diagonal picking across four rotations and
three zooms, selection without geometry rebuilds, actual angled gates, connected
enclosure eligibility, atomic chained purchases and save/undo round trips.
The default render cap is 60 FPS. On this workstation the 30-FPS cap produced
large presentation stalls despite actor/simulation work remaining within budget;
raising the cap avoids those stalls without reducing 2x supersampling or 8x MSAA.
Explicit `--fps` values are still honored. Untextured static box colors share
lit/unlit instance-color batches; textured materials keep their existing sampling.
`SiegeMotion` interpolates transient previous/current
goblin positions and gait per rendered frame. Projectile traces extrapolate their
known flight between ticks and orient to the trajectory. Rendering neither advances
simulation RNG nor changes travel speed/damage. Committed arrows, ballista bolts and trebuchet shots
use a shared visual elapsed clock for windup release and travel in both map views.
Flight progress excludes windup and post-contact retention and clamps at contact;
damage and cancellation remain authoritative fixed-tick simulation decisions.
Legacy projectile lifetime progress and oil/rock drop animation remain separate.
Actor transform lists and MultiMesh
instance buffers are reused; motion does not rebuild city or wall geometry.
Reduced-motion settings retain location/facing but suppress bob, sway and hammer
animation.

`SettlementRepairs.cs` owns deterministic continuous civilian repairs, including during
combat. Health per siege second is `12 + 0.24 * population + 9 * occupied industrial
buildings + 3 * active service buildings`, or zero without residents. Breaches precede
lowest-health-percentage sections; combat may redirect the crew when another section
is at least ten percentage points more urgent. Otherwise targets are completed.
Combat-ending ticks do not immediately heal a fresh breach. Optional saved repair
target/phase fields preserve old version-2 compatibility; fractional wall health
stores work already done. Paid/debug repairs clear stale crew targets.
Walls remain visually intact above 85% health. Civilian repair animations begin at
80% health and stay enabled for that shared section until it reaches 100%, even
after it recovers above 80%. Repair, bucket-water and masonry crew visuals are
suppressed in both map views until the enemy list is empty; health recovery,
repair priorities, paid upgrades and their progress still advance during combat.
After battle, the saved latch shows the crew until the wall is fully repaired.
This visual latch is saved; older saves infer it from
current health. Actual damage, repair speed, priorities and costs are unchanged.
Both perspective and overhead views use `VisibleRepairingWall`; `RepairingWall`
continues to report actual repair work, including invisible minor repairs.

`SettlementGarrison.cs` owns finite building rosters, gate-based ground/wall routes,
automatic combat and temporary move/attack orders. Both recruiting buildings unlock
at 100 souls and have four-person rosters (global limit 64), two-second recruitment
and eighteen-second casualty replacement cooldowns. Troops never teleport to walls;
archers climb tower access ladders and soldiers defend inside gate/corner posts.
Recruitment uses perimeter cells connected to the gate, not isolated pockets beside
large barracks. Soldiers wait outside the opening so archers retain ladder access.
`GarrisonCommands.cs` keeps transient selection separate from persisted simulation
orders. `PerspectiveCity.Garrison.cs` batches troops and uses actual model-volume
picking with building/roof occlusion. Selecting actors never invalidates city geometry.
Selected goblins, Orcs and all troll species show their live species name and health
in the bottom-left inspection status. Selection is transient and clears on map
selection, Escape, death or replacement of the city; defenders retain their ordinary
move/attack commands.
Perspective wall/tower selection frames enclose their rendered geometry rather than
logical anchor cells. Equipped towers frame and tint the mounted weapon itself;
its frame sits at the model's actual base, not above the tower crest. Placement
ghosts share these bounds and mounting origins. Building/yard and road outlines
retain their existing footprint behavior; changing selection does not rebuild city
or fortification meshes.

`SettlementOrcs.cs` plans one deterministic, reachable first-Orc approach from the
250-person milestone. The next natural wave includes Orcs as soon as the permanent
250-person milestone is reached; short warning time does not defer the introduction.
Only that first approach is forecast. Scout scheduling never gates species eligibility,
and pending saves with the former extra-wave delay are normalized when play resumes.
Explicit forced Goblin waves remain Goblin-only, and physically blocked approaches
still wait rather than spawning Orcs through obstacles or beyond the map.
Forecasts and actual Orc spawns share body-clear route checks through the first
physical melee contact. A corner tower cannot be walked through or counted as a
different wall frontage. Orcs need the normal six-cell exterior arrival run and
do not use the legacy near-wall fallback; Goblin/Troll behavior is unchanged.
Clear breaches remain valid routes. Pending forecasts are rechecked after load
or map/topology/wave changes, without re-running the scan every frame. Invalid
forecasts are revised to a viable side or withdrawn with a scout report; saved
wave/side fields keep their existing format.
Explicit species control health, body radius, movement and attack damage independently
of population tier; mixed swarms remain under the existing 120-attacker cap.
`SettlementGarrisonSave.cs` and optional fields in `SettlementSave.cs` validate
unit ownership, rosters, commands and species and retain legacy version-2 defaults.
`SettlementGarrisonTests` proves three-seed early recovery, directional Orc defense,
rosters, walking/climbing, orders, warnings and exact persistence.
`SettlementUpgrades.cs` purchases the whole next material atomically, then upgrades
one shared wall section every three siege seconds. Stone unlocks permanently at 250
souls and reinforcement at 1,000; another purchase is rejected until the current pass
finishes. Existing material and health remain until each section completes, at which
point its shared health pool is rescaled once, preserving damage percentage and
breaches (200/450/800 health per constituent cell). Construction runs without residents, before the first wave and during
combat, but pauses with the simulation. Optional save fields retain target material,
completed sections and fractional work; undo restores both payment and construction.
`MaterialFor`/`ProgressFor` and cell-query equivalents expose mixed-material walls.
`WallUpgradeArtwork` adds broad courses of stone over the existing wood, followed by
iron reinforcement over stone, using the existing box batches and damage fragments.
Graphical smoke captures walking and repair poses and measures active
120-goblin frames against 12ms actor-update and 60ms frame-p90 budgets.

`SettlementCrowd.cs` shares the exterior route flood between spawn planning and
legacy-save separation. Spawn positions honor tier-scaled body radii; requested
debug counts fail atomically if the reachable ground cannot fit the swarm.
Front-to-back, ID-stable movement uses swept relative-motion checks, including
already moved neighbors, so interpolated poses cannot cross or overlap at merges.
Blocked units wait behind the front rank instead of attacking through it. The
saved route format remains unchanged; load-time migration preserves attacker IDs,
health and simulation RNG while relocating old overlaps onto valid exterior routes.
The melee pose has strike/recoil/rest/wind-up phases, with a pivoted weapon and body
lunge rather than a barely visible arm oscillation. Existing damage cadence is unchanged.

`WallDamageLayout` provides deterministic, health-banded wall/gate/tower fragments,
ragged crests and rubble. Perspective picking uses those fragments rather than an
intact wall box. Mounted weapons follow the surviving crest. Flames begin strictly
below 0.33 health fraction (including zero), are batched separately from static walls,
freeze when paused and retain a static pose with reduced motion. Repairing extinguishes
them immediately at the threshold; fire has no simulation damage or spread.
Repair civilians use doorway-scale bodies with separate hammer handles/heads and
carried boards. The settlement smoke checks damage stages, all three materials,
fire thresholds, pause/reduced motion, restoration and city-geometry cache reuse.

`GameSession.AreaOverlaysVisible` defaults false; VIEWS / AREAS enables vacant zoning
and service/weapon guides consistently in perspective, overhead and the minimap.
Simulation zoning and independently selected map tints are unchanged. Smoke fixtures
that inspect area geometry explicitly opt in.

Debug Siege searches capacity-checked 64/56/48/40/36-cell enclosures unless dimensions
are explicitly requested; explicit dimensions never silently fall back.
`WallSections` enumerates shared-health anchors; `Walls` retains per-cell geometry,
picking, reservations and weapon slots. Original ordinary runs share health across three cells and stop at every
tower/gate. Shared pools preserve the combined per-cell strength. New saves validate
matching member health; legacy saves preserve the lowest member health fraction so an
existing breach never disappears. Repair/upgrading work converts shared pools once.
The map draws one health bar per section and raises selection/defense placement outlines
above tower crenellations. These indicator changes do not rebuild city geometry.
Selected structures reuse their exact box transforms and roof triangles in a separate,
depth-tested warm highlight pass. This covers the full selected wall span and mounted
weapons without tinting neighboring structures or rebuilding city/fortification batches.
The pass is cached until the selection or relevant geometry changes. Overhead sprites
and wall sections use the corresponding subtle tint; placement previews remain separate.

Blocked goblins periodically search collision-clear cardinal exterior routes to a free
wall frontage. Actual melee reach, rather than only the original route destination,
determines their attack target. Swept body separation remains authoritative during
movement; reroute cooldown and longer exterior routes persist in siege saves.

Replace `godot` with the executable path when needed; on macOS it is often
`/Applications/Godot_mono.app/Contents/MacOS/godot`.
Game arguments follow `--`; [GodotOptions.cs](../godot/GodotOptions.cs) handles seed, size,
load, frame cap, map dump, help, smoke tests and optional screenshot capture.
`--help` and `--dump-map` are headless-safe and exit without creating music/UI resources.

All projects target `net10.0`, with nullable references and implicit usings enabled.
Godot uses `Godot.NET.Sdk/4.7.2`; tests use VSTest/xUnit v2.
Generated `bin`, `obj`, `.godot` state and export outputs are ignored.

`GameSession.MapNavigationAllowed` distinguishes non-modal placement tools from modal
confirmations. Building, road and traffic-circle tools retain camera zoom/rotation/tilt,
minimap navigation, pan gestures, overlays and edge scrolling; ordinary dialogs and
confirmation previews still block map navigation. During building placement, brackets
rotate the building and Shift+brackets rotate the camera. Middle-drag takes precedence
over pointer-following placement, so panning cannot move or accidentally place the ghost.

`--debug-starter` is an opt-in Debug-only preset for a new random map. It places one
Town Well and one Woodlot within 32 cells of the generated starting spur, designates
18-20 complete realistic residential plots nearby, suppresses the initial guide, and starts
unpaused. The initial camera is centered on the residential rectangle and retains that
focus through startup viewport/layout changes without adding a selection highlight.
User camera navigation releases the startup target. It uses normal placement checks and
gold costs without altering terrain or roads.
An invalid or cramped setup fails explicitly; the flag cannot modify a loaded city or be
combined with populated scenarios, map dumps or smoke tests. Normal startup stays unchanged.
After validation finishes, launch the final passing Debug build without smoke-test arguments
so the interactive game remains open rather than exiting with the validation process.

### Windows rendering and resize checks

[project.godot](../godot/project.godot) keeps the Compatibility renderer, with
`rendering/gl_compatibility/driver.windows=opengl3_angle` selecting ANGLE / Direct3D11 only on Windows.
This reduces the native OpenGL resize stall without adopting the Vulkan path that crashed during
live resizing. Other platforms retain their default drivers. Native OpenGL remains an explicit
fallback via `--rendering-driver opengl3`, placed before the game-argument separator.

The graphical smoke test checks large resizes and maximize against a one-second budget on ANGLE.
[WindowsResizeSmoke.cs](../godot/WindowsResizeSmoke.cs) also exercises native enter/sizing/paint/exit
messages and window-size changes on the test window, without moving the system cursor or interacting
with other applications. It restores the original bounds, checks city data is unchanged, and emits
`TERMCITY_WINDOWS_RESIZE_OK`. Headless and non-Windows runs skip native Windows calls.

These checks do not establish stability during physical edge dragging. Rapid live resizing has
also been reported to hang with ANGLE: mouse input remains captured, opening Task Manager releases
the mouse, but the game stays frozen. No managed exception or graphics-reset event was found in
the observed logs. A native rendering/driver wait and a UI-thread deadlock remain unconfirmed
possibilities. Avoid aggressive automated GUI stress on an interactive desktop.
For diagnosis, keep the frozen process alive and use Task Manager's Details view to create a
memory dump of the specific game process before ending it. Keep dumps local; they contain process
memory. Inspect native and managed thread stacks rather than inferring the cause from resize timings.

## Architecture

| Area | Responsibility |
|---|---|
| [src/TermCity.Core](../src/TermCity.Core) | World layers, content registries, terrain generation, scenarios, simulation, session workflows, rendering data and saves |
| [godot](../godot) | Scene tree, desktop UI/input, map/minimap drawing, display settings, synthesized music and engine integration checks |
| [tests/TermCity.Tests](../tests/TermCity.Tests) | Simulation, generation, scenarios, persistence, session, rendering, grid/options and music synthesis tests |

`CityGame` owns rules, money, calendar, growth and lazily cached statistics/road service.
`CityProgression` owns the shared City Grew! stages, permanent building gates, unlock descriptions
and next-step coaching. `CityGame` records peaks independently of any session or guide setting.
`GameSession` owns cursor/camera/selection, zoom level, messages, previews, undo, prompts,
save/load/autosave guards and feedback. It has no Godot dependency.

Accelerated default-rules gameplay regression tests cover two seeds on each random map size,
using paid actions, no loans and a mid-play save/load. They take up to about an hour on large maps, so CI
skips them (`--filter "Category!=Playthrough"`); run them locally. The target is the final 10,000-person milestone:

```bash
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj --filter 'Category=Playthrough' --logger 'console;verbosity=detailed'
```

See [the playtest report](PLAYTEST.md) for measured game-time equivalents and balance decisions.

`Main` connects session events to desktop controls. It remains one Godot `Control`, attached through
[Main.cs](../godot/Main.cs) in [Main.tscn](../godot/Main.tscn), with cohesive partial files:

| File | Responsibility |
|---|---|
| [Main.cs](../godot/Main.cs) | Startup, frame processing, session notifications, focus, shutdown and smoke-test orchestration |
| [Main.Audio.cs](../godot/Main.Audio.cs) | Streaming music, placement sounds, volume/mute controls and audio resource cleanup |
| [Main.Display.cs](../godot/Main.Display.cs) | Preferences, font/window sizing, layout, HUD/status refresh and city-name editing |
| [Main.Dialogs.cs](../godot/Main.Dialogs.cs) | Menus, modal construction/sizing, tabbed prompts, choice help and dialog keyboard navigation |
| [Main.Construction.cs](../godot/Main.Construction.cs) | Left construction icon rail, category catalogs, statistics/description panels and placement entry |
| [Main.Input.cs](../godot/Main.Input.cs) | Gameplay keys, pointer/trackpad/pinch gestures, selection, panning and edge scrolling |
| [Main.Effects.cs](../godot/Main.Effects.cs) | Effect setup, level/celebration controls and redraw scheduling |

Fields live beside their owning behavior. The partials share the same scene instance and private state;
they do not introduce extra nodes, controllers, subscriptions or a second lifecycle.
`LoadPreferences`/`SavePreferences` handle the shared display/audio settings file.

`TerminalMap` and `TerminalGrid` implement the terminal-styled cell display; they are Godot presentation
components, not a console frontend.
`CityPanel`, `CityMinimap`, `CitySplit` and `TerminalFrame` provide sidebar sections, overview,
bounded divider resizing and closed double-border rendering. `SidebarReport` supplies the reduced
City table and semantic colors without a Godot dependency. `CityReport.ZoneDetails` is shared by
Demand hover tooltips, weekly/growth reports and the full-rules health report.

## World and content

`GameMap` stores flat layers indexed by `y * Width + x`: terrain/feature/road type/building IDs,
road booleans, zones, households and path wear (0-12). Sparse road/zone index sets keep simulation work proportional
to built cells. `SetRoad`, `SetZone` and `ClearCell` maintain them; loading rebuilds them.
`BuildingType.Width/Height` define player-placement footprints. `PlanBuildings` tiles the
expanded selection, rejecting blocked buildings atomically. `GameMap.BuildingFootprints` and a
sparse cell-owner lookup associate occupied cells with one building; `ServiceCells` contains
only its anchor. Zoned buildings also register complete footprints: new homes
normally use 1x1 plots, as do stalls and workshops in both rule sets. Deterministic
appearance-size preferences are 80% 1x1, 8% 1x2, 8% 2x1 and 4% 2x2, falling back to
the registered minimum when space is constrained. All default zoned building tiers fit
within 2x2; civic footprints remain independent.
Woodlots render as fenced, packed-earth firewood yards rather than tree groves.
`WoodlotArtwork` supplies shared block-built geometry with low rails, a front gate,
stacking pallets, and stepped cords with contrasting bark and cut ends. The canonical
menu thumbnail and city renderer use the same model; the legacy pixel sprite also
shows the fenced stacks. Footprints, clean-fuel capacity, costs and upkeep are unchanged.
Residential models choose a stable coordinate-derived medieval archetype: thatched cottage,
timber gable, stone croft, burgage, longhouse or weaver's house. These share coarse
clean half-timber walls, with three or four thick
roof steps assembled from small blocks instead of smooth pitched faces or long strips.
`HouseAppearance.Parts` supplies four square corner posts, joined sill/floor/eaves beams,
intermediate bay posts and uniform recessed plaster panels. Frame pieces meet without
intersecting duplicate trim; no diagonal braces or checker-colored wall tiles are used.
The frame and openings share `HalfTimberFacade` and `HouseBlock.WallPanel` so doors/windows
remain centered and clear the structural beams. Broad corner timbers, floor bands, square windows
and a single door replace fine trim, dormers and awnings.
One-cell houses remain the norm; 1x2/2x1 homes extend their roof ridge along the long axis.
Selected 2x2 styles add a low adjoining block around an open courtyard corner, without
overlapping wall masses. Larger plots never increase wall height or roof pitch.
Their open plot margins contain dirt approaches; facade layout favors a few readable windows
and structural beams over dense repeated trim. `HouseYardLayout` assigns each home one whole
adjacent free cell for a decorative lawn, stepping path, flower bed, vegetable bed and low fence.
Assignments are deterministic across saves, mutually exclusive, and stay within designated
residential areas; unzoned land, water, trees, other buildings, actual road shoulders and
existing footpaths are never chosen for a new yard. Established gardens take priority over
later footpaths: general wear cannot cross them, and shared ribbon/cap polygons are
clipped at the garden edge. An owned front garden may provide its existing central
walkway to the house entrance; beds remain in their assigned cell, clear of the
0.56-world-unit aisle and matching fence gap. Any sideways approach bend occurs
inside the building plot rather than across the planted beds. Old wear is retained in saves
without being painted over a garden. The yard's graphical
elements pick their owning home. Yards do not change saved footprints, growth rules, capacity
or household counts. The first chosen yard cell is stored per house in `GameMap.HouseYardCells`
and persisted in saves; it never relocates when neighbors, roads or footpaths change. A blocked
or dezoned yard hides rather than selecting a replacement cell, and can return only in its
original location. House upgrades preserve the assignment, even if an expanded footprint
covers it; demolition clears it. Older saves without yard metadata assign a fixed cell when
first rendered. Their grass replaces the vacant-zone overlay only in
the rendered yard cell. Yard assignments are cached until the map changes; camera movement
and selection changes do not recalculate them across the entire city.
The reverse yard-owner index makes route blocking constant-time; assignment, demolition
and garden visibility changes invalidate cached footpath routes.
Natural footpaths avoid all hill terrain, including level terrace tops and collapsed hill
remnants beside roads. Ordinary roads on hills and general service travel remain usable.
Footpath routing also rejects off-road transitions across height changes above 0.12 world
units and checks actual road-deck heights before choosing an approach. Legacy worn paths
are hidden on hills and split at those barriers instead of reconnecting across
cliffs. The renderer also rejects any ribbon triangle whose sampled ground-height span
exceeds that threshold, preventing steep strips on road cut walls or terrace faces.
General service travel, road grades, bridges and terrain geometry remain unchanged.
Selecting any part of a house or its yard outlines the whole property, including all cells
of larger saved houses. `HouseYardLayout.Outline` removes shared cell edges, so the highlight
is one closed outer boundary rather than separate boxes or an oversized bounding rectangle.
Dragged area selections retain their rectangular outline; property highlights only rebuild
the indicator layer, not city geometry.
Households live only at the anchor,
and population, jobs, taxes, supply demand and pollution count each building once.
Land is walkable without road access. Area effects originate at the anchor.
Saves retain explicit footprints; service buildings without them are rejected on load.
`BuildingType.FootprintAt` swaps width/height for odd quarter-turn rotations. Placement validation,
quotes, hill costs and confirmation all use that same rotated footprint. Building previews retain
their rotation when moved; `[` / `]` rotate the building while placing it, not the map.
Nonzero orientations are stored sparsely by anchor in `GameMap.BuildingRotations`, persisted as an
optional save dictionary and removed with the building. Older saves default to zero rotation.
The perspective renderer rotates the original model around its footprint center without stretching
its proportions; roofs, walls and picking volumes share the transform. Foundations follow the
actual occupied terrain cells, not the rotated model's local coordinates.

Vacant zones reuse `CellRenderer.RenderZone` for the foreground/background palettes,
reachability dimming and active map-overlay tint, not its legacy shade glyph. The perspective
renderer uses a cached 8x8 texture with solid 2x2-pixel checker blocks, repeated as four checks
per world-cell side. Terrain-following surfaces are independent of cursor selection or placement
indicators. Colored checks alpha-blend at 58% opacity; alternating gaps remain about 11% opaque.
The terrain stays visible without stippling, fine linework or a heavy solid backing.
`ZoneOverlayArtwork` shares the legacy colors, including dimming and overlay tints. Occupied and
dezoned cells have no vacant overlay.
Perspective smoke checks real zoning/dezoning actions, saved designations and actual marker pixels
at all four camera headings, including raised plots; it also checks building fronts and picking
at all four building orientations.

Residential perspective models use `HouseAppearance.At` for deterministic palette, proportion,
roof-direction and stepped-roof variants, with occasional low wings and broad chimneys.
Visible residential walls use dark, flat-colored half-timber frames and a single muted
plaster color per house. Square posts span the wall height and horizontal beams stop
at the posts, keeping corners clean. Roofs retain batched cuboids about 0.20 cells across,
with muted whole-block color variation rather than repeating fine textures on each face.
The existing 32x32 plaster, roof and masonry textures remain available to workplaces and foundations.
Broad timber corners/eaves, square window surrounds and solid roof
caps provide depth without fine facade framing. `BlocksOnPlot` supplies the same wall masses
to the model and hillside foundations; every roof step uses batched boxes. Picking uses the
actual recessed panel, frame and wall-core boxes, plus the exact solid union of each tiled
roof course. Step edges remain accurate without ray-testing every roof voxel; openings
and yards retain their own picking volumes.
Appearance depends on anchor and
building level, so reloads do not shuffle house variants. Other building models are unchanged.
On hills, residential foundations follow the inset house/wing walls and intersect the existing
terrain as stepped stone lower levels. Deep exposed sides have lower windows; shallow changes
remain masonry footings. Houses no longer receive full-plot wood supports or floating plot slabs.
Upper floors remain level, and terrain, simulation capacity, placement rules and costs are unchanged.
Perspective smoke checks lower-level support/picking at four camera headings, rotated foundation
alignment and unchanged terrain heights, and captures a hillside house for visual inspection.

`GameContent` combines terrain, feature, building and road registries. Types receive compact byte
IDs in memory; saves resolve their names instead, allowing content reorderings.
Default zones are Residential, Commercial and Industrial. A residential household stores adults,
children and seniors; occupied businesses contribute income but not population.

`CellRenderer` composes terrain, features, zones, buildings and roads into glyph/foreground/background.
The road tool (T) draws a line at any angle between its two ends (`CellLines.Between`, four-connected so the simulation can follow it); the preview carries just the cells on the line. Straight runs and diagonal staircases are drawn as straight lines (`RoadCurves.Straighten`), rounded only at real bends.
Road clicks commit the hovered stroke immediately on left-button press; they do not reset
its anchor and silently discard the shown segment. A held click can preview a further
extension, committed on release only when the pointer moved; an ordinary click release
never charges or commits twice. Pointer motion and arrows update the endpoint and Enter
supports keyboard construction. Successful strokes retain `_lineRoad`
and reanchor at the endpoint instead of exiting the tool. Esc/Finish exits; right-click resets
the starting point without leaving placement. Fixed traffic-circle placement also persists.
Each stroke remains separately quoted, validated and undoable; failed placements keep
their preview/type and spend nothing. Area-road confirmations transition into the same tool.
Pointer starts/endpoints over a highway's outer selectable band resolve to its actual
logical road owner in construction mode only; ordinary cell selection remains physical,
and keyboard endpoints can traverse both bands without snapping back.
Nearly parallel strokes within the combined rendered road widths and shoulders are
rejected atomically (15-degree tangent tolerance). Collinear endpoint extensions and
logical-cell upgrades are exempt; regular crossings and angled junctions remain legal.
The explicit rejection reason is shared by live previews and `BuildRoad`.
Paused undo is available while building and resumes the selected tool after restoration.
Perspective previews extract the prospective network with planned road overrides and reuse
`RoadMeshLayout` plus `RoadSurfaceMeshes` for actual type colors, pixel textures, raised
borders, widths, joins, grades and bridges. Preview meshes are indicator-only, slightly
raised/translucent, and limited to affected visible chunks; they never modify map layers
or rebuild the existing city while the pointer moves.
Preview triangles are clipped to prospective footprint/type/bridge/grade differences
from committed roads. Existing road meshes are never repainted as translucent ghosts;
no-op strokes emit no preview geometry. The preview remains separate from picking.
Planning is computed once per endpoint update and reused for the quote. Prospective
clearance and preview mesh generation retain complete nearby paths while excluding
distant roads, so joins/curves stay exact without rebuilding unrelated city geometry.
The perspective performance smoke measures live road-preview endpoint updates against
a 100 ms budget and asserts that they do not rebuild existing city geometry.
Roads may cross and branch but never run side by side: `CityGame.PlanRoad` (via `RoadRules.Plan`) lets a stroke touch another road only where it runs into it or at its two ends, and never completes a 2x2 block of road (`CompletesRoadBlock`). It also refuses a new junction within `RoadRules.JunctionSpacing` cells of an existing one. Quote, build and the line preview all use that plan, generated maps lay highways first and then the street grid as strokes through the same rules (`CityMapGeometry.Grid`), `RoadSeparation.RemoveFragments` drops tiny detached scraps, and `RoadSeparation.Apply` clears generated roads that form 2x2 blocks (dropping the humblest road where the neighbours stay connected). In `RoadCurves`, `Pair` decides which arms of a junction run through it (highest rank first, then straightest), so a diagonal highway stays one curve; `Join` moves each branch end onto the host curve and, unless the meeting is nearly square, `Ramp` bends it into a quadratic merge along the host. Zoomed out, `RoadVectorLayer.Weight`/`Opacity`/`Shown`/`RankOpacity` thin, fade and (at stride 8+) drop the minor roads, while top-rank highways always keep a narrow bed and a thin double line (`HighwayBedHalf`, `HighwayLineOffset`, `HighwayLineHalf`) so they stay a backdrop.
At four-way junctions where pairing would create opposing bends, only the main road continues
through; the other arms join it as branches. This prevents independently rounded curves from
pulling apart despite connected map cells. `RoadCurveTests` flood-fills the rendered road pixels,
including seed 1981019679 at 160x96 in normal and 2x zoom, to check visible connectivity.
Road glyphs use cardinal neighbor masks; generated highways leave each interchange along a compass arm and then run at any angle as four-connected staircases.
Every road type is drawn as a curve when `vectorRoads` is passed (any zoom level, no overlay; `BlockSampler.VectorRoads`
and `TerminalGrid` pass it, the minimap does not and stays glyph-based): the cell is left blank and
`RoadCurves`/`RoadVectorLayer` supply the picture. Network connectivity remains four-connected.
Where roadbeds overlap at a merge, the higher-ranked road retains its pavement and
line colors, including the smooth-min fillet and antialiased outer edge. This prevents a minor branch's smoothing ramp from painting a gray
patch along its highway host; equal-ranked overlaps still use the nearest path.
`Rendering/RoadGeometry` is built once per `RoadNetwork` revision from shared `DrawnPaths`, also reused
by `RoadVectorLayer`. It reserves whole cells intersecting the actual `RoadMeshLayout` surface
polygons, for every road type including disconnected roads. `RoadType.WidthCells` is one for dirt,
two for cobblestone and four for King's Roads; shoulders, cut/fill edges, bridges and picking derive
from those same offsets. Width is measured in square world-cell space, not terminal pixels.
King's Roads use tangent circular arcs with an eight-cell target radius in world space for broad highway bends; minor-road
curves use a compact one-cell radius so right-angle local roads retain straight, grid-aligned legs.
Mesh simplification pins transitions into and out of cardinal straight sections; it cannot
replace a straight leg with a slightly diagonal chord that blocks neighboring plots.
Junction joins intersect the host along the approaching road's existing cell axis instead
of moving a whole straight leg toward the nearest point on an angled host. If the host
does not cross that axis, only a short junction connector may leave it. Cell-straight mesh
sampling preserves constant coordinates exactly, avoiding floating-point spill into a
neighboring cell's placement clearance.
Intersections remain ordinary intersections. Automatic growth and player building placement reject
partially occupied cells. Road placement and widening check prospective curves against occupied
and zoned plots before quoting, previewing or committing. Existing overlaps are not demolished.
Even-width centerlines are offset by half a cell so straight road edges coincide with whole-cell
boundaries. A single highway anchor covers a 1x2 or 2x1 band, not three partly occupied bands.
Road costs, upkeep and weekly capacities (6/20/60) remain per centerline cell, not per width cell.
`RoadCurves.Extract` traces roads as paths that run straight through junctions, cuts a path where the road type changes
(`SplitByType`, so each stretch keeps its own colours and the seam is hidden by a one-cell overlap), then straightens,
resamples and smooths each (a branch reaches the host centreline so three-way junctions merge without a separate end cap). `RoadVectorLayer`
rasterises 16x16-cell chunks with a signed-distance field per path, blended across paths with a smooth minimum so
junction corners get fillets. The bed is opaque out to the outer edge of the two edge lines (`BedHalfWidth`), so it hides
terrain glyphs and water; chunks are drawn after the terrain glyph pass. When zoomed out (`Stride` > 1) chunks are
rendered at `Render(chunkX, chunkY, scale, stride)`, covering stride times the map area at the same on-screen width.
Ambient sprites (cars, walkers) follow the drawn curve: `IAmbientWorld.Snap` projects their position onto `RoadGuide`'s
segments (filtered with `FollowRate` so they glide), and speeds are the `AmbientLife` `*Speed` constants.
`godot/TerminalMap` caches the chunks as textures and draws them between the cell backgrounds and the glyphs.
Player road strokes accept water cells and render them as bridges instead of
silently skipping the crossing. Buildings and zones still reject water.
Interrupted water-crossing strokes quote zero cells and show no valid preview cells;
confirmation reports the blocked bridge without charging or laying a partial crossing.
Selected areas that cross water use the selected start/end cells as one ordered
road stroke, not a rectangle of scattered road strips. Preview, quote and commit
share that stroke. A blocked water crossing fails before changing any cell or
charging gold; it never builds disconnected approaches or an isolated deck.
Perspective crossing previews outline the stroke cells, with blocked cells marked
red, instead of outlining the entire enclosing rectangle.
Local roads retain their normal colors and speed benefits.
`BlockSampler` chooses representative visuals for coarse zoom, prioritizing buildings, zones,
roads, water, hills, features and open ground. `CellInspector` supplies bottom-line details.
Recurring week-summary messages are not displayed in the bottom bar; completed-week
results remain available in Weekly Report. Cell details, action feedback, important
warnings and save-path metadata retain their normal status displays.
Session saves and autosaves include map camera coordinates and zoom. Loading
restores that view, clamped to the current viewport/map bounds, without following
the old cursor. Older saves without view metadata retain the previous default behavior.
Placement ranges appear only on valid building locations; existing-building
ranges are suppressed during placement so occupied/blocked cells show no coverage preview.
Selected/previewed building ranges use a cached radial texture with a uniform 32%
opacity throughout the defined coverage area. The fade starts at that boundary
and reaches transparency at 125% of the radius. For services these are approximate
timely-response guides, not coverage limits. Pollution and air cleanup alone retain
the circular screen-space falloff, accounting for the 12x22 cell aspect ratio.
Pollution cleanup is applied after all sources, independent of map order.
Selected buildings show the concise `Active` status in green. Disconnection, missing
Right-clicking any building footprint cell opens a terminal-styled Building Status
table. It shows connection, status, upkeep, capacity and coverage, with Area menu
and Close actions. Granaries also show active storage, city reserves/storage limit,
food consumption, hunger/famine, purchases/import capacity, season and harvest.
Right-clicking empty land retains the usual area menu. Disconnection, missing
Table-dialog footer actions reserve at least 96x36 pixels and their full label
width plus style margins, preventing clipped buttons from collapsing in horizontal rows. Disconnection, missing
Shift-click selection retains the original anchor but replaces its opposite
endpoint, allowing shrinkage or changing direction instead of unioning old bounds. Disconnection, missing
supplies or zero funding show their specific problem in red; reduced funding/service
and scheduled removal show yellow warnings. Current error/warning messages retain
precedence, and Game Menu metadata keeps its neutral color.

## Generation and scenarios

Random `MapGenerator` stages are deterministic from the seed:

1. Terrain generators ordered by generator order and type ID: hills, then water.
2. Highway/interchange generation.
3. Natural-feature generators: trees and rocks.

`GameRandom` uses independent seeded stage streams so changing one stage does not shift another.
Perlin noise controls terrain coverage. The default hills cover about 17%; water generators
produce sea edges, bays, lakes, deltas or meandering rivers plus smaller bodies.
Random highways are long intercity through-routes spread across potential settlement regions,
not short spokes radiating from a hub. Primary routes follow coverage envelopes along the longer dimension, with stronger
meanders and individually randomized bend locations rather than visible fixed lanes.
Candidate scoring favors substantial curvature instead of the shortest nearly straight
line. Secondary intercity corridors enter from varying edges, select scattered land joins,
and end on an existing trunk; they do not all cross every primary route into a Cartesian
lattice. Crossing and terminal approaches share the ordinary road rules. Diagonal approaches
to water are adjusted into straight bridge reaches and then resume their intended corridor.
Standard square small/medium/large maps normally use 3/4/6 corridors.
The explicit coverage target is at most 96 logical cells of unobstructed Manhattan distance
from every buildable cell to the nearest highway centerline. This fixed two-screen reference
does not depend on the user's current viewport, zoom or monitor, and does not create a
simulation connectivity gate. Coverage checks include corners, center and all other land.
Highway-centerline density budgets are 2.5% for maps up to 15,360 cells and 1.5% for larger maps,
with a minimum budget of the long dimension plus 16 cells for thin maps.
Candidates commit atomically through `RoadRules.Plan`, rejecting blobs, near misses and
crowded junctions. Water crossings stay straight; a bounded straight fallback within a
primary band preserves coverage when curved candidates cannot cross water.
Seed determinism and named-city scenario layouts are preserved.
After rasterizing random highways, the generator aims for three to six spaced waterfront
dirt-road spurs, scaling with map size, with each end within six cells of any water body and a
clear, level 4x4 settlement plot beside it. Short straight spokes are preferred; a bounded
land-only search can reach a shoreline around obstacles. The whole stroke must pass
`RoadRules.Plan` before it is committed, preserving junction spacing, connectivity and
the highway count. Spur length is separately bounded; adding dirt spurs does not increase
the highway density budget. New routes preserve the clear settlement plots of prior spurs.
Candidate scoring favors shorter, more central usable sites without imposing a center gate.
Waterless or unsuitable all-water/all-hill maps retain ordinary stubs.
`HighwayGenerator.StartingSpur` selects the most central water-adjacent dirt-road tip.
Fresh sessions/restarts center there; the initial viewport resize preserves the target
instead of moving it to a screen edge. Explicit camera movement, loading and undo clear
the one-shot startup target, so saved camera state and user navigation remain authoritative.
Generated starter streets remain minor-road spokes beside the long trunks. Bridges never bend.

Water generation follows lightweight landscape rules rather than a fluid simulation.
Most river sources favor approaches across the short axis of their receiving lake or normal
to an edge shoreline. The last part of a river can widen laterally into its mouth; receiving
water keeps its body identity, so an inlet cannot consume a small lake or bay.
Existing flow fields keep river travel downhill, avoid cycles and leave lakes/seas level.
About 30% of river-bearing random-map opportunities add broad hill banks around a river
reach. Hill terraces can remain high on both sides of a classified river, forming a steep
canyon without raising the water or creating repeated waterfalls. Ordinary lake and sea
shores retain their existing hill-boundary treatment. Canyon terrain and river flags persist
through the existing map layers; named-city layouts are not rewritten.

`MapSize` accepts square random presets small 320x320, medium 480x480, large 640x640,
and rectangular or square custom maps from 80x24 through 640x640,
and case-insensitive city presets **SF/LA/SD/CHI/STL** (San Francisco, Los Angeles, San Diego, Chicago, St. Louis).
City presets set `GameConfig.Scenario`, normalize new maps to the independent
`MapSize.ScenarioWidth`/`ScenarioHeight` dimensions (640x384) and use `CityScenarioMap`.
The default `GameConfig` matches square Small. Existing saves keep their serialized
dimensions, including older rectangular maps. Session New city and Restart copy
the current configuration rather than applying the new default dimensions.
San Francisco delegates to `SanFranciscoMap`; shared `CityMapGeometry` handles polygons, ellipses and
orthogonally connected road paths.

These are hand-shaped, north-up regional approximations, not GIS/current land-use datasets.
The layouts include city-specific coastlines/rivers/hills/parks and road-served occupied R/C/I
districts. They keep the medieval setting's vocabulary and economy. Households use independent scenario RNG stages.
Scenario road strokes cross water only when the crossing has land approaches at both ends.
Crossings are checked against the accepted road plan, including both banks, before any
new water cells are committed; a rejected bank cannot leave half a bridge. Random highway
routes reject wet endpoints, and fallback straight highways omit leading/trailing open water.
Water-starting/ending road spurs are skipped, street grids stay on buildable land and cannot
branch directly onto bridge water cells. Chicago and St. Louis highway bends stay off their rivers,
while deliberate bridges (including Golden Gate and Bay Bridge) remain. These rules apply to
new/restarted scenarios; loading does not rewrite a saved city's roads.
Scenario generation first zones districts, then reserves complete service footprints on clear buildable
land, then populates the remaining district plots. Utility capacity is planned against those future
occupied plots. Existing services and road beds are not overwritten.

Named cities start paused without automatic onboarding. Explicit F6 guide requests still work.
Saved dimensions/layers remain unchanged on load. Scenarios use only the current `CityScenario` names
and `GameConfig.Scenario`; there are no historical aliases or per-city configuration flags.

## Simulation

`CityGame.Update` advances real time, capped to 0.5 seconds per call to prevent catch-up after stalls.
Paused updates do not advance gameplay. A game week has seven days and a year has 52 weeks.
New games capture the local starting year; saves must contain it.

Growth is planned weekly and distributed across days. Clear multi-cell zoned plots fill up to their daily
allocation. Candidates are sorted before random selection so results remain stable after loading
sparse indexes. Base weekly caps are 3 homes, 1 shop and 1 factory, plus 2% of existing capacity.
Single-cell session zoning expands to the minimum growth footprint, clamped inside
the map. Full-rules residential clicks reserve 2x2; deterministic coordinate-based preferences
choose wider/deeper homes up to 4x4 when clear zoned land permits, then fit down to 2x2.
Narrow strips cannot grow new full-rules homes. Existing small homes and classic growth remain
compatible; building capacity and household counts do not scale with plot area.
Core `Designate` retains exact selection semantics. Cell/zone diagnostics distinguish
incomplete plots from low attraction and missing starter fuel/water dispatch.
Commercial/industrial growth unlocks at 10 occupied residential buildings, with one supported shop per
20 homes and one factory per 10 homes.

The medieval layer lives in `TermCity.Core/Simulation`: `Seasons` (the farming year), `Harvest` (grain, famine, buying
grain), `Feasts` (pilgrim feast days), `Disasters` (fires, raids, plague, floods, earthquakes), `Settlement` (town rank and the
crown's tribute) and the castle tiers in `CityServices` (`SeatRank`). Each runs from `PopulationEngine.RunWeek` or
`CityGame.AdvanceWeek` in a fixed order and draws only from `game.Rng`, so a seed stays reproducible. See
[POPULATION.md](POPULATION.md) and [MEDIEVAL.md](MEDIEVAL.md).

Taxes arrive weekly. Occupied R/C/I buildings have base weekly values 200/350/500 before the default 9% tax rate.
Dezoned occupied buildings retain population/tax income until their random 14-21-game-day deadline.
Restoring their original zone cancels removal. Demolition is immediate, free and unreimbursed.
Service tiers retain their construction/upkeep prices and strengths; response quality now depends
on travel and staffing rather than summed radial coverage.

Statistics visit sparse zones/removal queues and use stack counters. Road connectivity is cached
separately from growth; road changes invalidate it. Utility demand and pollution refresh when
buildings grow, disappear or change zone, before subsequent statistics and weekly settlement assessment.
`RoadNetwork.IsServed` means walkable land or a bridge; `AccessRank` is an optional nearby-road
bonus, including local roads. `TravelRoutes` uses deterministic multi-source Dijkstra fields:
open ground costs 6 units per step (scaled by terrain), compacted paths 2, roads 1/rank.
Twenty-four units are one game day. Sources and topology cache route fields; road/terrain
changes and path compaction invalidate them. First service arrivals compare journey time
against a persisted building establishment day. Older services without that metadata are
already established. The timely target is `radius * 6 / 24` days (ordinary walking distance);
roads reach farther within that same target. Longer trips remain eligible, with quality proportional to target/travel time.
Default area-service radii are 50% larger than before, rounded up; their response targets,
range guides and radius-based staffing increase together without changing prices or upkeep.
Pollution and cleanup keep their prior spatial radii.
Nearest providers share residents by least-time routes. Area staffing capacity is
`max(300, radius * radius * 6) * effective funding`; excess workload lengthens response
time and reduces service quality. Fuel/water retain their city supply/demand limits and
also require deliveries to have arrived.

Each day, buildings wear one new path segment along their least-distance reachable
road approach from a reachable entrance-side cell. Cached deterministic multi-source breadth-first fields are seeded by
the actual rendered road footprint (including both highway bands), avoid building
interiors/unbridged water and established gardens, and choose among actual entrance
accesses, including a home's usable front-garden walkway.
`BuildingEntranceLayout` shares authored thresholds, directions, opening widths and
access kinds (door, gate, open bay or public space) with the model artwork. It transforms
them using actual rotation, foundation height, special offsets and fitted model scale.
Legacy undersized saved plots retain their footprints; model and doorway scale fit
those plots together. Courtyard approaches reach the recessed entrance-bearing block,
not an imaginary door at the overall plot front.
`RoadWearApproach` is deliberately separate from the original `RoadApproach`:
`RoadDistance`, growth classification, dispatch, service capacity, travel costs and
arrival formulas retain their existing perimeter-based behavior. Door-aware journeys
only change where new wear develops. An obstructed entrance yields no invented rear
connection and adds an inspection diagnostic, not a service or settlement gate.
Bridge decks and their parapet/approach footprints are never footpath targets, including
raised spans over dry valleys. A cached bridge-cell mask keeps new and saved path
connections on ordinary land roads; rendered ribbons and caps also subtract actual
bridge corridors so they cannot paint onto a deck. General travel and service routes
still cross bridges normally.
Buildings that can reach a road do not also wear direct links to neighboring buildings.
Roadless service/workplace journeys remain available, but residential-to-residential
journeys are excluded. Roads remain optional settlement/travel boosts, not gates.
Residential arrivals prefer the shortest reachable approach to an actual road footprint.
Both vacant homes and new plots are considered in nearest-first distance bands, with
randomized choices within a band and the existing land-value choice for vacant homes.
Invalid nearer plots do not block farther plots; roadless and unreachable-road plots
remain eligible. Commercial/industrial growth and weekly arrival quotas are unchanged.
Repeated traffic compacts segments at wear 3; wear caps at 12. Paths are not roads,
cost nothing, do not require edge connectivity, and survive saves and undo.
Perspective footpaths follow the same cached building/road journeys that sims wear, rather than
joining every adjacent worn cell. `FootpathLayout.Strokes` clips unworn gaps and rounds corners
inside their traveled cells, so neighboring routes do not acquire artificial ladder rungs.
Each actually traveled step records a reciprocal north/east/south/west mask in `PathLinkLayer`.
The optional compressed `PathLinks` save layer preserves that topology when journeys change;
loading rejects invalid or asymmetric links, and clearing wear removes incident links.
Legacy saves without edge metadata retain straight trails and real branches, but reconstruction
does not join parallel lanes merely because their worn cells touch. Current journeys take
precedence over historical corridors; redundant old connectors are removed as whole corridors
instead of leaving ladder-rung stubs. Raw wear is retained.
`FootpathArtwork` supplies lit, rough earthy block patches with mipmapped anisotropic
filtering. Its deterministic 256-pixel tile uses irregular, low-contrast four-tone
earth patches rather than a small repeating band pattern. World-space UV scale
0.0875 preserves the broad cluster size while extending the repeat to about 11.4
world units; mipmaps average the muted patches at distant zooms instead of producing
striped trails. Texture tests bound contrast and short axial/diagonal correlation.
Fresh and worn palettes remain distinct but muted. Emerging trails are 0.45 world units wide and worn trails 0.60
(about two to three feet), subordinate to the 1.0-unit Dirt Track. Short doorway
approaches flare only to the actual opening and use the same earth artwork and world texture scale
instead of untextured yard-color boxes; other yards and gardens retain their materials.
Material batching is retained rather than adding a node per path or entrance segment.
Terrain-following mesh ribbons replace overlapping rectangular bars. `RibbonSections` samples
the centerline and smoothly flares its width at road contacts; targets project onto the actual
road centerline, including diagonal roads. Ribbons are clipped against the road polygons, so
their curved mouths meet the road edge without painting dirt over it. Free ends of connected
strokes retain rounded caps. Persistent wear that no longer belongs to a current journey
is reconstructed into cardinally connected ribbons and branches, reaching neighboring
buildings/roads; isolated wear cells are not rendered as scattered dots. Unworn gaps and
water are not bridged, and active parallel journeys remain separate. Obsolete residential-only
links and near-road contour edges are omitted from persistent fallback rendering without
deleting saved wear. Wear rates, costs and save data are unchanged;
the overhead glyph renderer retains its existing cardinal connectivity.
`FootpathSurfaceLayout` caches exact ribbon triangles, turns, flared mouths and caps once
per scene rebuild. Both `DrawFootpaths` and `ZoneSurfaceLayout` use those same polygons,
so checkerboards clear only the actual path surface while residential zoning stays intact.
The same clipping is applied to other zone colors for consistency. Opt-in
`MeasureZoneFootpathOverlap` verifies emitted zone triangles against the shared path polygons.
Roads remain useful travel boosts, not prerequisites for settlement or redevelopment.

`FootpathReliefMesh` adds a 0.015-world-unit shallow crown with 0.12-unit shoulders above the existing 0.014
surface lift. Exposed polygon edges and road contacts taper back toward that lift;
the shared polygon index avoids treating internal triangle diagonals as shoulders.
Exposed union edges are split and spatially indexed lazily once per layout, so relief
queries do not repeatedly probe every polygon for every emitted vertex. Occupied
building cells are clipped out of exterior wear; plot-local entrance geometry takes over.
Each supported base triangle is subdivided into six triangles without expanding its
2D footprint. Shared vertices use the tightest incident grade allowance, keeping the
actual emitted rise within the existing 0.12-world-unit limit. Unsupported terrain
triangles remain rejected. Normals follow the resulting geometry rather than always
pointing vertically, so the existing sunlight and building shadows reveal the relief.
There are no new terrain levels, curbs, route rules, wear rates or save fields.

`PerspectiveCity.Entrances.cs` builds plot-local approaches and threshold-height
steps for all entrance-bearing families. It replaces the old independent house
doorstep/approach, clears directly obstructing front props and preserves picking,
highlights and preview tinting. Completed world-space dirt vertices, normals, UVs,
steps and threshold diagnostics are replayed by the building geometry cache; its key
includes the owned yard. Derived entrance accesses are invalidated with travel topology,
building/yard assignment, visibility and zone changes. No entrance fields are saved,
and historical wear and reciprocal links are retained.
`PerspectiveCity.Footpaths.cs` uploads dirt triangles as material-batched array meshes
instead of issuing per-vertex native calls during cache replay.
The focused `--footpath-art-smoke-test --capture /tmp/termcity-paths.png` covers fresh
and worn actual journeys, all five zooms/four headings, real doorway picking, exact
cached/fresh dirt buffers, upward normals/world UVs, actual building-shadow pixels,
road-mesh clipping and unchanged serialized city state. Its entrance catalogue also
checks 220 building/rotation cases against actual rendered door panels, step tops
and exact cached replay. Core entrance tests cover legacy plots, recessed doors,
blocked access, garden walkways, persistence and unchanged service/travel formulas.
The final focused core runs passed 151 entrance/path/yard/service checks and 58
placement/rotation/persistence checks. Graphical building/interface smoke checks
and headless artwork/interface checks passed; the headless interface process
reported two leaked ObjectDB instances during shutdown, despite a successful exit.

On Apple M2 / GL Compatibility, the earlier shallow-relief implementation changed
the four-house/six-journey fixture from 620 to 3,720 generated path triangles,
79 to 80 render nodes and 81.2ms to 122.6ms worst cold scene build. Entrance-aware
routing now produces five longer journeys in that fixture: 7,200 triangles,
80 nodes and 129.5ms worst cold build. These added artwork/route costs are retained
as findings, not represented as performance improvements. The exposed-edge index
reduced the entrance fixture's earlier 360-372ms cold builds without changing its mesh.
The final populated SF continuous-scroll check measured 20.1ms frame p90 and
93.0ms worst frame, reusing 1,897 buildings (pre-entrance result: 18.1ms/68.7ms).
The large random-map drag measured 18.0ms p90 and 24.5ms worst frame; its minimap
passed narrowly at 247.0ms against 250ms. Existing populated-map performance
failures remain: SF minimap 466.5ms against 250ms and road preview 115.4ms against
100ms. No budgets or rendering quality were reduced.

The low-repetition texture/0.75x follow-up passed 340 targeted core checks (six
retired checks skipped), graphical five-zoom artwork/catalogue checks, actual
placement zoom controls (keys, sidebar, wheel and pinch), and 0.75x sidebar layout
at 960/640 widths across all font scales. The artwork fixture retained 7,200
triangles/80 nodes and measured 139.6ms worst cold build. Large-map drag measured
16.8ms p90/17.5ms worst with 239.4ms minimap latency; populated SF drag measured
17.6ms p90/86.2ms worst, reusing 1,897 buildings. SF minimap (485.8ms/250ms) and
road preview (135.8ms/100ms) still exceeded their unchanged budgets. These checks
used separate runtime assemblies while preserving an existing user game window.
The broad perspective smoke still fails its terrace lower-stone/plinth assertion;
the untouched pre-change Debug build reproduces the identical assertion and
geometry, so this is not reported as a passing suite or a texture/zoom regression.

`Changed` notifies possible UI changes; `MapVersion` advances only for map-visible changes.
Renaming, taxes, speed, pause and guide preferences raise non-map change notifications, allowing
dirty/autosave tracking without changing the map version or discarding road geometry and service caches.
Failure results carry user-visible explanations.
`CityProgression.IsServiceUnlocked` gates service needs and petitions by the cheapest
population gate in each registered family, using current population or permanent peaks.
Locked care has zero unmet-service happiness penalty; reports show its gate without
asking residents to demand unavailable construction.
Growing Hamlet (250 souls) permanently unlocks Fire Watch, Watch House, Sheriff's Hall,
Apothecary and Chantry School, with the same shared milestone grant/report workflow.
The 100-person Hamlet milestone now focuses on starter supplies and jobs; grain remains
gated at the 500-person Village milestone.

## Session and zoom

`GameSession` supports camera pan/scroll/jump, cursor movement, rectangular and keyboard selections,
preview quotes, straight road-line endpoints and confirmation. Camera and selection changes have
separate events. A successful placement clears its selection; failed/cancelled actions retain the
existing undo boundary.

Zoom levels are -2 (0.25x), -1 (0.5x), -0.5 (0.75x), 0 (1x), and +1 (2x).
Controls traverse that ordered list; reset goes directly to 1x. The fractional step
uses 75% of normal perspective magnification and retains individual-cell selection,
not coarse block sampling. Saved views accept the fractional level while existing
integer zoom values retain their old meanings. Whole negative levels sample square
blocks of four or two cells. At +1, `TerminalGrid` doubles **both** pixel dimensions and `TerminalMap`
doubles glyph size; logical cells are never horizontally duplicated.

`Grid.Fill` sets the session viewport using fully visible-cell counts so keyboard navigation stays
on whole cells. Its buffer includes partial edge cells to paint the entire viewport, clipped by
`TerminalMap`. Pixel hit testing rejects coordinates beyond the actual viewport, including unused
parts of clipped cells, and uses
`PixelWidth`/`PixelHeight`; `TerminalMap.ZoomBy` remaps the world anchor after resizing so pointer
or center zoom stays anchored. Coarse cameras/selections align to whole sampled blocks.
The map's content margins follow its visible inner borders as they join the header,
construction rail and sidebar. All four visible padding gaps remain equal instead
of measuring a fixed inset from control bounds that differ from joined borders.
The right sidebar also follows its visible inner borders, using the same compact
4-pixel visible-padding treatment as the left construction rail on all four sides.

Undo caches up to ten serialized full-city snapshots for successful placement,
zoning, dezone and demolition actions, evicting the oldest when full. Undo requires
pause and confirmation and preserves older remaining snapshots after each restore.
It restores money, residents, random state and calendar. Failed/cancelled actions
do not consume history; load/new clears it. macOS uses Command+Z and other platforms
use Ctrl+Z; help displays the matching shortcut.
Session prompts/previews suspend simulation. Autosave still tracks elapsed real time and changed cities.

## Godot UI and input

Debug runs start maximized (not fullscreen), except headless runs and smoke tests, which retain their
controlled window sizes. Release runs retain the normal windowed startup. Changing
font size while maximized preserves maximized geometry.
The default window is 1440x900 with a 640x780 base minimum, capped to the usable desktop. Display font size defaults to 20,
supports 16-28, and sets shared content scale `font size / 16`. Window sizing stays within usable
desktop bounds. F3 opens live font controls with OK/RESET.

The top header is one row with uniform base font size 24, fitted on narrow windows. TermCity/name,
date/progress, population and funds have separate double-white frames. The time
section has 12-pixel spacing between its fields, with a rightmost speed label:
1x (Slow), 2x (Medium), 3x (Fast). These are speed-level labels; the underlying
configured day/week durations are unchanged.
Funds show the gold amount and weekly balance without a TREASURY label.
The weekly balance uses brackets without `/wk`, e.g. `50,000g [+100g]` or
`50,000g [0g]`; only positive balances have a plus sign.
REPORTS has its own frame between population and funds and opens the weekly report
on click, respecting the same editing/prompt/preview guards as other header links.
POP is display-only and does not open the city menu; its tooltip shows the population.
The icons use shared rendered construction artwork and hover/click pulses rather
than font glyphs, with dedicated clear, pollution, land-value and happiness drawings.
VIEWS opens a horizontal terminal icon rail inside the map, aligned to its top left.
All overlay views retain curved rendered roads; selecting a view never switches
the map back to terminal road glyphs.
Icons select every overlay, including a clear-overlay action. Only clicking VIEWS
O cycles overlays; Shift+O toggles off/on while remembering the last selected
overlay (fuel if none has been chosen yet). Clearing via VIEWS also preserves that choice. Clicking VIEWS again or pressing Esc closes the rail without clearing the overlay.
Outside clicks keep it open. When a dialog is on top, Esc dismisses that dialog first. Below 900 logical pixels,
REPORTS and GUIDE hide to prioritize readable header text; F7/F6 remain available.
The aggregate Services row is omitted from the sidebar; detailed services remain
available in Weekly Report.
Complaint is also omitted: needs become varied medieval petitions at bottom right.
Multiple complaints cycle once per game week in descending severity order and
wrap; the message remains stable while paused.
Needs costing at least 15 happiness points, essential shortages costing at least
5 points, or miserable city happiness show red; milder needs show yellow.
Placement warnings, save metadata and dialogs take priority; full messages are available on hover.
Petitions can use remaining bottom-bar width beyond half the screen while reserving
space for left-side information. Hover shows the full petition and a complaint-specific
remedy, including relevant buildings, road access, funding, taxes or food imports.
While a building catalog is open, the bottom-right message instead shows its
selected service's coverage, supply or grain storage/reserves as a stable label.
Color indicates service level; changing percentages and quantities appear only on
hover. Switching buildings/categories updates the label immediately, without
rewriting it on every UI refresh.
Displayed service level/color and hover details refresh every three Fast weeks,
scaled by configured speed durations to the same real-time interval (one Medium
week or half a Slow week with defaults). Opening a catalog or changing its selected
building refreshes immediately. Paused clocks do not advance the refresh interval.
Gameplay coverage and Add affordability still update normally.
City sidebar table rows open the matching Weekly Report tab when either cell is
clicked: demographics/Mood open Population, Season/Grain open Food & Seasons,
and Fuel/Water/Services/Complaint open Services. Existing dialogs and placement
previews block these links.
Fuel and Water show read-only terminal gauges with white block markers and raw supply/demand
percentages on hover. Their colored bars are 14 pixels thick with no white outline;
the white marker extends two pixels above and below the bar.
Percentages are not capped at 100%. Red is below 95%, yellow is 95%
to below 105%, and green is at least 105%. Tracks span 50-150%; levels at or below
50% pin the marker to the left edge without reserving a lower segment. Higher supply keeps
its exact numeric value while the marker stops at the end. No demand shows 100%.
Fuel/Water cells show only the gauges; percentages and supplied/required units
appear on hover. Gauge clicks retain the row report link.
Grain uses the same gauge with a two-week reserve target (100%). Its tooltip
shows actual reserve weeks, percentage of the target and hunger/famine status.
Clicking Grain continues to open Food & Seasons.
GUIDE sits between REPORTS and funds in its own terminal frame and opens the guide
on click. Below 900 logical pixels of window width its entire frame is hidden and
the other sections reclaim its space; F6 still opens the guide.
Clicking outside an open Guide closes it (or returns to its parent menu), consuming
the click without triggering gameplay underneath. Gold opens economy only when its rendered text is clicked, not when
empty space in its reserved label slot or frame is clicked.
On narrow windows, the gold slot reserves only the width of `99,999g [999g]`
instead of the full large-balance/empty-coffers sample, favoring a larger header
font. Longer values clip with ellipses and remain available in the tooltip.
Header gold amounts above one million use two-decimal millions, e.g. `1.01M`.
Exact gold amounts remain in reports and the header tooltip.
The weekly Reports dialog targets 720 pixels of content width, clamped to the
available map/window area. It uses the shared black/double-white terminal frame,
equal compact padding, wrapped scrollable text, and a bottom-right Close action.
Its summary and zone details are label/value tables using the same
shared single-white grid-cell styles, alternating neutral fills and data colors
as the City sidebar. Values wrap rather than being truncated.
Each table's label column is sized to its longest label so multiword labels stay
on one line, while the value column uses the remaining space.
The main title is `Weekly Report`; each table subheader uses a closed double-white
terminal frame on black, matching the sidebar section headers.
The Milestones report tab shows current settlement size and a green progress bar from the current
achieved size threshold to the next one. Population losses clamp progress at zero
without revoking achieved sizes; Great City shows a full bar and completion text.
The current milestone appears first, followed by a vertically scrollable list of the
other reached and upcoming milestones with their unlock tables, model previews and advice.
The next milestone indicator lives here rather than on Overview. The guide retains
its milestone summary; the growth announcement has Continue only, without browsing controls.
Weekly Report has Overview, Population, Economy, Services, Growth, Food & Seasons
and Events tabs, followed by Milestones. Food & Seasons prominently shows famine/food status, reserves,
storage/consumption, merchant grain purchases/prices, harvest quality, current
season and all four seasonal modifiers, plus recent food events.
Shared `ReportContent` supplies both session text and Godot tables from actual city
statistics. Overview includes map/calendar details and the current size. Population
includes demographics, jobs and last-week changes. Economy separates projected
itemized weekly income/expenses from the last completed week's figures. Services
shows supply, health, coverage/funding and complaints; classic rules explicitly omit
unsimulated service data. Growth shows zones and blockers. Events lists the latest
20 recorded events, newest first. Missing history is stated, not fabricated.
Dialog scrollbars use square white-outlined black tracks and gray block thumbs,
highlighting white on hover and yellow while dragged. Native arrow icons are
hidden; wheel, dragging and keyboard scrolling retain their normal behavior.
Dates place
the padded week/feast label before `AD` and the year, e.g. `Week the 17th AD 1289`.
Header text uses reserved slots and fits to window width independently of current
values, preventing movement when weeks, feasts, pause/speed, population or treasury change.
Exceptionally long values are clipped with ellipses; hover shows full date or financial text.
Ordinary weeks display
`Week the 1st` through `Week the 52nd`, with correct ordinal suffixes. In the standard
52-week calendar, fixed medieval English feasts replace the week label: Candlemas (5),
Lady Day (12), May Day (18), Middsommer (25), Lammas (31), Michaelmas (39),
All Hallow's (44), and Christmas (52). These use January-based seven-day week buckets,
not ISO weeks; movable feasts are not modeled. A pixel icon accompanies the date:
a calendar ordinarily, or a candle, flower, maypole, sun, wheat, angel, skull or tree
for the corresponding feast. Custom-length years retain ordinal weeks.
This is presentation only; the simulation calendar is unchanged.
Desktop new maps opt into the current approximate week using January-based
seven-day buckets capped at the year's last week. The date then advances with game
time, so season and feast labels remain consistent. Loaded saves retain their date.
Core callers and smoke/map-dump runs retain week-one startup unless a starting date is supplied.
The day bar comes from `Fmt.WeekBar`. Speed hotkeys still work without a header indicator.
While paused, yellow `[PAUSED]` replaces the week-progress bar in its existing centered
slot; resuming restores the cyan day bar without moving other header fields.
The indicator also reflects clock suspension by any prompt, placement preview or
name editor, without changing the player's manual pause state.
Space toggles pause globally outside text entry, including dialogs and placement.
P remains a menu mnemonic while dialogs are open and toggles pause during gameplay.
Explicit resume allows simulation to run with the current dialog or
preview still open; newly opened dialogs retain automatic suspension. Enter
activates dialog buttons (Space is reserved for pause). Ctrl+P, or Command+P on
macOS, toggles pause even while typing; plain P/Space still enter text.
No pause dialog, facts or artwork are instantiated, leaving the map and controls
unobstructed. Space/P toggles pause; Escape opens Game Menu without changing pause.
The former dialog/animation implementation remains inactive for possible later use.
Top-bar frames use 4-pixel visible top/bottom insets and vertically centered text.
Their layout margins follow the actual inner border positions, including shared
bottom joins, rather than centering against the container rectangle.
There is no shortcut toolbar. Function-key actions remain available through keyboard input.
All modal dialogs, including save-before-quit/continue confirmations, use `TerminalFrame` for
the black background, two white border lines and shared 12-pixel inset.
Popup frames disable adjacency joining so their borders stay closed and do not
extend into neighboring headers. The game menu uses an all-caps GAME MENU title
and option/action labels, with existing keyboard mnemonics and no description text.
The modal's local Button theme gives every clickable action a single square white
outline in all states, including tabs and save controls. The green construction
Add button uses green text on a gray fill and the same one-pixel outline.
Construction type rows use `MnemonicButton` with the prompt's shared `MenuShortcuts`
assignment, including locked choices. Underlines respect asymmetric content insets
so capacity subtitles do not displace them. Enter/keypad Enter on a type row activates
Add; a focused Cancel retains its normal keyboard action. Row focus updates the model,
statistics and actual selected purchase together.
Right-click construction context records both the map cell and game identity, survives
submenu navigation, and is consumed on placement. A valid building/defense uses the
ordinary preview and confirmation transaction immediately at that cell. Invalid
footprints remain unpaid movable previews with an explicit error. Closing the menu,
opening a fresh sidebar catalog or replacing the game prevents stale automatic placement.
Cancel button text is centered; construction Cancel buttons match Add in width.
Save-confirmation action text is centered. Save and quit is green; Quit without
saving is red. Descriptive text is omitted, while the title and errors remain.
Godot debug builds quit immediately without confirmation. Release builds retain
the save confirmation; smoke tests explicitly retain it to validate that flow.
Construction actions have equal space above and below, with additional top padding.
Dialogs reserve the construction rail's width. Construction catalogs anchor at
the map's top-left; other dialogs center on the map, clamped to the available
window area when too large to fit inside the map.
The game menu targets a compact 420-pixel content width.
Its bottom-left SAVE button (underlined S shortcut) writes the existing quick-save file and
keeps the menu open. Save errors remain visible through the normal modal error display.
While it is open, the bottom status row shows seed and map dimensions on the left
and the quick-save path on the right, clipped without overlap with the full path
available as a tooltip. Closing the menu restores the usual gameplay status.
Its content uses equal padding on all sides: the shared 12-pixel frame inset plus
2 pixels, matching the existing top/bottom spacing rather than adding wider side gutters.
Music and Sound volume sliders and mute buttons appear directly below the display
options, without a separate audio dialog or increasing the game menu's width.
Each channel has a single-white border with 4-pixel padding and an uppercase label.
Volume sliders share a row with their mute buttons and omit percentage readouts.
MUSIC and SOUND labels occupy separate single-white bordered cells in those same rows.
Each slider has 12 pixels of additional left/right padding to separate its track
and block handle from the channel frame and mute button.
Slider tracks use square, single-white outlines on dark fills and crisp white block
handles instead of rounded native styling, retaining native pointer and keyboard input.
Mute buttons show pixel-stepped white speakers: unmuted channels have two sound-wave
lines, while muted channels show only the speaker, without a slash. M and U toggle music and sound.
The game menu has no separate column-header row or frame.
Effects and Celebrations are omitted from the game menu; the effects keyboard shortcut remains available.
The NEW MAP button to the right of the bottom-left SAVE action uses green text and the existing unsaved-progress
guard before creating a fresh random map of the same configured size.
Back to game and Quit are not menu rows. Bottom-right Quit and Cancel buttons use
the same action layout as construction catalogs, with red Quit text. Quit opens
the existing confirmation; Cancel closes the game menu. Q and C activate Quit and
Cancel respectively, with their initial letters underlined. City management is
not a game menu option; city-action keyboard shortcuts remain available.
Cancel receives focus whenever
the game menu opens or returns from a submenu, with a subtle blue-gray focus fill
that remains visible when the pointer moves away. Keyboard navigation can still focus other actions.
Titles and section headers use 24-point text with compact vertical frame insets.
Catalog choices use 22-point names and separate 30-point icons, with single-white
name borders and one enclosing single-white border around the list.
Buildings with a capacity also show it directly beneath their choice name in
14-point text and near the top of the details table, after cost and upkeep.
Capacity units distinguish fuel, water, grain measures, residents and jobs.
Area-service buildings without a capacity retain their radius/strength information.
Starter fire, sheriff, physic and learning services unlock at Growing Hamlet (250 souls);
higher-tier service gates remain unchanged. Locked catalog choices show a red
padlock to the right of the name. Unlock details show the milestone name and
remaining population, e.g. `Growing Hamlet (40 souls to go)`; permanent unlocks show zero.
Village Green requires Village (500 souls); Tavern requires Market Town (1,000 souls).
Construction Add is disabled for both locked buildings and insufficient base-cost
funds and refreshes while the submenu stays open, including after income, expenses
or unlock changes. Selection and focus are preserved; disabled Add text is gray,
and affordable/unlocked Add text is green.
The shared session purchase guard also rejects keyboard/direct preview
entry, retaining the menu and showing required/current gold. Terrain-dependent
placement costs remain checked at the chosen location.
Every catalog uses the same name-column width, sized to the longest placeable
building or road name across all categories. The duplicate
selected-building heading is omitted.
At minimum window widths, detail labels and descriptions can wrap within words
to preserve this shared name width without overflowing the window.
Details table cells use uniform one-pixel white lines, drawing shared edges only
once. Cell padding is 6 pixels horizontally and 3 vertically.
An outer single-white description box encloses the text without a description header.
Description text uses 20 points; the box reaches at least the first name row's bottom
and grows for wrapped text.
Catalog category headers also use a single-white border; the main dialog retains its double border.
Enclosing catalog boxes use even 4-pixel border insets, matching the icon rail.
Stacked catalog sections and names use 3-pixel separation for 4-pixel line-to-line spacing.
The name list fills the catalog height and shares its right border with the description
and details table, forming connected intersections without adjacent duplicate vertical lines.
The text body has padding but no separate border.
Each newly achieved milestone awards a one-time gold grant based on the two
cheapest newly unlocked buildings, with a 5g-per-threshold-soul floor and grants
never decreasing at higher milestones. Skipped milestones each pay their grant;
the persisted highest milestone prevents repeat payment after decline or load.
Grants appear in the announcement and guide and do not count as recurring tax income.
Your City Grew! announcements explicitly pause the game and target 720 pixels of
content width, clamped to available space. The reached milestone is selected, with
tables of newly unlocked buildings, summaries, costs/upkeep, footprints and key
capacity/coverage features. Continue is the only action; arrow keys cannot browse
other milestones in this announcement. Reports > Milestones opens at the current size,
with progress to the next milestone and a scrollable list of all other milestones.
The announcement lists only that milestone's new unlocks,
not all currently available buildings or higher service tiers.
Milestones with no new building/service unlocks omit the New Services header and empty
availability tables; their population, grant, status and advice remain visible.
The focused `--milestone-smoke-test` crosses 250 population, verifies exactly the five
care unlocks and their thumbnails, verifies the Continue-only announcement and
scrollable Milestones report with its relocated progress indicator, and checks the
shared terminal dialogs at normal and 640-pixel window widths.
Continue closes the dialog but leaves the clock paused. Placement feedback does
not overwrite a newly raised milestone banner.
The growth title is centered yellow, the city-size heading is green, and New Services
precedes the unlocked building tables. Left cells are blue and right descriptions
are gray.
The city-size and New Services headings use matching 24-point text, with the city
size centered. Each building section shows its footprint artwork (or fallback
icon) in a bordered cell to the right of its feature table.
The shared square terminal scrollbar is reused; the modal and scroll
viewport clip overflowing content to prevent frame/text drawing outside the dialog.
Area effects are written in one combined Effect row, including service coverage,
clean air, pollution and their radii, alongside other effect text.
Descriptions are unchanged; there is no separate area-icon row or hover icons.
Each effect shows its bounding width x height in cells (2 x radius + 1 on each
axis); the simulated area remains circular. Upkeep omits the funding suffix, and
the redundant Operation row is omitted.
Buildings without an actual area of effect omit the Effect row entirely; capacities
and other non-area details remain visible.
Catalog descriptions appear above statistics/effects at every window size, with
both sections filling the information column. The statistics/effects heading is
omitted. Road details also omit Needs. Details omit Does and Placement, show Cost as only the gold amount,
omit "cells" from Footprint, label area icons "Area of Effect", and capitalize
values beginning with letters. Cost comes first, followed by Upkeep.
Dialogs never cover the construction icons.
Adjacent terminal frames share one double border through the midpoint of their gap.
At three-way intersections, the upper horizontal line continues straight. The lower line
breaks between the vertical stem's two lines and turns downward instead of crossing the branch.
At the bottom edge, vertical stems stop at the inner horizontal line so they do not cross
the double-border channel. At the left and right window edges, the outer vertical
line stays continuous and horizontal branches stop at the inner square corners.
Only the map/city-sidebar drag handle shows a resize cursor;
the construction icon rail is fixed-width with a 4-pixel outline-to-outline inset,
matching the spacing between its double-border lines, not the container rectangle.
Individual icon frames use a compact 6-pixel inset, 40-pixel square buttons and
24-point glyphs. The 3-pixel container separation gives stacked icon outlines the
same 4-pixel line spacing, so every category fits the minimum window height without a scrollbar.
`ConstructionIcon` draws shaded illustrations with up to four palette colors inside
the existing buttons: a warm-toned isometric road with two shaded lanes and a dashed
center divider (no curbs, wheel tracks or tile pattern), water droplet, campfire,
Fire Watch tower with a red warning flag,
sheriff star, health cross, open book, tree, shield, chapel, market stall and wheat.
The trade stall has a red-and-cream striped awning.
Granary wheat uses rounded kernels on thin stalks rather than pointed chevrons.
Education has cream pages and a red bookmark; commons has a brown trunk with
shaded green foliage; granary has golden kernels, green stalks and a brown tie.
Faith has cream walls, a red roof and a blue window.
Hover and click trigger a short pulse in the artwork's main color.
Hover also rocks only the artwork left, right, then upright in three 0.25-second motions,
starting with the top tilting left, then returning upright. This works while a
construction catalog is open as well as during normal gameplay.
Each icon has one white outline, not another double-line frame. The rail's outer
border participates in the shared double-line layout coming down from the header.
Icon buttons have square bounds and zero additional theme-style margins inside
their equally padded outlines.
Border joins refresh with layout and visibility changes. The rail opts into
`VisibleContentPadding`, adjusting its layout margins to preserve equal visible
spacing when shared borders extend beyond its container rectangle.
Hover tooltips use the same treatment through the inherited `TooltipPanel` theme:
`TerminalFrame.CreateTooltipStyle` supplies a nine-slice texture so borders retain
their spacing as tooltip text changes size. This convention is required for all
future menus, dialogs and popups by the repository's Copilot instructions.
`GameSession.HasCursor` starts false: map highlights and selected service ranges remain hidden
until explicit targeting or placement, while the status line shows `Press F1 for HELP`.
Camera movement and zoom do not activate the cursor.

Double-click enables a right-aligned city-name `LineEdit`, limited to 16 Unicode code points.
Its native caret width matches a character to form a blinking block; normal/focus right margins
reserve that character cell so the caret is not clipped. Text backspaces toward the
stationary right edge; an overlay slot leaves branding and header geometry unchanged. Editing
pauses the clock and restores its previous state on commit/cancel. Invalid names remain editable
with a visible error.

`TerminalFrame` draws two closed rectangular outlines inside its own bounds, with square corners and
a shared 12-pixel content inset. Neighboring frames are separated instead of extending edges into
crossing or T-shaped joins. The map fills its expanding frame; partial edge cells are clipped rather
than snapping the frame to cell widths. This keeps all four map insets exactly equal and sidebar
dragging continuous. `CitySplit.Divider` matches the map inset, so the external gap is also 12 pixels.
Popups fit the longest title/choice plus five `M` glyph-widths per side,
with inner vertical margins of 2 pixels. Deferred minimum-size changes re-fit wrapped content
and keep smaller dialogs from retaining stale heights. Oversized content scrolls.

The non-scrolling sidebar contains Minimap, Demand and City, with black double-white header cells.
An enclosing `SidebarFrame` retains the full-height double-white outline and the same 12-pixel
content inset as the map frame. The divider gap is measured between the two outer frames.
City uses colored native cells without a Metric/Value heading row. It omits population, tax/budget,
standing and employment rows duplicated elsewhere. Demand tooltips contain all per-zone counts;
bars use `Demand.Compute` with the same `ExpectedArrivals` rates and `SupportedBuildings`
capacity as weekly growth. Vacant zoning is not counted as fulfilled demand. Full-rule
attraction, seasonal migration and trade climate affect the forecast without drawing from
simulation RNG. Tooltips show expected weekly arrivals, remaining business slots and the
shared `GrowthDiagnostics` blocker, including stalled trade climate and slow arrivals.
`CityPanel` caches the formatted hover details by city/statistics/indicator snapshot,
pause state and season so pointer selection and camera motion do not repeatedly rescan
vacant plots.

Perspective zone checkerboards use `ZoneSurfaceLayout.Patches` to subtract exact rendered
road polygons before emitting triangles. This includes curbs and bridge decks and applies
to existing zoned cells as well as new zoning, without changing gameplay zoning or UV scale.
Opt-in `PerspectiveCity.MeasureZoneRoadOverlap` checks emitted triangle intersections during
graphical smoke rebuilds; normal play incurs no diagnostic intersection scans.
`RoadSurfaceMeshes` caches each cell's actual land-top triangles separately from roads,
water and vertical banks. `ZoneSurfaceLayout.Conform` intersects zone patches with those
triangles and interpolates height on each original plane, retaining exact world-space
UVs and the shared small surface lift. Road-cut height breaks therefore cannot pull a
large checker triangle underneath a neighboring hilltop; lower and upper terraces stay
separate and cliff faces remain unpainted. Flat cached chunks retain their flat-plane
fast path. Opt-in diagnostics verify complete top-surface coverage and emitted heights
beside all road types at multiple zooms and rotations; terrain and logical zoning are unchanged.
Clipping intersections use double-precision arithmetic before storing float vertices.
Float-rounded outside intersections are kept on the outside of the cutter, including
at large square-map coordinates; exact aligned road-edge area checks remain unchanged.
Graphical gap checks temporarily use a magenta scene background to distinguish actual
holes from legitimate dark shadows, restoring the normal background afterward.
Ground-only color/picking fixtures temporarily remove and restore natural-feature
occluders, and artwork fixtures convert authored dimensions through each model's scale.
keyboard users get the identical formatter through F7, F8 and the full-rules health report.
City body text targets 16 logical pixels (three larger than the previous body setting); section
headers stay at 18. Constrained layouts adapt the body size without changing the header font.
`CityPanel` prioritizes a square minimap at the full content width, adjusting text spacing
and using paired City rows when height is constrained. Minimap and Demand remain full-width blocks.
The minimap has no section header; its zoom controls sit directly above it.
`MinimapProjection` maps the entire overview into a fixed square and rotates its
contents in quarter turns relative to the initial camera heading. The overview
does not inherit camera tilt or become a diamond. `GroundViewCorners` projects
the four viewport corners onto the ground plane; the white indicator traces that
footprint rather than the session's axis-aligned view rectangle. Its edges clip
to the square without changing angle. Click/drag navigation uses the inverse
quarter-turn transform. Heading and tilt changes reuse the cached `MinimapImage`
and texture rather than resampling terrain. The legacy cell view stays north-up.
CITY and DEMAND headings retain larger 22-point text while the sidebar fits.
The zoom section has a single square white border on black with a 1-pixel inset
on all sides, leaving no extra gutter beyond the border.
The centered zoom-level text retains a 20-point size during sidebar fitting.
Its buttons retain centered 22-point symbols, with equal 2-pixel text padding on
all four sides and no forced minimum width or height, retained
when the sidebar adapts to narrow or short windows.
Demand letters use vertically centered 18-point text. Their bars retain matching
18-pixel heights, centered alongside the labels during sidebar fitting.
Its body text targets 18 points, fitting down to 13 points when sidebar height is constrained.
The City table uses a continuous square terminal grid, with single shared lines and
no gaps between cells. White borders and alternating black/dark-gray fills match
the terminal interface. Existing label/value colors are preserved in both the normal
and paired-column compact layout.
Width/height/row-count caching avoids redundant fitting. Screen-constrained layouts preserve the
minimap's shape and fit the largest overview that leaves the remaining details visible.
The Guide sidebar section is removed; optional next-step coaching is on the F6 Start tab, and its
existing dismiss/enable controls and saved preference still work there.
`CitySplit` supports a 12-pixel drag area, 280-pixel sidebar minimum
and 480-pixel maximum, reduced to retain 320 pixels for the map where possible.
The game menu size rows use white text and inline -/RESET/+ buttons. RESET restores
the corresponding default font size or sidebar width. Their labels still
open the separate adjustment dialogs. Font size uses 2-point steps; sidebar width uses 12-pixel steps.
RESET buttons use 16-pixel horizontal and 10-pixel vertical internal padding in all states.
Inline -/+ buttons have 48x44 logical-pixel minimum targets and centered 28-point symbols.
Arrows navigate dialog buttons; Space/Enter activates the focused button. Text-field dialogs retain
native cursor navigation and sliders retain native left/right arrow editing.
Same-menu refreshes preserve the highlighted choice.
Game Menu Tab/Shift+Tab cycles through every button and slider in display order,
wrapping within the menu. Arrows navigate controls; focused sliders use left/right
to adjust volume and up/down to leave the slider. Button focus uses a blue-gray fill;
focused/hovered sliders use a yellow block handle and brighter track fill.
`MenuShortcuts` assigns unique letter/modifier pairs, preferring first
letters and then subordinate letters. `MnemonicButton` underlines the assigned character; rare
Shift fallbacks show a visible hint. Number shortcuts are not supported.
Shift+click or Shift+Enter inspects a dialog option without activating it; plain Enter still activates.
`SessionChoice.Help` supplies registered building/road glyphs, descriptions, current-funded upkeep,
cost, capacity, radius/strength and requirements. Ring icons come from `AreaOfEffect.ForBuilding`,
not a separate effect list. Other choices have a text fallback. The independently scrolling help
panel stays on the right, with compact proportional widths in small windows; Close help does not
change menu focus/history. Shift+letter mnemonics and ordinary selection Shift+click are unchanged.
The guide's Glossary tab lists current terrain, feature, zone, road and building content (including
footprint art), actual area-of-effect icons, and the shared `EffectGlyphs` with their meanings.
`GameSession` retains parent prompts, input text and selected indexes. Cancellation pops one parent;
successful actions explicitly close the menu chain. Placement previews retain their originating
prompt/history and restore it only on cancellation, not successful confirmation.

Native input routes gameplay before button focus, but preserves path/name editing and modal guards.
Pan uses mouse drag, wheel or fractional `InputEventPanGesture` accumulation.
Ctrl/Command zoom keys accept logical, physical and Unicode forms. Magnify/pinch factors accumulate
logarithmically at a 20% threshold per step. Gesture state resets on keyboard input, dialogs and focus loss.
Dialogs capture otherwise unmatched keys and zoom gestures rather than dispatching gameplay.
Esc opens the configuration-focused Game menu during gameplay; keyboard shortcuts retain access
to save/load, budget, reports and restart. Esc returns to the parent prompt (or closes a root prompt)
and cancels active editing/previews.
Selection drags always edge-scroll; hover edge scrolling is optional.

## Rendering, animation and music

The map uses cached native draw commands, a reusable visible-cell buffer and `QueueRedraw`.
There is no node per glyph and no cell rebuild on every rendered frame. Overlays reuse base visuals.
`TerminalGrid` caches animation eligibility: a salted coordinate hash selects about 10% of hills,
trees and water. Hills move vertically; trees/water horizontally in four held 80 BPM steps.
Directions alternate by world-coordinate parity. Glyph offsets do not change backgrounds/hit targets.
Animation continues during gameplay pause, freezes while unfocused and leaves bridges static.
Successful session placements are explicitly distinguished from wholesale map
rewrites, so zoning more than 1,500 visible cells still animates instead of resyncing.
Large zone ripples sample up to 600 cells across the entire changed area. Wholesale
loads/new games still resync silently, and disabled/reduced-motion effects stay disabled.

`CityMinimap` caches the shared `MinimapImage` by game/version. Its image is at most 240 pixels wide
and neither axis exceeds the source map, avoiding empty sampling bands on small maps.
Linear texture filtering smooths scaling. The camera outline/navigation uses the actual view rectangle.
Overlay count/occupancy buffers are reused.

`MusicRegistry` contains Greensleeves and five original modal themes: Willow Court, Cloister Light,
The Evening Reeve, River Lanterns and Orchard Vigil. `MusicSong` stores immutable validated melody,
chord, meter, tempo and voice definitions. Register new scores there; do not add another player
or a song-specific synthesis loop. `MedievalMusicSynth` is the shared 22,050 Hz mono PCM16
synthesizer with gentle wind, strings and soft-brass colors, alternating phrase voicing,
sustained harmonies, soft plucked accompaniment and a restrained quarter-note running bass.
Scores use 48-56 BPM, fade to silence at their boundaries, and fit the 8 MB-per-score budget.
`MusicSequence` shuffles every registered song, avoiding adjacent repeats across shuffle cycles.
Greensleeves' public rendering/phrase APIs remain compatibility wrappers around the same synthesizer.

`AudioStreamGenerator` streams small reusable buffers instead of looping a fixed WAV. A single
background producer fills its one-second ring buffer every 10 ms, independently of map rendering,
zooming and scrolling. It accesses only the audio playback resource, not scene nodes. One future
phrase is synthesized off-thread while the current one plays, with an RNG independent of gameplay.
Full-volume music is -24 dB and placement sounds are -12 dB; smoke tests use -80 dB.
Missing volume preferences default to 70% music and 60% sound without overwriting saved choices.
Esc > Audio controls has independent 0-100% sliders and mute buttons; Esc > Music and Sound
also toggle their respective channels. Preferences are saved alongside font size in `user://display.cfg`.
Older settings without a sound toggle inherit the old shared mute state once.
Music pauses on focus loss, not gameplay pause/dialogs. Worker failures are reported on the main
thread; cancellation and joining precede releasing playback resources at shutdown.

Monuments are registered building types with `IsMonument`/`DebugOnly` metadata. The first is an
original Bewildered Duck stone statue holding its helmet, occupying 8x6 cells with a raised
sculpture and stepped plinth. It is not imported or traced character artwork. Its normal gate
is 100,000 residents, using saved `HighestPopulation`; Debug builds expose a Monuments catalog
and allow immediate free placement so it can be inspected without developing a city first.
Release builds hide and reject debug-only placement. Footprints, picking, demolition and saving
use the normal building infrastructure; monuments do not masquerade as civic services.
Release loading explicitly rejects saves containing the debug-only prototype rather than
silently removing it or exposing unfinished content.
The Debug minimum window height is 800 logical pixels (Release retains 780) so the additional
monument icon fits without pushing the status row outside the window or changing terminal padding.
The engine smoke test checks underruns through a 1.5-second render-thread stall and repeated zoom/pan.
No external recording or package is needed.

`AreaOfEffect` supplies service and pollution radii (sharing `CityServices.PollutionRadius`) and
cached pixelated outlines in map-cell space. `TerminalMap` draws faint white rings at every zoom
only for buildings whose footprints intersect the active selection (or cursor block), culls offscreen
ranges, includes valid placement previews and labels multiple effects side by side.
Ring alpha is 0.38; icon alpha is 1.0.
Utilities with citywide supply and no spatial radius do not have a fictitious range circle.

`BuildingSprite` generates original classic-city-map-inspired pixel artwork from an elevated
oblique view: broad shaded roof planes, short walls, compact walled courtyards, planted home
plots and paved civic/workshop yards. Medieval details include thatch, timber framing,
market awnings, mill wheels, forge chimneys, cloisters, aqueduct channels and battlements.
Church and castle towers are compact volumes rather than stretched front-facing columns.
Sprite lift is a fixed eight pixels across building levels; denser plots grow wider or
add wings instead of increasing presentation height. Sprites extend above
their logical footprints and draw in south-edge order for depth. `TerminalMap` caches
nearest-filtered textures per type/footprint/variation at every zoom, after roads and paths.
Placement still uses the logical footprint; mouse picking tests the full sprite alpha
front-to-back, including raised roofs and overhangs. Transparent pixels pass through.
`TerminalGrid` suppresses building glyphs; overlays and selection remain available.
`BuildingType.FootprintArt` and `MapSnapshot` retain terminal art for text/effect fallbacks;
the minimap and `--dump-map` retain canonical glyphs.
Compact residential models use restrained varied thatch/tile roofs, timber and floor bands,
readable doors/shutters and small chimneys within the actual 1x1–2x2 plots.
`WorkplaceAppearance` distinguishes stalls, shopfronts, smithies, carpenters/coopers, mills
and forges with period-appropriate props rather than generic factory boxes.

`RenderedModelPreview` replaces road/building glyph illustrations in construction choices,
selected-model panels, contextual choice help and milestone unlocks. A shared
`RenderedModelPreviewCache` renders canonical `PerspectiveCity` geometry in an isolated,
paused staging city, captures static nearest-filtered thumbnails and reuses them by subject.
Only one staging viewport renders at a time; the real city, its RNG and simulation are untouched.
Headless mode exposes subject metadata without waiting for unavailable draw signals or
claiming a graphical texture is ready. All catalog frames retain terminal styling and keyboard focus.
`TerrainSprite` supplies world-anchored pixel ground chunks and raised natural-object
sprites. Meadow uses quiet mottled grass, hills have shaded earth ledges, and water
has banks and a four-phase shimmer. Terrain textures cache by map terrain revision,
chunk, stride and water phase; roads do not inherit terrain motion.
Open and closed `RoadCurves` drafts share straightening before smoothing, so loops
do not turn their diagonal grid staircases into ripples.

`PerspectiveCity` supplies the default tilted orthographic 3D view in a supersampled
`SubViewport` with 8x MSAA, linearly downsampled into `TerminalMap`. Each render axis is
twice the native physical panel extent: four shaded pixels per displayed pixel before
MSAA coverage. Objects and roads use linear mipmapped anisotropic textures.
TURN LEFT/RIGHT rotate in 45-degree
steps; TILT alternates 35/50-degree elevations. OVERHEAD retains the sprite renderer.
The initial azimuth is 325 degrees (mirrored from the former 35-degree angle), so the
buildings' front faces project toward screen-right without flipping logical map coordinates.
The scene reads the same map, footprints, curved roads, path wear, overlays and selection.
Visible ground chunks reuse pixel terrain textures, while mesh walls and pitched roof
planes can be viewed from every side. Building height is based on type/density, never
footprint height; large plots expand roofs, courts and walls rather than stretching towers.
Ground rays drive placement/dragging/panning; nearest mesh-volume/roof-triangle hits drive
building selection. Valid placement shows a green-tinted full building model, excluded
from picking until it is constructed. View mode and angle are presentation state, not simulation mutations.
`--perspective-smoke-test` checks rotation, zoom, ground projection, building picking and
bounded tower proportions without running the unrelated desktop input stress checks.
Perspective rendering batches box geometry into a `MultiMesh` per material and roof faces
into shared meshes; forests and roads do not create one scene node per component.
Terrain mesh/texture chunks are cached independently. A buffered visible region prevents
small pans from rebuilding geometry, while selection/placement/range indicators use a separate
small layer. The viewport redraws on changes rather than rendering an unchanged scene continuously.
Camera caching includes viewport cell dimensions, and changed views render for a few
settling frames before stopping. This prevents zoom/resize updates from leaving a stale
or blank subviewport frame; graphical captures wait for those frames before reading pixels.
The graphical performance check covers every zoom, selection, buffered panning and cache-boundary
loads, with a 512-node ceiling, 50ms CPU interaction budget and 60ms frame-p90 budget.
`PerspectiveCity.BuildingCache.cs` retains up to 4,096 completed building models
across scroll-buffer rebuilds, including world-space transforms, roof normals/UVs,
picking volumes, highlights and authored-tree diagnostics. Footprint, type and
rotation must match; map replacement, terrain, roads or founding bounds invalidate
the cache. Non-geometric city updates do not throw away unchanged models.
Foundations near an incompletely loaded buffer edge are not cached. City instance
transforms/colors are uploaded in bulk without changing materials, native resolution,
supersampling or antialiasing. Repeated stationary synchronization reuses camera
corner projections, refreshing them after streamed ground changes.

The performance smoke now includes a 90-frame continuous planar drag that crosses
a buffer boundary, rather than measuring only buffered pans. It reports worst
input/sync time, p90 and worst frame, rebuild count, reused buildings and GC events
for frames over 60ms. It also compares retained building bounds/roof triangles
against fresh rebuilds and reads native MultiMesh transforms/colors back for
validation. Existing thresholds remain unchanged.

On Apple M2 / GL Compatibility at 960x800, populated SF's continuous-scroll
baseline was 159.0ms worst synchronization, 23.8ms frame p90 and 190.8ms worst
frame. The standalone verified run measured **52.8ms synchronization, 19.3ms p90
and 73.0ms worst frame**, reusing 1,897 buildings. An earlier standalone repeat
measured 59.0ms/18.0ms/74.3ms. A run alongside unit tests recorded a 325.6ms
worst-frame outlier despite 54.7ms synchronization; retain that result rather
than claiming all skipping is eliminated.
After post-stream corner-projection invalidation was added, the final SF repeat
measured 59.8ms synchronization, 18.7ms p90 and 74.2ms worst frame; minimap latency
was 466.2ms and road preview 107.8ms, still over their unchanged 250ms/100ms budgets.
The final large random-map drag measured 17.9ms p90 and 25.1ms worst frame.
The existing minimap/road budgets still have overruns: large minimap 251.9ms
against 250ms; SF minimap 459.6ms against 250ms and road preview 145.8ms against
100ms. Cached/fresh geometry, native instance/color checks, building artwork and
interface/archer/troop graphical smokes passed. The broader simulation run still
reports the previously documented seed-1 starter-battle duration failure.
`PerspectiveCity.Magnification` uses 1.9 for the normal view and 3.2 for the close
view. Normal is eased back 5% from the previous 2.0 scale, while the close view stays
at 3.2. New desktop games and restarted/new sessions start at zoom level 1 (the nominal
2x control); loaded games retain saved camera/zoom state. The visual step is about 1.68. Overview zoom levels retain the 1.8 baseline divided by stride.
The nominal 1x/2x controls, UI scaling, legacy overhead grid, cell selection strides
and camera-aware picking remain unchanged. The supersampling factor changes image
quality, not world magnification.
Render sizing uses the effective canvas-to-framebuffer transform and rounds physical
extents up before supersampling. Do not multiply the OS Retina scale into an already
physical framebuffer a second time. Targets exceeding the 16384-pixel texture limit
fail explicitly rather than silently dropping below native resolution.
`ScreenPoint` and input remain in logical map coordinates. `CaptureDisplayImage`
returns a downsampled image in those coordinates for renderer probes; thumbnail
captures use `DisplayRegionToRender` to crop the high-resolution source without
clipping roofs or overhangs. Only the 3D map composite uses linear sampling; terminal
fonts, double borders and the legacy overhead renderer keep their existing treatment.
Model-preview controls fit and clip their textures to the allotted rectangle.
Construction-list thumbnails occupy a fixed inset square above capacity text,
not the full button height; row clipping and the compact rail inset keep cached
high-resolution screenshots from painting over borders, labels or adjacent rows.
Selecting or placing a mounted drop defense shades its exterior station footprints,
independently of AREAS. `DropCoverage` excludes screened/out-of-range patches using
the same eligibility rules, and perspective patches follow the rendered ground.
Each patch is clipped against the actual cached terrain-top triangles and lifted
0.035 units above that triangle's plane. Terrace discontinuities split the overlay
instead of stretching a triangle between corner samples; vertical cliff faces are
excluded. The native defense check measures exact covered area across two levels.
Other weapons show maximum-reach perimeter rings/minimum-range holes, not a filled
promise of unobstructed coverage. The map labels maximum reach without drawing
target-connecting aim lines; actual projectiles remain visible in both map views.
Facing/topology are included in coverage cache keys.
Overhead uses matching drop polygons and maximum-reach rings,
and service-response guides remain screen-space circles.
Selecting one fighter also displays a maximum bow/contact-reach outline and an
explicit clear-shot/contact-validation label, without a target-connecting line; guard reach uses the same
body-aware helper as combat.
The terrain shader filters visual colors independently of nearest-sampled categorical
color data and flow vectors, preserving water, shore and travel semantics.
`RenderingQualitySmoke` compares actual final-output edge coverage against the old
native-resolution/2x-MSAA baseline, checks canvas-scale changes without rebuilding
geometry and saves matched silhouette captures. Existing large-map performance
limits are unchanged; quality-first GPU costs and overruns must be reported.
Perspective range rings draw as antialiased screen-space circles anchored at their projected
source and scaled by camera magnification. They do not inherit camera foreshortening or
terminal-cell 12/22 compensation, and remain approximate timely-response guides.
`TerrainSprite` shades meadow with world-anchored 4x8-pixel quantized patches over
a low-frequency field, using five subdued palette steps. Open water uses matching
4x8-pixel blue blocks with phase-based variation, not single-pixel wave lines.
Tree canopy materials use original 16x16 textures containing 4x4-pixel leaf clusters;
overhead tree silhouettes use coarse clustered shades as well. Water uses a weighted neighboring
cell-density field and bilinear contour shading to round banks while keeping thin channels
and isolated ponds. Shoreline mesh heights interpolate at shared vertices instead of exposing
individual recessed water-cell walls; terrain layers and buildability remain unchanged.
Medieval material cues use original 16x16 procedural pixel textures for mortar, timber,
plaster, thatch and roof tiles. Timber framing is instanced in the same material batches,
not separate scene objects, so additional facade detail retains the large-map performance model.
Reference games inform broad camera, palette and atmosphere choices only. No reference-game
sprites, textures, screenshots, models or traced artwork are bundled; these materials and
building volumes are generated by the project's own rendering code.
Graphical `--perspective-smoke-test --capture <path>` also writes `.river.png` and
`.ranges.png` companion captures for the smoothed diagonal bank and circular guides.
`.zoom05.png` and `.zoom2.png` capture the 0.5x/2x views; the smoke checks roof pixels
after actual zoom-control transitions, not merely scene counts or picking metadata.
Subtle voxel detail uses the same material batches: seeded broadleaf/conifer canopies,
scaled boulder clusters, well coping, aqueduct pier bases, granary supports, roof ridge
blocks and castle cornices/crenellations. `TerrainSprite.Elevation` keeps meadow at 0,
hills from 0.3 to 1.35 world units; water uses a separate gently graded surface
below meadow height, never hill terraces. Cached terrain meshes close the step faces.
Ground picking intersects those visual elevations without changing logical terrain rules.
Hill interiors now rise through four terraces: a readable 0.3-unit grassy base
and three prominent 0.35-unit steps, reaching 1.35 units. The lowest exposed banks
use dark green/earth shading rather than thin tan strips. Flat green tops retain
the ordinary meadow palette; exposed faces use cached, earthy horizontal strata,
not the top's grass texture. Buildings spanning levels sit on level foundations
at their plot's highest terrace rather than disappearing into the hillside.
A cached breadth-first
distance from each hill region's boundary determines height; terrain edits invalidate
the cache, and save/load derives identical terraces from the saved terrain layout.
Coastal singleton/sliver caps collapse visually without changing logical terrain.
Higher terrace tops keep level edges beside water rather than easing down toward
the next terrace; bank faces close the vertical step. Smoothed shoreline-cap
classification and road profiles remain unchanged by the blocky visible shore.
Perspective roads are dedicated meshes, not pixels painted onto terrain.
The sidebar's TILT and ROTATE buttons sit above the minimap zoom controls.
Each button has its own single white border on black; there is no overhead toggle.
TILT cycles through 20, 35 and 50 degrees; ROTATE advances clockwise in 90-degree
steps, wrapping after four clicks. Either button activates the 3D view when
used from overhead. Modal prompts and active placement previews block these
actions, matching sidebar zoom behavior.
The `[` and `]` keys rotate 90 degrees left and right, respectively, with the
same modal/placement guards as the sidebar controls.
`RoadMeshLayout` converts the existing `RoadPath` centerlines to square world
coordinates and simplifies them within 0.2 terminal pixels. Centerline heights
are sampled at most one cell apart before smoothing, so sparse flat stretches
cannot stretch a summit transition into a long shallow trench. Stable terrace
samples within 0.06 units settle back onto the terrace; uniform straight
stretches then coalesce up to six cells. Smoothed centerline heights define the shared
road grade, including raised water crossings. Offset edges define a one-cell local
carriageway or four-cell King's Road, with a 0.12-cell shoulder on either side; joins
share their edge vertices. Grade lookup buckets cover the full miter-support reach,
including exact map/cell boundaries. Uniform-height shortcuts cannot report support
outside that reach.
Paved roads have shallow raised stone edging; dirt roads have a wider, low earthy verge,
and wooden bridges retain timber-colored edge beams. These bands lie inside the road bed,
reuse the pixel materials and cached chunk mesh, and stop at ordinary intersections.
Their actual triangles participate in picking; there are no per-stone scene nodes.
Curved curb fragments interpolate shared inner/outer miter endpoints rather than
offsetting cell-clipped fragments independently. Every touched pavement cell clips
the complete nearby border bands, including bands whose outer edge lies in the next
cell, so diagonal cell crossings cannot lose triangular wedges. Bands split at cell
and road-intersection boundaries before exposed-edge classification. Candidates are
cached per cell/road type rather than recomputed for each pavement fragment; normal
junction openings remain clear and eligible straight strips still coalesce. The perspective
smoke samples actual emitted curb heights around bends/intersections at both zooms
and all four camera headings.
Acute unsupported terrace tips beside
curves are trimmed into supported road grade; broad hill remnants remain intact.
Shared cell edges split at road-grade stations so shoulders and terrace faces meet
without height-interpolation cracks. Bridge approaches close cut faces below the deck
while retaining terrain underneath.
Normal/close-zoom road selection remains one physical map cell, with both halves of a
two-cell highway independently selectable, including bridge decks and rails. Road clicks
must not snap to the logical centerline. At 0.5x/0.25x, a single
coarse selection follows a road-centered ribbon at the zoom's block size so its
cross-section contains the whole highway. `RoadPath.SourceCells` retains logical network ownership
separately from selection. Pointer inspection uses the actual picked surface point; this is not a nearby-road
status fallback. Generic terrain/placement picking remains in physical map-cell space.
Road junctions are ordinary intersections regardless of rank. The former
`Ramp`/`AlongHost` shaping is retired: an acute minor road is not redirected
along a highway as an invented merge ramp. Flat shoulders keep the terrain
material, avoiding decorative tan strips around level intersections.
`TravelRoutes.JunctionDelayUnits` derives a bounded delay for shallow-angle
minor-road approaches from their actual local centerline tangents, not a long
grid-route chord around a traffic circle. It affects
actual least-time travel, is cached until road/terrain edits, and imposes no
extra delay on equal-rank traffic-circle connections.

`RoadSurfaceMeshes` partitions each cell into nonoverlapping road, shoulder and
terrain polygons using Godot's polygon intersection/difference tools. Road types
have deterministic priority at crossings. All surfaces share one world-coordinate
height query, so junctions do not stack coplanar road strips. The remaining
terrain keeps its original terrace height, and explicit cut/fill walls connect
the corridor boundary to it. Road ends, curves, closed loops and junctions use
the same geometry path; the renderer checks that the partitions cover each cell
within 0.001 square cells without holes or overlaps. Bridge corridors retain
the ground/water below; their extra projected surface area is accounted for
separately. Thin retained terrace polygons under 0.25 cells wide are lowered into
supported road grade. Shoulders restore up to 0.12 units of terrain height across
their actual width, meeting shallow cuts/fills flush rather than leaving thin
vertical skirts. Substantial hilltops are not lowered just because their grade
difference is small. Cut faces, terrace seams and retained picking triangles use
the same shoulder support heights. Shoulder restoration fades out for substantial
cuts (0.24 units and above), retaining solid earth faces instead of small bevel
wedges at each terrace. Before triangulation, adjoining road/shoulder/terrain
polygons share boundary vertices, including cuts from neighboring cells. Triangles
and bank walls are split at those vertices so nonlinear grade samples cannot leave
T-junction cracks between an incline and its supporting ground.
Undeformed level-road cells keep the direct triangulation fast path; shared-edge
refinement is reserved for graded terrain, bridge decks and riverbanks.
On hills, all road shoulders and cleared terrace remnants use the earthy road
material, even when flush with the terrace. They never sample the grass atlas;
uncut terrain outside the road corridor retains its grass. Flat meadow
intersections still use grass shoulders.
Where a high terrace edge meets a road cutting, convex tips are excavated along
a bounded eight-segment quadratic curve. Only exposed terrace/corridor joins are
rounded, not ordinary cell seams or untouched hill corners. The curved clearance
uses the same earth surface, cut walls and picking triangles as other remnants.

Water crossings receive bridge decks, parapets/posts and piers. A short valley
with at least a 0.35-unit drop between banks is bridged when its span is no more
than eight world cells, rather than following a steep descent and ascent.
Bridge shape is derived from the same centerline edges and grade query;
logical roads, construction cost rules and saved road layers remain unchanged.
Deck edges have solid 0.12-unit vertical fascia following the exact top mesh,
not an infinitely thin sheet. Railings use thicker 0.13-by-0.18-unit members and
posts spaced two world cells along each continuous span, with restrained
unstriped wood/stone colors rather than fine bright railing textures.
Deck-side triangles are retained in the ground-picking cache. Rails and posts use exact transformed
box intersections; the nearest visible hit resolves to a road cell on the owning bridge path,
instead of selecting adjacent water. Graphical/headless smoke covers railing clicks at four headings.
Abutment excavation checks the clipped deck corridor's vertices and edge
midpoints, not only the logical cell center, so a sloped or curved bridge approach
cannot retain hill grass that protrudes through one edge of the carriageway.

Road and shoulder meshes use nearest-filtered original pixel materials with
restrained stone/dirt variation and clean antialiased silhouettes. They are cached
in 16-cell chunks alongside terrain and bank meshes. Terrain/road revisions
invalidate the appropriate derived geometry; selection and buffered pans do not
rebuild it. Picking retains the actual road, shoulder and terrain triangles, and
selection outlines follow those same surfaces. Graphical smoke scans the
road-cut corridor for exposed background pixels.

The inspector uses the same `RoadMeshLayout` carriageway footprint at the
selected cell center, cached per road-network revision. Bare selected terrain
does not report a road just because an old logical route cell exists there;
visible road meshes crossing other cells are identified too. Picking and the
outline use the same retained mesh triangles. Logical placement, demolition, walking/travel costs and save
layers are unchanged. The overhead renderer retains its curved raster road layer.

The Roads catalogs include `RoadTemplate.TrafficCircle`: one atomic 5x5 cobbled
ring, nine grassy center cells and four cardinal connection points. A fixed
placement preview follows the cursor; clicks/Enter confirm, Escape cancels.
The entire dry, unzoned plot is validated before spending; existing roads in
the center must be cleared first, and higher-ranked perimeter roads are not
downgraded. Placement is one undoable action and saves as ordinary road cells.

`WaterFlowField` derives deterministic downstream directions toward broad water
bodies/outlets. Each river step reduces distance to its outlet, preventing
cycles. Equal-distance routes prefer similar bank clearance and a short
bank-aware lookahead, so ripples travel along the channel rather than across
it toward an arbitrary shortest-path parent. Outlet distances, classification
and rendered water levels are unchanged by this direction preference.
Adjacent water levels change by at most 0.001 units, with total channel
relief capped at 0.12. Lakes stay level and no waterfalls are generated by
default. Water heights never use hill levels, and shoreline blue does not spill
onto raised hilltops. Generated/scenario river identity is retained in an optional
compressed save layer, so a wide river does not become a still lake simply because
its center is several cells from the bank. River paths drain toward classified
sea or broad non-river bodies. A cached flow atlas carries incoming/outgoing directions
and outlet-distance phase. `RiverRippleLayout` defines quadratic tracks joining
water-cell edge midpoints, rounding turns without moving the shoreline or terrain.
Sparse chunky ripple accents travel downstream at 0.75 world cells per second,
with eight updates per second and one active cell per roughly eight distance steps.
Deterministic per-wave offsets vary arrival intervals while preserving continuous tracks across
cells and chunks; cluster sizes and brightness also vary. The float flow atlas retains full outlet
distance so wave identities do not repeat at every eight-cell boundary.
The coarse base-water texture stays stationary; most pixels do not animate. The existing
muted water palette remains unchanged. All cached chunks use the same rendered
flow clock, including chunks first revealed by panning.
Decorative river motion continues while gameplay is paused, without advancing the simulation.
`RiverActivityLayout` schedules sparse, staggered splashes and fish jumps independently of
simulation time. A bounded set of at most 24 open-water sites, one per eight-cell region,
uses a single reusable colored cube multimesh for fish bodies/tails and chunky splash droplets.
Candidate sites are cached with terrain; camera changes select the visible nearest sites
without rescanning terrain or letting an off-screen buffered region consume the activity limit.
Fish follow a short raised arc and end in a splash; road/bridge corridors are excluded.
Effects-off and reduced-motion modes freeze flow and hide fish/splashes without rebuilding terrain.
`ShorelineLayout.BankCell` excludes hilltops, preserving their grass palette beside rivers;
the existing earthy cliff faces and lowland bank coloring remain intact.
Road shoulders share one canonical height for each world-space vertex, including cell/chunk
boundaries, so terrace seams cannot create brown wedges inside the tan incline. Terrace-remnant
cleanup is limited to the shoreline's land polygons; water beneath bridge approaches is never
replaced by an elevated shoulder. Natural generated crossings are scanned for background gaps
at four headings, in addition to constructed bridge, shoreline and hill-road fixtures.
Render-on-demand retains two actual settling frames before disabling the viewport
on the following tick, so a flow-clock update cannot be canceled before drawing.
Terrain/road/bank triangle buffers upload their vertex attributes in batches
rather than issuing native calls for every vertex.

`TreeAppearance` supplies deterministic model parts for oak, rounded, tall and spreading
broad-leaf crowns and two tapered, overlapping-tier pine silhouettes. Coordinates select
the shape, proportions and broad-leaf palette; pines use darker evergreen colors.
`PerspectiveCity` batches these parts through the same box/material path for natural
features and planted service-building trees, retaining preview tint, rotation and picking.
Foliage keeps the existing nearest-filtered four-pixel texture clusters. Feature generation,
save data, terrain and legacy glyphs are unchanged.

Tests tied solely to retired painted roads, ASCII map elements, highway merge
ramps and glyph-terrain animation have explicit skip reasons. Dedicated mesh,
partition coverage, geometry, selection, traffic-circle, bridge and flow tests
remain active; gameplay, saves and terminal UI tests are not disabled.
The perspective mesh uses `ShorelineLayout.BlockyCell`: whole-cell, axis-aligned
land/water blocks matching the hill terraces. Land tops retain their own level
height; closed earthy faces descend to the water at block boundaries.
Water-to-water boundaries never produce earth walls. The smoothed density field
and `ShorelineLayout.Cell` remain unchanged for coastal-cap suppression and
feature clearance, so changing the visible water
edge does not change road centerlines, terrain costs or saved-map data.
Road bridge classification follows logical whole-cell water too: dry bank
protrusions must not acquire short one-sided bridge decks from smoothed density.
Dead-end curve padding stays inside its dry cell when the outward neighbor is
water; other road curves and actual complete crossings retain their geometry.
Ground mesh surface markers keep water blue and land green independently of raster atlas colors.
Perspective atlases disable baked shoreline sand/blending: grass and water stay
pure, with the block mesh defining their boundary. Bank tint uses a binary
`ShorelineLayout.BankCell` mask for land cardinally adjoining logical water,
sampled at cell centers. Straight and stepped edges share the same earthy
palette rather than varying with the old smoothed density field. Diagonal-only
contacts and off-map atlas padding do not receive bank tint.
Only classified sea/ocean coasts have sandy beaches: selected 12-cell coastal
regions extend sand up to 3.5 cells inland, with a 0.25-cell inland transition.
The narrow water-edge slope is separate from the level sandy area. Rivers and
lakes never receive beaches. Sea identity is recorded by the water generator's
body IDs and coastal scenario definitions, persisted as an optional compressed
save layer; older saves without it remain valid and do not guess sea identity.
An 18×18 padded, cell-centered shoreline atlas supplies density and coast distance
independently of mesh tessellation, without triangular color wedges.
Raised hills retain cliffs; beaches do not change buildability or river flow.
Beach vertices carry an explicit land/sand marker even below ground
zero so they cannot be misclassified as water. Coastal building foundations
extend to the actual rendered surface while preserving their level plot height.
Beach eligibility samples cell edges and corners as well as centers, keeping
the inland slope continuous where the density field ends. Both sides retain
shared half-cell edge vertices, avoiding T-junction gaps along slanted banks.
Regression checks compare actual triangle heights on adjoining cell edges,
not just the beach's interior pixels. Natural feature
generation and rendering both require the complete feature footprint to remain
on the land side of the visible contour; existing saved shoreline trees are
suppressed rather than drawn standing in water.
Coastal regions coalesce their internal sample edges before triangulation.
Uncut ground skips road polygon work; convex road intersections use bounded
half-plane clipping, with Godot polygon clipping retained for concave pieces.
Material-state caching avoids redundant native color updates for identical
vertices. The full projected cell-coverage and performance limits remain active.
Land cliff intersections are split
at the road/corridor boundaries and use one region classification for both ends,
preventing black triangular gaps where cuts meet terrace faces.
Road shoulders next to water use the land-side support height, not a
threshold-sensitive land/water corner classification. Shore walls share all
surface-edge splits and reach the actual shoulder height for both cut and fill,
closing the road/shore join rather than leaving unsupported thin surface strips.
Narrow rounded hill cuts retain their connecting wall even when fixed-width
side probes both miss the cleared strip. The remaining-terrain edge's midpoint
identifies that cut boundary directly. Diagonal hill-road smoke checks include
near-endpoint wall samples, not just the middle of broad terrace faces.
Wide-water shoreline masks use a five-cell binomial filter rather than a strongly
center-weighted three-cell filter, suppressing repeated scalloped riverbanks.
Sparse three-cell neighborhoods retain narrow-stream/isolated-pond visibility.
New river generation stamps the continuous centerline with slowly varying widths,
not rounded-cell centers and abrupt two-size stamps. Existing saves retain their
logical water layout while benefiting from the wider visual shoreline smoothing.
Meanders use a quintic endpoint envelope with continuous first and second
derivatives, rather than a piecewise-linear taper that introduces opposing bank
points where it meets the main channel. Broad bends use a lower-frequency course
field; endpoint locations and river-connection validation remain unchanged.
Flat meadow chunks without roads use two triangles instead of per-cell tessellation.
Shared shoreline corner values are sampled once per chunk. Unshaded terrain/cliff
meshes omit unused normal generation. Perspective performance smoke exercises actual
minimap clicks to uncached destinations and measures through the rendered frame,
with a 250 ms click-to-render budget. Land and open-water blocks use packed span
fills; shoreline density samples are lazily cached and invalidated by terrain edits.
Perspective hill tops omit legacy tan edge strokes: separate layered cliff geometry
already depicts changes in height. A fixed northwest light softly brightens raised
grass tops, casts broad green contact shadows on lower neighboring terraces, and
gives opposing cliff faces different brightness. This shading is baked into cached
textures/vertex colors, preserving render-on-demand and avoiding shadow-map overhead.
Grass and water retain richer green and blue/teal five-step palettes; hill shadows
scale those colors rather than mixing in gray, preserving saturation and coarse patches.
Selection and placement outlines retain a two-pixel nominal thickness across zoom
levels and resize events. Their footprint follows the active selection/zoom block,
and their edges follow the rendered ground triangles (including road ramps and
cuttings) instead of floating at a fixed or maximum original cell elevation.
Original rocks have six deterministic silhouettes
(boulder, slab, standing stone, split pair, scatter and layered outcrop), six subdued
stone palettes, and additional scale/rotation variation in perspective. Natural
features use the same whole-cell curved-road-bed clearance as buildings in both views,
preventing trees/rocks from occupying the visible curve outside logical road cells.
Riverbank corner heights are shared by every adjoining ground tile. Cliff walls
join those actual top/bottom corner heights, rather than nominal cell elevations,
so stepped hills meeting smoothed banks cannot leave dark holes in the terrain.
Perspective mode enables `GameSession.CameraEdgePadding`, allowing half a viewport beyond
each boundary so map edges can reach the view center. Overhead retains its original clamp.
Saved views explicitly record this mode to validate negative camera positions; older saves
retain their original behavior. The minimap clips its camera rectangle to actual map bounds.
Perspective drags anchor to a continuous, level ground plane rather than a picked terrain
cell. The renderer retains the fractional camera remainder between input events, avoiding
rotated stair-step motion during slow drags. Trackpad panning uses the same screen-space
translation. Terrain-aware picking and grid-aligned overhead panning remain unchanged.
The indicator batch is flushed independently of whether selected services have range rings.
`BuildingDetailLayout` supplies centered facade bays and symmetric detail spacing.
Glazing stays between the timber posts; story rows avoid floor bands and the central door.
Tower bodies are 0.6 units wide with 0.66-unit caps and eight spaced square perimeter merlons;
curtain-wall blocks stop before the tower corners. Small merlons/caps use plain stone colors
to avoid high-frequency mortar aliasing at normal zoom. Palisaded mottes do not use crowded
stone-style corner towers, and the small gaol uses a separated two-tower gate frontage.
Watch House, Apothecary, Chapel and Tavern now place as 2x1 buildings. Saves accept their
prior explicit 1x1 footprints without expanding into neighboring cells; all other civic
footprint validation remains strict. Prices, capacities, unlocks and service targets are unchanged.

Grain management and Market Cross/Granary placement begin at the permanent Village
milestone (500). `CityGame.GrainRequired` gates imports and hunger effects; locked
grain appears as an unlock label instead of a warning gauge in the sidebar and reports.

The raw bundled DejaVu Sans Mono bytes load directly, without depending on an import cache.
Startup verifies registered map glyph coverage. Export presets include the font and
[DejaVu license](../godot/Assets/DejaVu-LICENSE.txt).

Terminal effects (glyphs that shrink, grow, burn and roam) are a separate engine-independent layer; see
[EFFECTS.md](EFFECTS.md).

## Persistence

`SaveGameStore` writes JSON version 2, atomically via `<path>.tmp` followed by replacement.
Development saves are not backward compatible: other versions, obsolete names, unknown fields,
missing state and unsupported compression are rejected explicitly rather than migrated or defaulted.
Godot supplies `user://quicksave.json` in the distinct `TermCityGodot` user-data directory.
The reusable core's fallback path remains local application data plus `TermCity/quicksave.json`;
the desktop application does not use that fallback.
`GameSession.SaveDisplayPath` abbreviates the home directory for the right-aligned bottom status label.
The label is visible only while a save-confirmation dialog is open (`SessionPrompt.IsSaveDialog`).
It shares a single row with cell information, uses at most half the row width, and exposes
the full absolute path in a tooltip. Save guards do not duplicate the path in prompt footers.

Saves include configuration/scenario, name, cash/calendar, RNG state, speed/pause/taxes, growth plan,
last report, the current week's birth/death/migration/event tally, milestones, guide dismissal,
pending dezone removals, building footprints, path wear, service establishment days and all map layers.
Terrain/features/buildings/road types use name palettes. Layers are deflate-compressed and
base64-encoded. Encoding resolves palette indexes once per registered type, not once per cell.
Terrain uses every byte ID, including 255; the empty-cell marker is only used by optional layers.
Load preserves saved configuration and clock timings, rebuilds sparse indexes and starts the session
paused. Medieval state (grain, harvest, hunger, outbreaks, town rank, tribute arrears) is required.
Classic remains an explicit sandbox ruleset, not a fallback for incomplete saves.

Autosave checks every 60 real seconds, rotates three sibling files and never overwrites the
quick-save. Unsaved-progress guards offer save/continue, discard/continue or cancel, and failed
saves block continuation. Failed loads leave the city unchanged and surface an error.

## Rules and extensions

Core tuning is in [GameConfig.cs](../src/TermCity.Core/Simulation/GameConfig.cs):

| Property | Default |
|---|---|
| MapWidth / MapHeight | 160 / 96 |
| Seed / Scenario | 1 / Random; startup picks a seed |
| StartingYear | Current local year minus 800, captured for new games |
| StartingMoney / RoadCostPerCell | 50,000 / 500 |
| MinResidentialCells | 10 |
| ResidentialPerCommercial / ResidentialPerIndustrial | 20 / 10 |
| DefaultTaxRate / RoadServiceReach | 0.09 / 2 |
| MaxNewResidential/Commercial/IndustrialPerWeek | 3 / 1 / 1 |
| GrowthRatePerWeek | 0.02 |
| WeeksPerYear / DaysPerWeek | 52 / 7 |
| Slow/Medium/FastSecondsPerWeek | 16.8 / 8.4 / 2.8 |

Add registered terrain/feature/building/road types through `GameContent`. Generators implement
their existing ordered interfaces. Each glyph must be covered by the bundled font.
Player-placeable roads/buildings appear automatically in area menus; growth buildings declare
their zone. Named scenarios require the default named Meadow/Hill/Water, Tree and road content,
plus growth buildings for every zone.

## Tests and CI

```bash
dotnet test TermCity.slnx -c Release
dotnet test tests/TermCity.Tests/TermCity.Tests.csproj \
  --filter "FullyQualifiedName~GodotPresentationTests|FullyQualifiedName~CityScenarioTests"
godot --headless --path godot -- --smoke-test --seed 42 --size large
godot --path godot -- --smoke-test --seed 42 --size SF --capture /absolute/path/city.png
```

Unit tests cover economy/growth, calendar, road networks, generation/water, named-city geography,
occupied districts/service, current-format save/restart round trips and invalid-save rejection, sessions/previews/undo/autosave guards,
minimap images, zoom/pixel mapping, animation density/rhythm, options, city names and synthesized music.
Pure Godot helpers are linked into the test project; running unit tests does not require a display.

[GodotSmoke.cs](../godot/GodotSmoke.cs) runs inside the engine and verifies scene layout, native
keyboard/pointer/trackpad/pinch routing, modal focus/errors, name editing, font/sidebar resizing,
menu highlight retention, save/load/restart, minimap dragging, pause/focus animation and streaming
music. It ignores saved display settings and uses an isolated smoke-save directory.
Success prints `TERMCITY_GODOT_SMOKE_OK`; failures log an error and exit nonzero.
Headless tests supply a logical viewport; screenshots require a graphical display.

CI runs on pull requests targeting main. Windows/Linux/macOS jobs build the complete Release
solution and run tests without rebuilding. A Linux Godot .NET job builds the desktop project and
runs headless engine checks. Unit coverage and engine checks are complementary.

## Export

Install Godot 4.7.2 .NET and matching .NET export templates from the
[official release](https://github.com/godotengine/godot/releases/tag/4.7.2-stable).
Presets in [export_presets.cfg](../godot/export_presets.cfg) support Windows x86-64, Linux x86-64
and macOS. Create the destination directories, then:

```bash
godot --headless --path godot --export-release "Windows" exports/windows/termcity-godot.exe
godot --headless --path godot --export-release "Linux" exports/linux/termcity-godot.x86_64
godot --headless --path godot --export-release "macOS" exports/macos/TermCityGodot.zip
```

Export paths are relative to the project directory when not absolute; prefer absolute output paths
if launching from a different working directory. macOS signing/distribution is a separate release step.
Retain the bundled font license in exports.

## Diagnostics and performance

F12 shows the last input and loop-gap measurements in the bottom status line.
`--dump-map` offers a UI-free generator check. Engine checks measure visible-cell rebuild timing
and assert that animation does not rebuild cells or mutate saves.

Profile .NET allocations and Godot native resources when investigating leaks or frame-time regressions.
Sparse indexes, lazy network/statistics caches, reusable map/minimap buffers and independent music
prefetching avoid repeated whole-map work. Keep invalid input and save/audio errors visible rather
than substituting silent success-shaped fallbacks.

### Wall fire and firefighting visuals

Wall fire anchors are derived from exposed fragment tops, including surviving
gateposts, crenellations and breach rubble, rather than a section-wide maximum height.
Cached anchors share the wall geometry's rotation and elevation. The fire's base
remains fixed to that surface; overlapping colored blocks change width, height,
tip shape and brightness at deterministic irregular intervals instead of bobbing.
Overhead fire shares the same geometry and flicker samples. Pausing and reduced
motion retain their existing frozen/static behavior.
Civilian crews first lift and tip alternating buckets, throwing connected, widening
sheets of water from the bucket lip along short arcs, rather than dotted particle trails.
The leading edge travels toward the fire before the trailing edge leaves the bucket;
an impact splash appears only once the water reaches the target. Shared arc segments
keep perspective and overhead representations continuous. Crews use buckets
while their repair target is burning. At the existing 33% extinguishing threshold
they switch to hammer-and-board repairs. This is a visual phase of the existing
health-restoration work, not an extra delay, resource cost or combat mechanic.
