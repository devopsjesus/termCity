# Medium-city benchmark execution

## Scope and current coverage

Executed on the 8 GiB Apple M2 reference workstation with normal 256 MiB CPU /
128 MiB destination-GPU targets. Gameplay defaults, resolution and antialiasing
are unchanged.

The ordinary-rule generator uses actual founding, paid services/defenses,
connected gated expansion, daily population changes and combat. It does not
seed a requested population or force contentment. Early controller issues
(gate-aware expansion, connecting the outer enclosure, placement validation
and recruitment while paused) were corrected before accepting later fixtures.

**Coverage gap:** the genuine growth attempts have not yet produced the requested
10K/25K/50K checkpoints. Valid checkpoints currently cover the beginning,
100/250/500 milestones and approximately 760 residents. These must not be
presented as a successful high-population progression.

Growing the connected settlement exposed a severe garrison path-search fanout:
an archer assignment repeatedly searched for paths to many enemy/wall-post
combinations. A managed process stack identified that operation. Bounded
generation preserves completed snapshots and reports missing targets rather
than manufacturing them.

## Measurement controls

- Separate processes reload the same read-only snapshot for each zoom/case.
- Fullscreen window: 2940x1846; native map panel: 2530x1748.
- Render target: 5060x3496, with existing 8x MSAA.
- Local route: 48 steps, out/back twice, paused.
- Minimap route: 24 clicks, paused half and live Medium-starting-speed half.
- Ordinary wave-driven speed changes remain enabled and logged.
- Input snapshot hashes are checked after measurement.
- Overview requests 772 cells on a 480-cell map; actual travel is 360 cells and
  is explicitly marked clipped.
- An initial 1440x900 pre-layout trial was discarded. The runners now require
  physical fullscreen dimensions, not only a fullscreen mode flag.

## Initial baseline: local revisit

These figures precede the batched garrison-path optimization and any upload/LOD
candidate. Population labels identify actual checkpoint coverage, not 10K tiers.

| Checkpoint | Zoom | Readiness p90 / worst | First frame p90 | Second-pass readiness p90 | Repeated terrain uploads |
| --- | --- | ---: | ---: | ---: | ---: |
| Beginning | Normal | 89.3 / 280.8ms | 51.1ms | 82.0ms | 185 |
| Beginning | Overview | 1,029.8 / 3,212.4ms | 54.3ms | 395.2ms | 364 |
| ~500 | Normal | 498.5 / 596.3ms | 63.3ms | 486.1ms | 204 |
| ~500 | Overview | 1,507.2 / 3,916.5ms | 69.0ms | 866.2ms | 364 |
| ~760 | Normal | 610.9 / 1,109.9ms | 67.8ms | 608.9ms | 204 |
| ~760 | Overview | 1,869.9 / 4,542.8ms | 75.1ms | 1,259.1ms | 364 |

Terrain counters include buffered/halo publication. Required-owner identity
counters and object buffer-write profiles are recorded separately. No row is an
all-budgets pass: cold/worst-frame, readiness or scene-node checks fail.

## Initial baseline: minimap jumps

| Checkpoint | Zoom | Response p90 | Readiness p90 / worst | Frame p90 / worst |
| --- | --- | ---: | ---: | ---: |
| Beginning | Normal | 27.3ms | 374.0 / 540.9ms | 79.3 / 133.8ms |
| Beginning | Overview | 66.7ms | 3,966.9 / 6,996.5ms | 54.5 / 957.1ms |
| ~500 | Normal | 38.2ms | 713.4 / 745.4ms | 74.9 / 174.7ms |
| ~500 | Overview | 73.9ms | 6,275.1 / 15,838.3ms | 56.5 / 1,091.6ms |
| ~760 | Normal | 40.8ms | 921.1 / 2,045.5ms | 95.2 / 230.9ms |
| ~760 | Overview | Incomplete | Drawing stopped at jump 14 | No aggregate claimed |

The immediate response is substantially faster than full destination readiness.
The incomplete live overview is a correctness failure, not a near-budget result.
Node thresholds and the distinction between active and retained nodes remain
unchanged.

## Cost attribution and candidate gates

Beginning overview minimap traversal recorded approximately 8,156ms CPU ground
preparation and 4,462ms object publication time across the run. Instance upload
payload was about 812 MB; terrain native upload counters reached about 1.93 GB.
The final object backing buffers were only about 4.4 MB, with substantial spare
capacity and holes. Replaying those buffers repeatedly is therefore worth an
isolated trial, but terrain remains a major cost.

The `AdaptiveUploads` diagnostic chooses existing per-instance APIs rather than
whole-buffer replay when a chunk changes less than one quarter of a pool.
All non-target Tuned flags and cache targets remain identical. Reported payload
bytes describe API data, not measured driver bus traffic; real rendered latency
and exact transform/color validation are required before judging the trial.

Overview LOD is evaluated separately under the same targets. Neither candidate
is a default change. Adoption requires matched repeats, preserved normal-zoom
behavior and meaningful gains; high-population certification remains blocked
until authentic higher checkpoints exist.

## Validated paired overview trials

Three baseline/candidate repetitions used the same initial medium snapshot and
2940x1846 full-display borderless maximized window. The native panel and render
target remained 2530x1748 and 5060x3496. Native macOS fullscreen transitions
occasionally restored the old 1440x900 dimensions; those preliminary paired
trials were discarded. Sizing is now checked before and during sampling.
The borderless diagnostic presentation is not a change to normal game windows.

| Policy | Median readiness p90 | Median second-pass instance payload | Median first-frame p90 |
| --- | ---: | ---: | ---: |
| Tuned baseline | 1,159.2ms | 46.34 MiB | 57.8ms |
| AdaptiveUploads | 1,087.1ms | 46.43 MiB | 58.8ms |
| OverviewLod | 1,122.9ms | 23.01 MiB | 58.9ms |

Adaptive uploads improved median readiness by about 6.2%, but did not reduce
repeated payload volume. It fails both selection targets and is **not adopted**.
Overview LOD halved instance payload, but improved median readiness only about
3.1%. It remains opt-in; this does not justify default adoption.
Terrain uploads and the large terrain-node population remain dominant.
All runs retained the original strict budget failures; none is an acceptance pass.

## Genuine-growth and garrison follow-up

The completed bounded resumed run reached day 1,022 and a peak of **858**
residents, starting from a genuine saved settlement. It explicitly returned
incomplete population coverage. No 10K/25K/50K files are manufactured or claimed.
The periodic saved progress allows later continuation without restarting growth.

The diagnosed garrison fanout was addressed separately from renderer trials:
single-target and multi-goal searches now share traversal logic, wall-post
filtering precedes scoring, and one batched search preserves enemy/post
priorities and occupied-endpoint rules. Targeted path/assignment/garrison
validation passed 72 tests. Exact indexed wall lookup matches the original
full-scan result across rotations/materials and keeps existing cell-map priority.
This removes repeated search work without altering population, combat rules,
movement permissions or simulation time.

The post-fix live ~760-resident overview completed all 24 minimap jumps and
passed native-buffer/fixture/quality correctness checks. It advanced 34.988
simulation days. Performance is still poor: response p90 83.7ms, readiness p90
24,409.7ms, worst readiness 90,943.8ms, frame p90 356.3ms and worst frame
2,793.9ms. This is not a performance pass, and there is no comparable complete
pre-fix aggregate from the earlier stopped-drawing run.

## Recommendation and limits

Keep the current gameplay cache targets and existing overview opt-in. Do not
adopt adaptive uploads or enable overview LOD by default on these results:
neither met the paired readiness improvement gate. Further terrain residency
and garrison CPU work is a separate follow-up, not an excuse to raise every
memory limit or weaken the benchmark.

The planned high-population confirmation and alternate-seed promotion checks
were not used to approve a winner: there was no winning candidate, and authentic
10K/25K/50K coverage remains missing. This report records bounded execution and
that coverage limitation, not a completed high-population certification.

## Reproduction

See the [implementation plan](MEDIUM_CITY_PERFORMANCE_PLAN.md) and
[checkpoint/diagnostic commands](DEVELOPMENT.md#medium-growth-checkpoint-tools).
Generated files belong in a separate output directory, never the player's
quick-save.

## Final verification

The changed-surface test run passed 164 tests with six retired glyph/painted-road
tests explicitly skipped. Garrison/path/lookup focused runs also passed.
Full-display captures of the baseline, LOD trial and live grown settlement were
inspected; native-buffer correctness and LOD transition/picking/preview checks
passed in the measurement runs.

The separate graphical wall-network smoke passed water-wall/footbridge checks
but failed its diagonal-selection/no-rebuild assertion. Repeating that smoke
with the original wall lookup produced the same failure; its paused fixture
has no garrison actors. It is therefore recorded as an unrelated existing
selection/render-rebuild issue, not patched or represented as passing.
