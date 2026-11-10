# Growing-medium-city performance plan

## Goal and scope

Keep the acceptable beginning-settlement experience intact. Find the population
and workload at which a normal medium city becomes noticeably less responsive,
then optimize the measured bottleneck rather than raising every cache limit.

This is a follow-up implementation plan, not approval to change gameplay defaults.
The existing 256 MiB CPU / 128 MiB destination-GPU targets, 1/2-chunk terrain halo,
immediate camera presentation and detailed normal-zoom artwork remain the baseline.
TOP remains the top-end stress fixture; SF is not the normal acceptance target.
See the [existing measurements](DEVELOPMENT.md#top-minimum-spec-baseline-and-recent-region-experiments).

## Phase 1: reproducible medium-city checkpoints

1. Use one seeded **480x480 medium map**, initially seed 42. Preserve its terrain,
   rivers and settlement location throughout the growth sequence.
2. Create checkpoints for the beginning settlement and approximately **10K, 25K
   and 50K residents**, using a deterministic simulation harness that scripts
   ordinary zoning, construction and service expansion while advancing the game.
3. Exercise real costs, tier unlocks, population accounting, jobs, utilities,
   grain, journeys and service delays. Do not force household totals, happiness,
   dispatch capacity or permanent milestones to make a checkpoint pass.
4. Preserve existing developed districts as the town expands. Avoid replacing
   the settlement with a uniform tenement grid or flattening the whole map.
   Include ordinary roads, services, footpaths and representative defenses.
5. Save each checkpoint separately with its seed, population, elapsed days,
   happiness, building/road/path counts and save hash. Record the actual
   population; a near-target checkpoint is acceptable, a false target label is not.
6. Validate deterministic generation and save/load. Bound generation attempts;
   if normal rules prevent reaching a target, report the limiting factor instead
   of bypassing it. Authored stress fixtures must be labeled separately from
   genuine growth checkpoints.

**Deliverable:** reproducible snapshots and a small fixture-generation/test
harness. Never overwrite the player's quick-save or modify source snapshots.

## Phase 2: baseline the actual player workloads

Reuse [RecentRegionSmoke](../godot/RecentRegionSmoke.cs) and
[MinimapTraversalSmoke](../godot/MinimapTraversalSmoke.cs), rather than creating a
second timing framework.

These runners now accept an **explicit snapshot path**, with option-isolation
tests; implicit quick-save loading and conflicting smoke/starter modes remain
rejected. Reload the same snapshot before each run and normalize renderer
initialization before sampling. Use `--benchmark-zoom normal|overview` in
separate processes for matched starting states.

Execution findings and outstanding population coverage are recorded in the
[benchmark report](MEDIUM_CITY_BENCHMARK_RESULTS.md). The generation and
measurement tools do not imply that all requested population targets were reached.

For each checkpoint, measure:

| Workload | Execution |
| --- | --- |
| Local exploration and return | Existing two-screen out/back route, twice; paused |
| Distant minimap navigation | Existing 24-jump route; paused half and live Medium-speed half |
| Zoom | Normal and maximum zoom-out; include transition correctness checks |
| Presentation | Actual fullscreen on the 8 GiB M2 reference Mac |

Log requested and actual camera displacement. Medium-map overview routes may
clip; choose the longest feasible route, flag clipping and do not label it as
an unrestricted two-screen test.

Separate first visible response, terrain readiness and complete destination
readiness. Record:

- Frame/input latency p90, p95 and worst; cold versus warm returns.
- Visible-owner regeneration and native reuploads, separately from halo work.
- Object, roof, terrain and path upload bytes and time; whole-buffer versus
  changed-instance work; pool capacity, live slots and fragmentation.
- CPU preparation, retirement/compaction and simulation/HUD costs.
- Active and resident nodes separately, without changing a runner's criterion.
- Accounted cache bytes, managed heap, process memory, driver allocations and
  memory pressure. Do not sum overlapping unified-memory counters.
- Map, panel and render dimensions, antialiasing, build/source identity and
  actual simulation progress.

Perform one discovery pass across the four stages. At the first meaningful
regression, run three matched baseline trials. Keep the original frame,
readiness, input and node budgets unchanged; report failures explicitly.
In particular, retain 250ms destination readiness, 60ms frame/first-draw p90,
100ms worst-frame checks and the traversal's 50ms input-handler limit. Keep
the existing 512-node checks and their active-versus-total distinction.
Headless checks establish correctness, not rendered-frame performance.

**Decision gate:** if ordinary medium-city growth remains responsive, stop here.
Deliver the measurements and regression coverage without changing defaults.

## Phase 3: reduce native object upload churn, if indicated

Proceed only if Phase 2 identifies object publication/upload as a material cost.
Start in [ObjectUploads](../godot/PerspectiveCity.ObjectUploads.cs) and
[GPU residency](../godot/PerspectiveCity.GpuResidency.cs).

1. Trace why a return uploads unchanged instances: eviction, slot relocation,
   pool growth, compaction, invalidation or unconditional whole-buffer replay.
2. Preserve valid native slots and backing buffers on unchanged returns. Publish
   only genuinely new/changed geometry, retaining exact ownership and picking.
3. Verify the actual Godot GL Compatibility APIs before promising partial-buffer
   updates. Compare supported per-instance updates against existing bulk upload.
   If partial updates are unsuitable, test smaller native object pages with
   unchanged 16-cell CPU ownership chunks and existing material batching.
4. Avoid global compaction/reallocation during navigation; compact only when
   measured waste warrants it and the existing navigation cooldown permits it.
5. Keep this as an isolated experiment until it improves the affected workload.
   Do not bundle it with larger budgets, terrain-page changes or LOD.
   Match all non-target presentation/retention flags; an older bundled cache
   experiment is not a clean single-variable comparison.

For unchanged chunks that remain resident and within budget, require identity
reuse and no redundant native upload. Distinguish legitimate simulation edits
and pressure-driven eviction from avoidable replay.

**Candidate target:** at least 30% less repeated object upload volume and 15%
better readiness p90 across three matched trials. These are selection targets,
not replacements for existing absolute budgets. Reject a consistent greater
than 5% regression in the beginning settlement or normal-zoom frame p90.
Memory targets remain unchanged; any increase needs a separate measured decision.

## Phase 4: evaluate overview LOD independently

Compare the baseline with existing
[OverviewLod](../godot/PerspectiveCity.OverviewLod.cs) at the affected checkpoint,
using the same memory targets and renderer settings.

- Measure overview latency, uploads, memory and frame tails.
- Inspect real captures, four camera rotations and detailed/overview/detailed
  transitions. Verify roofs/overhangs, frontmost picking and construction previews.
- Keep services, foundations, entrances and gameplay unchanged.
- Preserve full detail at normal/intermediate zoom and the terminal interface.
- Reject distracting transition flashes, missing objects, quality loss or a
  normal-zoom regression.

If it wins, retain an explicit opt-in first. Default adoption is a separate
decision based on medium-city evidence and visual review, not TOP alone.

## Phase 5: confirm, document and stop

For a winning candidate, repeat the affected checkpoint and 50K checkpoint on
seed 42, then confirm with one alternate seed. Check live Slow/Medium/Fast
operation, daily/weekly updates and continued footpath publication at the
affected stage. Use TOP for a final regression comparison, not as the sole gate.

Run focused simulation/persistence/renderer tests and graphical geometry,
rotation, zoom, picking, preview and narrow-terminal checks. Preserve original
road meshes, terrain grading, native panel sizing, supersampling and MSAA.
Do not fix unrelated failures in this performance change.

Publish a comparison report with passed checks, failed checks, near-limit
findings, memory costs and a clear adopt/keep-opt-in/reject recommendation.
When correctness passes and remaining performance is merely near a budget,
finish and disclose it rather than repeatedly chasing small gains.

Keep intermediate work silent. Preserve an existing game window; do not replace
its running binaries or launch competing graphical measurements. Coordinate
any required preview restart before final graphical validation.

## Out of scope

- Blanket cache increases or rendering the entire map.
- Lowering resolution/antialiasing or weakening benchmarks to obtain a pass.
- Simulation shortcuts, hard service radii or road connectivity gates.
- A renderer/backend rewrite without evidence that narrower fixes are inadequate.
- The unrelated Oil cauldron menu assertion, which belongs in a separate fix.
