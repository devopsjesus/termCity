# Implementation plan: memory-budgeted whole-map preparation

## Goal

Reduce exploration and minimap stalls by preparing map geometry progressively in
the background and retaining it within a memory budget.

Cache the whole map when it fits; do not render the whole map. Keep GPU residency
selective, startup responsive, and picking tied to published geometry.

The renderer implementation now includes managed CPU packets, byte-budgeted
retention, progressive whole-map preparation and diagnostic acceptance coverage.
See [the developer guide](./DEVELOPMENT.md#whole-map-cpu-retention) for lifecycle
contracts, the current 256 MiB CPU target and 128 MiB destination-GPU target,
reproducible commands and remaining
performance/validation findings. The requirements below preserve the original
handoff and its acceptance criteria; they are not an all-passing benchmark claim.
Existing branch work and unrelated uncommitted changes must remain preserved.

The subsequent [atomic minimap implementation](./DEVELOPMENT.md#atomic-minimap-presentation-and-retained-destinations)
adds a retained GPU picture during jumps, 128 MiB recent-destination native
residency, managed terrain/flow/shore texture packets and incremental ground-node
reconciliation. It keeps warm visible destinations reusable without regenerating
or reuploading them, while preserving the separate CPU/native budgets and the
original cold-readiness thresholds.

[Sustained traversal tuning](./DEVELOPMENT.md#sustained-minimap-traversal-tuning)
subsequently made frozen presentation diagnostic-only, restored immediate camera
feedback, limited native retention to useful revisit regions and added a one-second
navigation cooldown. These changes were selected from matched 24-jump ablations,
not short-return cache tests. Cold SF budget failures remain explicit.

[Preparation profiling](./DEVELOPMENT.md#preparation-profiling-and-full-zoom-out-traversal)
now runs 24 jumps at normal zoom **and 24 at maximum zoom-out**, with separate
terrain, geometry, encoding and upload measurements. Bounded shared road
dependencies, snapshot batching and direct buffer encoding preserve exact
geometry. Full-overview measurements expose severe remaining latency; they
must not be represented as passing normal-zoom results.

[Overview LOD diagnostics](./DEVELOPMENT.md#diagnostic-maximum-zoom-overview-lod)
now generate simplified houses, workplaces and trees directly at maximum zoom-out,
with detailed previews, separate budgeted CPU representations and published
geometry picking. The experiment improves paused dense-city scrolling but remains
opt-in; incomplete live fullscreen runs and remaining budgets are documented.

[TOP minimum-spec measurements](./DEVELOPMENT.md#top-minimum-spec-baseline-and-recent-region-experiments)
use a named 100,000-resident current-scale settlement with content civilians and
intact defenses on the 8 GiB M2 reference machine. Larger allowances, protected
recent routes, overview LOD and stable GPU terrain pages remain isolated
experiments. The measured failures and boundary-clipped overview route are not
an acceptance pass or a reason to weaken the original budgets.

## Existing implementation

Build on these components rather than replacing them:

- [Object chunks](../godot/PerspectiveCity.ObjectChunks.cs): 16x16 ownership
  chunks, local dependency stamps, cooperative artwork emission and bounded
  worker encoding.
- [Object uploads](../godot/PerspectiveCity.ObjectUploads.cs): shared/paged
  instance buffers, atomic publication and selective GPU residency.
- [Ground preparation](../godot/PerspectiveCity.GroundPreparation.cs):
  snapshot-based terrain/road workers and progressive mounting.
- [Path presentation](../godot/PerspectiveCity.Footpaths.cs): bounded indexed
  uploads and complete-mesh replacement.
- [Developer guide](./DEVELOPMENT.md): architecture, validation commands and
  existing performance findings.

Starting limitations (addressed by the implementation):

- Object retention uses a 256-chunk target rather than a byte budget.
- Prepared ground outside the requested window is discarded.
- Offscreen object preparation can still generate native ground resources
  through its ground-support dependency.
- Artwork emission is not entirely worker-safe; only later encoding stages
  run on workers. This remains deliberately cooperative, as permitted below;
  whole-map preparation does not move native operations onto workers.

## 1. Establish memory accounting and baselines

Measure separately:

- Retained managed object buffers, picking data, highlights and entrances.
- Prepared terrain/road packets and their picking geometry.
- Building-cache data and shared dependencies.
- In-flight snapshots, queues and temporary preparation buffers.
- CPU upload backing arrays and estimated GPU allocations.
- Process memory and available driver-memory telemetry.

Avoid double-counting shared buffers. Label estimates explicitly; encoded-buffer
bytes are not total memory usage. On Apple Silicon, CPU and GPU allocations
ultimately compete for unified system memory.

Record startup, cold/warm minimap readiness, frame latency, preparation throughput
and peak memory for:

| Fixture | Dimensions | Ownership chunks |
|---|---:|---:|
| Custom | 80x48 | 15 |
| Small | 320x320 | 400 |
| Medium | 480x480 | 900 |
| Populated cities | 640x384 | 960 |
| Large | 640x640 | 1,600 |

Chunk count is `ceil(width / 16) * ceil(height / 16)`. Doubling both dimensions
quadruples the count, but geometry density and terrain/road complexity also
determine memory and preparation cost.

Include sparse random landscapes and original dense populated cities.

Deliverable: reproducible baseline and a justified default CPU-cache budget.
Do not select a production budget from encoded-buffer estimates alone.

## 2. Separate prepared CPU data from native residency

Define explicit lifecycle states:

`unprepared -> preparing -> CPU-ready -> GPU-staging -> published`

Eviction may return a published chunk to CPU-ready; CPU eviction returns it to
unprepared.

Requirements:

- CPU-ready chunks must not require live Godot nodes, meshes or `SurfaceTool`
  resources.
- Replace retained zone `SurfaceTool` payloads with managed geometry packets.
- Allow object foundations and entrances to sample prepared ground triangles
  without uploading offscreen terrain.
- Preserve exact roofs, overhangs, yards, road triangles and frontmost picking.
- Keep shared dependencies owned and accounted for explicitly.

Do not turn whole-map preparation into whole-map GPU allocation.

## 3. Introduce byte-budgeted retention

Replace the fixed object-count policy with a shared CPU-retention budget covering
object and ground packets.

Policy:

1. Pin visible data and dependencies required for current publication.
2. Reserve bounded space for in-flight work.
3. Retain nearby/directional data next.
4. Use LRU eviction for remaining unpinned data.
5. Suspend background warming when memory pressure or foreground demand requires
   it.

If pinned data exceeds the target, preserve correctness, stop discretionary
warming and report the overage.

For maps that fit, retain all prepared chunks. For maps that do not, retain the
most useful subset. Do not repeatedly prepare and evict the same background
chunks.

Keep GPU residency under a separate limit.

## 4. Add progressive whole-map warming

Use one deduplicated scheduler with priorities:

1. Missing/stale visible chunks.
2. Immediate directional neighbors.
3. Likely destinations or recently visited regions.
4. Remaining map chunks.

Requirements:

- Start the game after visible readiness, not after whole-map preparation.
- Preempt background work between bounded preparation steps.
- Reuse the existing worker limits initially.
- Avoid a full simulation snapshot per chunk.
- Capture consistent bounded work batches; reject stale results.
- Index building/yard ownership once per relevant version rather than repeatedly
  scanning all buildings for every background chunk.
- Keep native operations on the main thread.
- Report warming progress through diagnostics initially; no new popup or
  settings UI is required.

Artwork emission may remain cooperative initially if measurements show it stays
within the host budget. Extract additional pure builders only where necessary.

## 5. Preserve local invalidation and cancellation

Extend existing dependency stamps to retained offscreen data.

Verify:

- Buildings, rotations, yards, terrain and roads invalidate their actual
  dependency neighborhoods.
- Analysis overlays and changing paths cannot leave cached zone geometry stale.
- Ordinary simulation clock changes do not regenerate static artwork.
- Map replacement, load, renderer shutdown and mode transitions cancel or
  invalidate old work.
- Canceled/stale payloads release all resources.
- Visible old geometry remains active until its replacement is complete.
- Invisible queued buildings cannot be selected.
- Save contents, generation rules and simulation state remain unchanged by
  warming.

## 6. Keep selective GPU residency

On camera movement:

- Upload CPU-ready destination chunks without regenerating geometry.
- Retain the current atomic draw/picking publication contract.
- Preserve material batching and bounded upload pages.
- Account for actual transferred bytes, including full-page rewrites.
- Retire distant native pools without discarding useful CPU packets.

Only consider a larger GPU-resident neighborhood after profiling. Whole-world
GPU residency is not part of the initial implementation.

## 7. Add acceptance tests and benchmark coverage

### Correctness

- Fully warm a small map; revisit every region without CPU regeneration absent
  edits.
- Warm a large map under a deliberately small budget; demonstrate bounded
  retention and no preparation/eviction loop.
- Compare fresh versus cached geometry exactly.
- Compare published GPU buffers and picking payloads.
- Confirm a local edit rebuilds only its dependency neighborhood.
- Exercise minimap jumps during warming, rapid direction changes, rotation,
  zoom, placement and off-map dragging.
- Test save/load, map swaps, overlays, paths, bridges, large footprints and
  renderer disposal.
- Verify memory plateaus during repeated traversal and is released on map
  replacement.

### Performance

Keep existing thresholds unchanged:

| Check | Budget |
|---|---:|
| Frame p90 | <60ms |
| Worst frame / input-to-render | <100ms |
| Input handler | <50ms |
| Minimap click-to-ready | <250ms |
| Road preview | <100ms |
| Cached selection/pan/picking | <50ms |
| Cold readiness | <1,500ms |
| Render nodes | <512 |

Measure both cold jumps before warming and warm jumps afterward. Readiness must
include actual visible geometry, uploads and picking, not merely completed CPU
preparation.

Known baseline findings must remain visible:

- SF cold readiness approximately 1,446ms.
- SF minimap readiness approximately 491ms; road preview approximately 120ms.
- Chicago/St. Louis Fast-mode p90 approximately 69-73ms.
- Some small/custom/siege runs have isolated frame overruns.

Do not disguise these by changing fixtures, reducing visual quality or weakening
thresholds.

### Validation entry points

Use the existing test runners and smoke checks documented in the developer
guide. At minimum, cover:

- Core generation, persistence, authored scenarios, snapshot and road-layout
  tests.
- Full perspective geometry/interaction smoke.
- Exact footpath artwork and entrance catalogue checks.
- Interface checks, including narrow windows, and garrison regression checks.
- Perspective performance smoke on large random and dense populated maps.
- Live camera performance on small/medium/large, SF/LA/SD/CHI/STL, custom 80x48
  and the debug-siege fixture.

Inspect graphical captures and separate functional failures, strict performance
failures and near-limit findings in the final report.

## 8. Documentation and handoff completion

Update the developer guide with:

- CPU versus GPU residency contracts.
- Chosen memory budget and measurement rationale.
- Eviction, warming, cancellation and invalidation behavior.
- Measured memory by map size and density.
- Cold versus warm latency results and remaining failures.

Preserve unrelated dirty worktree changes. Do not rebuild underneath an existing
user preview. Follow the project's preview ownership and completion rules.

## Suggested delivery order

1. Memory instrumentation and baseline tests.
2. Managed CPU packets and independent ground sampling.
3. Byte-budgeted retention.
4. Priority scheduler and whole-map warming.
5. Invalidation/lifecycle coverage.
6. Performance matrix, documentation and verified preview.

The renderer stages are sequential prerequisites. Baseline fixture/test
preparation can proceed independently, but final validation depends on all
implementation stages.

Success means: maps retain all prepared geometry when affordable, otherwise
stay within a measured budget; warm navigation avoids unnecessary regeneration;
foreground responsiveness and rendering correctness are preserved.
