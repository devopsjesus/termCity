# Coastal terrain, bridge, river-flow and rotation report

## Delivered

- Collapsed isolated/sliver coastal hill caps visually without changing logical terrain.
- Softened higher terrace endings near the river and stopped shoreline fragments borrowing taller inland elevations.
- Added solid bridge deck sides matching the rendered top surface.
- Replaced fine railing textures with thicker wood/stone members and regularly spaced posts.
- Made subtle downstream river animation visibly update; retained pause, effects-off and reduced-motion behavior.
- Preserved explicit river identity in new saves so wide rivers are not treated as still lakes.
- Added `[` / `]` for 90-degree left/right rotation.

## Completed verification

- Debug build: passed with no warnings or errors.
- Targeted regression suite: **139 passed**.
- Final launch verification: **40 passed**, a subset of the above coverage.
- Godot graphical integration: passed, including bridge-edge pixel scans, actual deck-side geometry, rotation key events, and before/after river-animation captures.
- River motion check: required visible changes in coarse water patches while limiting brightness changes; pause and reduced-motion checks passed.

| Performance check | Populated SF | Large random map | Limit |
|---|---:|---:|---:|
| Minimap click-to-render | 247.3 ms | 180.7 ms | <250 ms |
| Frame p90 | 28.4 ms | 32.2 ms | <60 ms |
| Cold rebuild | 589.5 ms | 259.2 ms | <1500 ms |

The corrected workspace build was launched. The final verification finished successfully. No further verification runs will be started for this request.

## Remaining concerns

### 1. Populated-map performance has little headroom

The final SF minimap result passed by only **2.7 ms**. Earlier runs occasionally exceeded the limit. Background load, larger cities, or further geometry detail could push it over again. The large random map has substantially more margin.

### 2. Coastal cleanup is a visual heuristic

Singleton/sliver suppression and coastal slope easing passed the constructed fixtures and inspected captures, but not every generated seed or unusual edited coastline was exercised. Tiny islands, narrow headlands, and closely spaced hill/road/water junctions remain worthwhile targets for later inspection.

### 3. Bridge side-face and railing picking need dedicated coverage

Deck-top picking was verified. The new vertical deck-side faces and thicker railings were checked visually, but clicks specifically on those faces were not comprehensively tested. Ground picking primarily follows cached surface triangles; an edge/railing click may resolve to underlying or adjacent ground.

### 4. Older saves do not contain explicit river identity

Older saves remain loadable and use geometric flow inference. Without the new river metadata, unusually wide channels or ambiguous lake/river shapes may be classified differently. Newly generated rivers and new-save round trips have explicit coverage.

### 5. Flow is intentionally subtle and freezes when paused

Motion is low contrast and stops while paused, with effects off, or in reduced-motion mode. At distant zooms it may be difficult to see. This is intentional rather than a failed animation, but the subjective visibility has not been assessed at every camera angle and zoom.

### 6. A transient engine/runtime abort occurred during validation

One repeated engine run aborted in the .NET/Godot finalizer path with an `UnmanagedCallersOnly` error. Subsequent runs passed. Temporary mesh attributes now have explicit disposal, but the abort's cause was not established, and it should not be considered conclusively fixed.

### 7. The full repository suite was not rerun

Verification was focused on affected rendering, terrain, flow, generation and persistence behavior, plus Godot integration and performance. Long playthroughs and the full non-playthrough suite were not rerun for this change.

## Useful captures

- [Updated bridge](coastal-cleanup-verified.bridge.png)
- [Updated riverbank](coastal-cleanup-verified.river-cliff.png)
- [Flow before](coastal-cleanup-verified.flow-before.png)
- [Flow after](coastal-cleanup-verified.flow-after.png)

No commits were created.
