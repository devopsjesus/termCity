# 60fps implementation plan

## Scope and invariants

Target sustained 16.7ms rendered frames and simulation throughput without growing
backlog on the genuine 858-resident save. Keep the original checkpoint read-only,
combat priorities/collisions/tick boundaries deterministic, picking matched to
rendered geometry, and terrain scrolling/residency unchanged. Orc-study artwork
and its standalone preview are out of scope.

Default quality remains native-panel 2x supersampling and 8x MSAA. Native-panel
8x/4x MSAA comparisons are diagnostic opt-ins, not a new default or half-resolution
upscaling.

## Ordered changes

1. Record matched frame IDs, viewport render CPU/GPU, host work, actor/wall work,
   allocation/GC and simulation backlog. Capture a before/after run from the
   same checkpoint and camera.
2. Cache complete authored fortification artifacts by owner. Regenerate changed
   walls and dependent ladders/joins; reuse unaffected geometry and picking.
3. Retain original actor model/part output, update changed poses, and spatially
   page native siege batches so off-screen geometry can be culled. Keep bounded
   node/capacity budgets and actual native-buffer checks.
4. Replace dictionary/set BFS scratch with reusable generation-stamped integer
   node storage and queues. Preserve resumable multi-goal searches, occupied
   endpoints, ground/wall levels and neighbor tie order against the frozen oracle.
5. Publish gameplay before optional path/yard preparation. Transfer immutable,
   versioned presentation packets separately and discard obsolete packets without
   dropping simulation input or replacing the last complete scene.
6. Add opt-in quality comparisons with identical native geometry, capture and
   CPU/GPU timing. Adopt no quality change without image/interaction validation.

## Verification

- Frozen path oracle and serial/worker save parity, edits, pause/speed/wave events,
  actor identities, persistence and stale presentation packets.
- Graphical wall/native-buffer parity, rotation, zoom, actual picking, previews,
  terrain continuity and narrow terminal UI.
- Original saved-combat cadence and backlog; large random-map actual minimap
  latency and existing perspective performance budgets.
- Report p50/p90/p95/p99, worst frames, supported GPU timing, allocations/GC,
  local regeneration and upload counts. Preserve existing budget failures and
  report added 60fps targets separately.
- Reopen only the latest verified Debug preview. Do not rebuild underneath it.

## Results

Implemented the ordered pass:

- Matched viewport CPU/GPU APIs, frame IDs, wall assembly/page/top phases, actor
  work, allocation/GC and backlog. GPU timings returned zero on both local
  backends and are explicitly marked unavailable, not interpreted as zero cost.
- Cached full authored wall artifacts, stable picking/highlights and rebased
  weapon slots. Damage regenerates 1-5 owners rather than all 968. Exposed tops
  use a persistent spatial dependency index; unchanged top meshes are retained.
- Spatial 32-cell native pages retain capacity, exact transforms and original
  pixel materials. Original actor rigs retain local part output off screen;
  visible joints refresh before entering the panel. Picking uses the actual
  transformed original parts. No orc artwork or study files changed.
- Integer BFS workspaces retain visited generations, predecessors and queue
  arrays. Searches own exclusive leases, including interleaved/resumable cases,
  and return storage on disposal. Frozen neighbor/goal/occupancy rules pass.
- Gameplay publication no longer waits for path/yard layout construction.
  Isolated versioned preparation has one active task and coalesces pending
  requests. Stale results are counted/rejected; incoming packets are consumed
  once. Previously committed scenes remain visible. Paused/unfocused pending
  work still publishes without enqueuing new elapsed time.
- Diagnostic-only `--render-quality` supports Reference, Native8x and Native4x
  with normal defaults untouched. Matching paused baseline captures are saved
  alongside cadence captures.

### Measurements

Single bounded seed/checkpoint comparisons on the Apple M2, normal zoom:

| Case | p50 | p95 | p99 | Worst | Allocated globally / 15s |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before, Reference | 22.8ms | 60.2ms | 72.7ms | 95.3ms | 5.96GB |
| Local updates, Reference | 17.9ms | 40.3ms | 50.6ms | 81.0ms | 2.99GB |
| Final isolated validation, Reference | 18.2ms | 41.2ms | 49.1ms | 67.6ms | 2.93GB |
| Intermediate opt-in Native8x | 15.3ms | 35.8ms | 51.7ms | 68.9ms | 3.49GB |
| Intermediate opt-in Native4x | 15.8ms | 36.1ms | 50.5ms | 69.3ms | 3.45GB |
| Local updates, opt-in Metal/Mobile Reference | 15.9ms | 35.6ms | 46.5ms | 67.7ms | 3.07GB |

The native-AA trials preceded the final local top-dependency index and are not
presented as a statistically paired final ranking. Allocations/GC are global,
including simulation and background preparation, not solely renderer allocation.
Reference slow-frame wall work dropped from roughly 30-38ms/all 968 owners to
roughly 10-23ms/1-5 owners; actor worst dropped from 8.9ms to 4.6ms in the matched
reference run. Final validation stopped with 3.421 seconds of backlog and drained
in 6.022 seconds without retries. **The save does not yet sustain 60fps or
real-time simulation.**
Added p95/p99 targets and original budgets remain enforced; failed runs exit
with their failure status.

Metal/Mobile reduced rendering CPU time but failed image parity: terminal text
spacing and lighting visibly differed. It is not adopted. Native AA remains
opt-in; Reference is still the interactive default. Baseline images were
inspected for original terrain/buildings, closed terminal frames and small detail.

Final build passed with no warnings; 268 targeted tests and the isolated full
seed-42 headless settlement smoke passed. Large seed-42 graphical performance
passed: 386.6ms cold, 22.3ms frame p90, 174.8ms actual minimap click-to-render,
279 nodes. The final isolated graphical garrison smoke passed, including original
poses, walking, ladders, rotation, zoom, commands and picking. Its 120-goblin
stress measured 34.9ms p90, 5.4ms actor worst and 17.5ms simulation worst.
An earlier graphical stress run failed at 88.1ms p90; these are bounded,
variable-load workstation results, not a guarantee of stable cadence.

Final graphical wall tests passed actual native buffers, local/full top parity,
damage, weapons, previews, rotation, zoom and exact picking. The overall command
still exited with a performance failure: short-wall click-to-render was 106.0ms
empty and 200.8ms populated incremental (287.5ms full control), against 100ms.
Those misses and the saved-city 25ms p95 / 50ms p99 combined target remain failures;
no thresholds were weakened. GPU timing was unavailable on both tested backends.

Daily map publication still copies the mutable map, and preparing a changed
path scene still captures an isolated checkpoint on the worker. Changed-cell
publication journaling was not implemented. Remaining measured costs include
regrouping/uploading wall pages, changed-color top meshes and simulation
throughput; the owner cache is not a claim that every upload is now local.

### Reproduction

```bash
dotnet build TermCity.slnx
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --camera-performance-test --capture /tmp/reference.png
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --camera-performance-test --render-quality Native8x --capture /tmp/native8.png
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --camera-performance-test --render-quality Native4x --capture /tmp/native4.png
godot --audio-driver Dummy --path godot -- --seed 42 --size large --perspective-performance-test
```

Quality comparisons require a Debug camera diagnostic and do not write preferences
or the original checkpoint. The engine's optional `--rendering-method mobile
--rendering-driver metal` switches are exploratory only, with the parity rejection
above. Existing terrain residency, buffering and camera/placement rules remain.

## Remaining-stutter investigation (November 8, 2026)

The original read-only checkpoint still hashes to
`11904fbead5905bd02ee4a48c9e12e9e33428d389b01aca49844e41830b6ae8b`.
All runs below used Reference quality, the same initial camera and the actual
858-resident/73-attacker/24-defender/968-wall fixture. No combat rules, elapsed
input ordering, artwork, terrain retention targets or rendering quality changed.

### Measured fixes

- Wall-top native publication now submits complete position/normal/UV arrays,
  rather than making three `SurfaceTool` calls per vertex. Exact clipped geometry
  and winding are retained; graphical validation checks the native attributes
  against the CPU packet before comparing local clipping with the full oracle.
- Siege pages retain immutable model bounds and published counts. Unchanged
  pages no longer recompute every transformed bound or reset native visibility
  counts on every sync. Bounds still update for changed transforms/counts.
  Typical wall-page grouping fell from approximately 3.9-4.3ms to 0.6ms;
  extension pages fell from about 3.6ms to 0.6-0.8ms.
- Daily civilian bridge planning rejected inland cells before expensive enclosure
  tests. Its measured cost fell from 74-76ms to 12-14ms per day. Cell/neighbor tie
  order, active construction, enclosure, water and crossing rules are unchanged;
  five frozen exhaustive-selection cases cover narrow/wide rivers and a breach.

### Sustained measurements

| Run | Seconds | Frames | p50 | p95 | p99 | Worst | Frames >50ms | Backlog at stop / drain |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Instrumented baseline | 45 | 2,148 | 18.9ms | 38.1ms | 47.8ms | 116.7ms | 18 | 12.289s / 12.462s |
| Wall fix | 45 | 2,407 | 17.6ms | 29.2ms | 35.8ms | 65.8ms | 4 | 9.463s / 8.629s |
| Wall + bridge fix | 45 | 2,355 | 18.0ms | 30.2ms | 39.4ms | 104.7ms | 4 | 7.811s / 6.497s |
| Extended final observation | 90 | 4,904 | 17.9ms | 28.3ms | 35.5ms | 100.3ms | 7 | 0.018s / 0.167s |

These are individual workstation observations, not guaranteed improvements or
statistically paired simulation workloads: reduced stalls change elapsed input
slices and therefore publication frequency. The 90-second run is not a matched
45-second comparison. Its backlog peaked at **6.933s**; the near-zero ending
backlog followed the changing combat workload and does not establish real-time
combat throughput. The final 45-second worst frame correlated with a **59.24ms
map publication** and two global gen-0 collections; that correlation alone does
not separate copying from GC/scheduling delay.

The baseline's largest matched event had 62.92ms of top publication and a
116.71ms frame. The wall-fix run also observed a 20.12ms top-publication outlier,
so bulk submission is not a promise that uploads never stall.

### Remaining causes, separated

- **Rendered frames:** wall rebuilds still affect hundreds of frames per run.
  The 90-second sample had 581 wall frames, 1,035 frames with global GC activity,
  and 15.38GB of global allocations. Rendering CPU p95 was 2.07ms; GPU timing
  remained unavailable. Global allocation/GC totals include worker preparation
  and diagnostic sampling, not just the renderer.
- **Simulation/publication:** the final long run recorded a 1,778ms daily update,
  a 387ms weekly population pass, roughly 58-97ms travel-field repairs and two
  later analysis events around 607-608ms. Initial patrol assignment was about
  193ms. Isolated worker presentation captures were typically 4-13ms; host reset
  captures were zero during sampling. Daily map copying remains implemented;
  changed-cell journaling was not introduced. The one sampled autosave captured
  its isolated snapshot in **6.94ms**; it was not the dominant measured event.
- **Camera preparation:** the matched paused fullscreen 48-step normal route
  still had **zero repeated terrain uploads and zero required terrain
  replacements**, but 184 required object republications, 23,767,232 repeated
  object bytes and 277,073,852 path-upload bytes. Readiness p90/worst was
  **643.6/717.8ms**; first-frame p90/worst was **58.2/195.8ms**. The paused save,
  native buffers and terrain continuity passed. This remains an object/path
  preparation/publication problem, not evidence for restoring terrain eviction.

### Validation and explicit failures

Build passed with zero warnings/errors. Focused tests passed **256**, with six
explicitly retired cases skipped. Graphical wall/native top parity, damage,
rotation, three zooms, picking and preview checks passed. Graphical garrison and
perspective suites passed; captures of the live city and original wall/building
artwork were inspected. The perspective suite was run **without capture**; the
earlier close-zoom ripple capture failure is not claimed fixed.

Large seed-42 performance passed: cold 367.7ms, frame p90 23.2ms, buffered pan
1.7ms, actual minimap click-to-render 177.7ms and 322 nodes. Camera performance
varies; this does not erase the prior 302.6ms minimap failure.

Still failing: saved combat's **25ms p95** target (final 28.3ms, +3.3ms), its
**100ms worst-frame** budget (final 100.3ms, +0.3ms), and the long run's moving
presentation-hold target (**85/316 eligible frames**, versus 5%). The paused
route's **250ms readiness** and **100ms worst first-frame** budgets still fail.
Populated incremental wall purchase remained **171.1ms** versus 100ms (full
control 249.0ms); empty incremental purchase passed at 94.5ms. These commands
retain their nonzero failure exits. **Sustained 60fps and real-time combat are
not achieved.**

The camera diagnostic now defaults to a 45-second saved-combat sample, prints
the actual slowest matched rendered frames and event frequencies, and separates
wall extension/clipping/upload, host publication/reset capture, worker
siege/day/analysis/navigation/checkpoint, path wear and autosave capture.
Sampling duration excludes draining and final capture.

```bash
godot --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --camera-performance-test --camera-profile-seconds 90 --capture /tmp/stutter.png
godot --maximized --borderless --audio-driver Dummy --path godot -- --load /absolute/checkpoint.json --recent-region-test --benchmark-zoom normal --cache-experiment Tuned
```

`--camera-profile-seconds` accepts 5-300 seconds only with a loaded-save camera
diagnostic. Run heavy graphical benchmarks sequentially, without other previews
or builds. Several initial attempts failed readiness/render watchdogs; those
attempts are not included as completed measurements and no watchdog was relaxed.

## Simulation efficiency follow-up (November 9, 2026)

This pass prioritized live gameplay, using a fresh baseline rather than assuming
the earlier workload was unchanged. All improvements are active in normal play.
Rendering quality, actor artwork, combat decisions/timing, ordered elapsed
inputs, terrain retention and camera publication policies are unchanged.

### Profile and implementation

A deterministic Debug replay loaded the original checkpoint, selected Medium
speed, and supplied exactly 2,700 updates of `1d / 60`. At each day boundary it
evaluated Stats, Services, Indicators and Finance, like the worker. Runtime
allocation ticks identified large integer/double arrays and a defender-search
closure allocated per explored edge. Managed CPU sampling confirmed defender
searches as the dominant simulation cost. Allocation ticks are sampled type
attribution; exact totals below use current-thread allocation counters.

- Defender body checks and ground-blocking scans no longer create captured
  delegates in hot loops. Collision predicates, thresholds and iteration order
  are preserved.
- Defender edge/interior caches use integer cell/node keys, matching the existing
  integer BFS workspace. Already-visited nodes are rejected before interior
  lookups. Cache invalidation for terrain, navigation and wall changes remains.
- Guards share one lazy, disposable multi-target search per assignment rather
  than restarting a single-target traversal for each candidate. Target ranking,
  melee checks, retained targets and occupied endpoints remain unchanged.
- Travel repair checks for a strict endpoint improvement before copying a field.
  A no-op keeps the prior immutable field. Large repairs reset their already
  allocated destination instead of cloning it a second time; temporary tree,
  generation and pending arrays are leased from the shared array pools and
  returned in `finally`. Published route arrays are **never** pooled or mutated
  after publication. Padded rented-array tails are never treated as map cells.
- Growth-site masks reject occupied/water/road/path cells before wall-distance
  and enclosure work. No cache invalidation rule or development ranking changed.

An alternative sorting-first archer search was measured, found slower, and
discarded. This is not a broad cache/pooling rewrite or changed-cell journaling.

### Fixed-input comparison

| Measure | Fresh baseline | Final optimized |
| --- | ---: | ---: |
| Replay runtime | 46,932ms | 41,066ms |
| Current-thread allocations | 7,141,221,848 bytes | 3,582,146,536 bytes |
| Expensive day at slice 791 | 1,738ms | 1,289ms |

That is approximately **12.5% less runtime and 49.8% fewer allocations** in this
bounded replay. The final serialized games were byte-for-byte identical, with
SHA-256 `d3e9befbcf6494a4bf4225632c80f1b8fecb409f881ada035925c4a70ca5b60c`.
This verifies the measured trajectory, including RNG/combat state; it is not a
universal speedup guarantee. The original source checkpoint retained its original
SHA-256. CPU sampling was a separate run and is not included in these timings.

### Live gameplay comparison

Matched 45-second rendered runs used Reference quality and the same original
checkpoint/camera. Different render cadence changes input slice boundaries and
publication frequency, so they are representative live observations, not the
fixed-input parity experiment above.

| Measure | Fresh baseline | Optimized |
| --- | ---: | ---: |
| p50 / p95 / p99 | 17.8 / 31.7 / 39.9ms | 18.3 / 30.6 / 37.4ms |
| Worst frame | 104.1ms | 72.0ms |
| Global allocations | 9.80GB | 7.26GB |
| Frames with GC activity | 596 | 481 |
| Backlog at stop | 7.968s | 3.208s |
| Backlog drain | 9,178ms | 3,401ms |
| Frames above 50ms | 5 | 3 |

The final isolated 90-second run recorded p50/p95/p99 **18.0/30.9/37.9ms**,
worst **79.4ms**, **2.575s peak backlog**, 0.017s ending backlog and 344ms drain.
One autosave capture cost 5.96ms. Global allocation was 12.57GB over 90 seconds;
this includes rendering, preparation and diagnostic sampling, not just core
simulation. GPU timing was unavailable; no GPU bottleneck attribution is made.

### Validation and remaining work

Build passed with zero warnings/errors. **210 distinct focused cases passed**,
including frozen path/guard-target oracles, collision parity, deterministic
serial/worker snapshots, movement/body blocking, growth masks, service snapshots,
travel-field immutability and parallel repair parity. Added allocation tests
bound eight warmed defender searches to 64KiB and a no-op travel repair to
16KiB. Large-influence repair also passes on 161x97 maps, exercising pooled
arrays larger than the logical map and repeated reuse.

Graphical garrison validation passed animation, commands, picking, rotation and
zoom. Live captures were inspected. Large seed-42 performance passed: 339.3ms
cold readiness, 23.2ms frame p90, 2.2ms buffered pan, **172.1ms actual minimap
click-to-render** versus 250ms, and 322 nodes. No renderer/UI changes or quality
reductions were required.

**Remaining failures are explicit:** live p95 is still 30.9ms against 25ms
(5.9ms over); the 90-second moving-presentation hold check recorded **47/502
eligible frames (9.4%)** against 5%. Its benchmark exits nonzero. The 45-second
run had zero held frames out of 33 eligible. Original 60ms p90 / 100ms worst
frame budgets passed in both optimized runs, but this is **not sustained 60fps
or guaranteed real-time combat**. Daily growth, route repair and defender
searches still consume meaningful worker time. Paused-city object/path upload
costs and previously disclosed capture-water failures were not addressed or
reclassified as passes.
