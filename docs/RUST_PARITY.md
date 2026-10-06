# C# and Rust parity contract

TermCity contains two supported implementations:

- The original C# application under `src/`, built with .NET.
- The Rust application under `rust/`, built with Cargo.

The implementations provide the same documented gameplay and terminal user experience. They are not required to
generate identical maps from the same seed, and their save files are not interchangeable.

## Compatibility policy

| Surface | Required parity |
|---|---|
| Gameplay rules | Equivalent costs, placement rules, growth, economy, demand, time, reports, milestones, and failure behavior |
| Command line | Equivalent options, defaults, validation categories, exit codes, redirected-I/O guard, and map dump capability |
| Terminal layout | Same minimum size, major regions, information, controls, overlays, and responsive behavior |
| Input | Equivalent keyboard and mouse actions, including platform fallbacks |
| Rendering | Same glyph vocabulary, truecolor palette, layer priority, zoom semantics, and selection/preview meaning |
| Persistence | Equivalent save, load, autosave, undo, atomic-write, and corruption-handling capabilities |
| Exact seeded output | Deterministic within each implementation; cross-implementation identity is not required |
| Save format | Versioned and validated independently; cross-loading is not required |
| Performance | Both must remain responsive on supported map and terminal sizes |
| Platforms | Windows, macOS, and Linux |

## CLI contract

| Behavior | C# evidence | Rust acceptance |
|---|---|---|
| `--seed <n>` | `src/TermCity.App/Program.cs` | Accept any signed 32-bit integer and use a random seed when omitted |
| `--size <size>` | `src/TermCity.Core/Simulation/MapSize.cs` | Support `small`, `medium`, `large`, and `WIDTHxHEIGHT` from 80x24 through 640x384 |
| `--load [file]` | `src/TermCity.App/Program.cs` | Load the implementation's quick-save when no path is supplied |
| `--fps <n>` | `src/TermCity.App/Program.cs` | Accept 5 through 60, defaulting to 30 |
| `--dump-map` | `src/TermCity.App/Program.cs` | Print a seed/dimensions header and the rendered map without requiring an interactive terminal |
| `-h`, `--help`, `/?` | `src/TermCity.App/Program.cs` | Print usage and exit successfully |
| Invalid arguments | `src/TermCity.App/Program.cs` | Print a specific error plus usage and return the invalid-argument exit class |
| Redirected input/output | `src/TermCity.App/Program.cs` | Reject interactive play explicitly while still allowing help and map dumps |

## Simulation and world contract

| Area | Existing C# coverage | Rust acceptance |
|---|---|---|
| Terrain, water, and map generation | `MapGenerationTests`, `WaterTests`, `LargeMapTests` | Generated maps satisfy bounds, terrain, water, highway, gateway, and large-map invariants |
| Roads and connectivity | `RoadTests`, `DiagonalAndPanelTests` | Road masks, bridges, service reach, costs, connectivity, and generated diagonals follow the documented rules |
| Zoning and removal | `GrowthTests`, `DezoneTests` | Designation, delayed removal, occupancy, and cleanup behavior match |
| Economy | `EconomyTests` | Costs, all-or-nothing spending, tax rates, rounding, and weekly income match |
| Growth and demand | `GrowthTests` | Unlocks, capacity, weekly plans, daily shares, diagnostics, and seeded reproducibility within Rust match |
| Clock and speed | `ClockAndSessionTests` | Pause, three speeds, update cap, days, weeks, and reports match |
| Households and population | `GrowthTests`, `PersistenceTests` | Household ranges, weighted generation, population, and continuation after load match |
| Rendering model | `UiTests`, `ZoomTests`, `RoadTests` | Terrain/zone/feature/building/road composition and block sampling match |

## Session contract

| Area | Existing C# coverage | Rust acceptance |
|---|---|---|
| Cursor, camera, and viewport | `ClockAndSessionTests`, `ZoomTests` | Movement, clamping, centering, and coordinate conversion match |
| Selection | `UiTests`, `ZoomTests` | Keyboard/mouse selection and zoom-block snapping match |
| Zoom | `ZoomTests` | Support 0.25x, 0.5x, 1x, and 2x while retaining the focus cell |
| Placement previews | `PlacementConfirmationUiTests`, `SessionFeatureTests` | Quotes, valid/blocked/mixed states, confirmation, cancellation, and straight-road tools match |
| Undo | `SessionFeatureTests` | Store one successful action, enforce elapsed-game-time rules, restore atomically, and pause after the prompt |
| Save/load guards | `SessionFeatureTests`, `SessionFeatureUiTests` | Save/discard/cancel prompts protect changed games and propagate failures |
| Autosave | `SessionFeatureTests` | Use three rotating slots, preserve the newest good save on failure, and retry |
| Guide, reports, milestones | `SessionFeatureTests`, `SessionFeatureUiTests` | Present the same information and persist dismissal/achievement state |
| Edge scrolling | `EdgeScrollTests`, `UiTests` | Match zones, acceleration, frame-rate independence, stale-pointer suppression, and selection scrolling |

## Terminal UI contract

| Surface | Required behavior |
|---|---|
| Main layout | One-row HUD, map, 34-column right panel, one-row message bar, and overlays; minimum terminal size 80x24 |
| HUD | Money, date, seven-day progress, population, pause/speed state |
| Map | Truecolor, single-width Unicode cells, four zoom levels, previews, cursor, selections, and partial redraws |
| Minimap | Whole-map overview with platform-appropriate block rendering and viewport marker |
| Side panel | Zoom controls plus collapsible demand, city, and zone sections |
| Help and prompts | Help overlay, city menu, path entry, progress guards, guide, weekly report, growth report, and undo prompt |
| Placement confirmation | Target-adjacent popup with quote, keyboard traversal, direct Y/N/Esc keys, and mouse buttons |
| Context menu | Right-click actions and prices without duplicate opening on synthesized click sequences |
| Keyboard | Movement, full-screen jumps, selection, actions, speed/pause, zoom, menus, reports, guide, diagnostics, undo, and quit |
| Mouse | Highlight, pan, modified drag-select, wheel scrolling, alternate horizontal scrolling, wheel zoom, buttons, and context menu |
| Resize | Re-layout safely, clamp view/camera/popups, and preserve a valid focus target |
| Event loop | Responsive input polling, edge-scroll ticks, redraw throttling from 5-60 FPS, simulation updates, message expiry, and stall detection |
| Terminal lifecycle | Restore raw mode, alternate screen, cursor, and terminal state on normal exit and errors |

## Persistence contract

Each implementation owns its save identity and default path. An implementation must never silently interpret or overwrite the other implementation's incompatible quick-save.

Both formats must provide:

- An explicit format/version discriminator.
- Complete simulation state, including PRNG state and in-progress weekly growth.
- Palette-based content names so registry ordering does not invalidate a save.
- Compressed binary map layers represented safely in the serialized format.
- Strict length, enum, index, numeric, and semantic validation.
- Atomic replacement through a temporary file in the destination directory.
- Three-slot autosave rotation that preserves the latest good save on failure.
- Whole-game in-memory snapshots for undo.
- Clear errors for corrupt, incomplete, unknown-version, or wrong-implementation files.

## Validation gates

Changes that affect shared gameplay or UX are complete only when:

1. The C# solution builds and all .NET tests pass.
2. The Rust workspace is formatted, passes strict Clippy checks, and all Cargo tests pass.
3. Equivalent scenarios are covered in both native test suites.
4. Headless UI tests cover changed input and rendering behavior in both implementations.
5. Platform-sensitive terminal changes are exercised on Windows, macOS, and Linux.
6. Rendering changes retain acceptable input latency, frame consistency, and terminal output volume on large maps.

The implementations should share requirements and fixtures only when the data is implementation-neutral. They must not introduce a runtime dependency on each other.
